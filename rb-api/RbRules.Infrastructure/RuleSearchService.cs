using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record RuleSearchHit(
    long Id, string SourceId, string SectionCode, int? Page, string Snippet, string? FileUrl);

/// <summary>Hybride zoeken voor de regels-browser (#72): vector-zoek op de
/// sectie-embeddings + Postgres full-text over dezelfde chunks, gefuseerd met
/// RRF. De RRF-fusie is hier bewust minimaal gedupliceerd uit AskService —
/// extractie naar één gedeelde helper volgt via #59. Ollama-uitval is een
/// verwacht pad: dan degradeert het zoeken naar alleen-FTS.</summary>
public class RuleSearchService(
    RbRulesDbContext db, EmbeddingService embeddings, ILogger<RuleSearchService> logger)
{
    private const int RrfK = 60;
    private const int SnippetChars = 180;

    /// <summary>Cap op de embed-call: een publieke zoekopdracht mag nooit
    /// minutenlang aan een koude/haperende Ollama hangen (de gedeelde
    /// HttpClient-timeout is ruim vanwege batch-embeds).</summary>
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<RuleSearchHit>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        // Ruim ophalen per kanaal, zodat de fusie en de sectie-dedupe
        // hieronder nog wat te kiezen hebben.
        var fetch = Math.Max(limit * 2, 20);

        // 1. Vector-kanaal — best-effort: bij Ollama-uitval alleen-FTS.
        List<long> vectorIds = [];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EmbedTimeout);
            var qv = await embeddings.EmbedOneAsync(query, cts.Token);
            vectorIds = await SectionChunks()
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // de aanvrager zelf haakte af — niet maskeren
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Embedding voor regels-zoek mislukt — degradatie naar alleen-FTS");
        }

        // 2. Full-text-kanaal (Engels — de bronnen zijn Engels).
        var textIds = await SectionChunks()
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // 3. RRF-fusie — zelfde vorm als AskService, zonder bron-bias (#59).
        var scores = new Dictionary<long, double>();
        void Accumulate(List<long> ids)
        {
            for (var rank = 0; rank < ids.Count; rank++)
                scores[ids[rank]] = scores.GetValueOrDefault(ids[rank]) + 1.0 / (RrfK + rank + 1);
        }
        Accumulate(vectorIds);
        Accumulate(textIds);

        var rankedIds = scores
            .OrderByDescending(kv => kv.Value)
            .Take(fetch)
            .Select(kv => kv.Key)
            .ToList();
        if (rankedIds.Count == 0) return [];

        // Projectie zonder embeddings; net genoeg tekst voor het snippet.
        // (De afkap zelf gebeurt in-memory via TextUtils.Snippet — eigen
        // methodes vertalen niet in een expression tree.)
        var rows = await db.RuleChunks.AsNoTracking()
            .Where(c => rankedIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.SourceId, c.SectionCode, c.Page, c.DocumentId,
                Text = c.Text.Substring(0, Math.Min(c.Text.Length, SnippetChars + 60)),
            })
            .ToListAsync(ct);

        // PDF-bestands-URL's voor deeplinks (…rules.pdf#page=N).
        var docIds = rows.Select(r => r.DocumentId).Distinct().ToList();
        var fileUrls = await db.Documents
            .Where(d => docIds.Contains(d.Id) && d.FileUrl != null)
            .ToDictionaryAsync(d => d.Id, d => d.FileUrl, ct);

        // Fusie-volgorde aanhouden; één resultaat per sectie — meerdere chunks
        // van dezelfde § zijn voor de gebruiker hetzelfde resultaat.
        var rowsById = rows.ToDictionary(r => r.Id);
        var seen = new HashSet<(string SourceId, string SectionCode)>();
        var hits = new List<RuleSearchHit>();
        foreach (var id in rankedIds)
        {
            // Een chunk kan tussen de query's verdwenen zijn (her-index).
            if (!rowsById.TryGetValue(id, out var row)) continue;
            if (!seen.Add((row.SourceId, row.SectionCode!))) continue;
            hits.Add(new RuleSearchHit(
                row.Id, row.SourceId, row.SectionCode!, row.Page,
                TextUtils.Snippet(row.Text, SnippetChars),
                fileUrls.GetValueOrDefault(row.DocumentId)));
            if (hits.Count == limit) break;
        }
        return hits;
    }

    /// <summary>Dezelfde selectie als de regels-boom (/api/rules/toc): alleen
    /// chunks met een echte §-code, zodat elk resultaat kan deeplinken.</summary>
    private IQueryable<RuleChunk> SectionChunks() =>
        db.RuleChunks.AsNoTracking().Where(c =>
            c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro");
}
