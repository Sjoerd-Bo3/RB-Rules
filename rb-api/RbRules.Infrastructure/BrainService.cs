using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

// Response-records van de brein-API (#105). Bewust hier en niet in
// ApiContracts.cs: de services bouwen deze responses en Infrastructure mag
// niet naar Api verwijzen (lagen zijn éénrichting) — hetzelfde patroon als
// Citation/AskResult bij AskService. Veldnamen volgen docs/BRAIN.md §2.3
// letterlijk (camelCase op de draad), incl. het NL-veld "richting".

public record BrainSearchItem(
    string Ref, string Layer, string? Title, string? Snippet, double Score, string TrustLabel);

/// <summary>Wrapper {results: [...]} — de rb-ai-tools accepteren een platte
/// array óf deze wrapper; de wrapper laat ruimte voor het degradatie-signaal
/// (#100-patroon: embedding-uitval ⇒ alleen-FTS, eerlijk gemeld).</summary>
public record BrainSearchResponse(IReadOnlyList<BrainSearchItem> Results, bool Degraded);

/// <summary>Eén brein-knoop als Postgres-projectie (nooit embeddings): vaste
/// omtrek (ref/kind/laag/trust) met de soort-specifieke eigenschappen als
/// props-map — twaalf ref-soorten met elk een eigen record zou twaalf
/// contracten zijn voor één koppelvlak.</summary>
public record BrainNodeResponse(
    string Ref, string Kind, string Layer, string TrustLabel, Dictionary<string, object?> Props);

public record BrainEvidenceSource(
    string SourceId, string Name, short TrustTier, string Url, string? Quote);

public record BrainEvidenceResponse(
    string Ref, string Statement, string TopicType, string TopicRef,
    int Corroboration, double TrustScore, string Status, string? StatusReason,
    string OfficialStatus, string TrustLabel, IReadOnlyList<BrainEvidenceSource> Sources);

public record BrainConflictItem(
    long Id, string Topic, string Kind, string Status, string? Explanation,
    string? SourceAId, string? SourceBId, string? WinnerSourceId, DateTimeOffset DetectedAt);

public record BrainRejectedClaim(
    string Ref, string TopicType, string TopicRef, string Statement,
    string Status, string? StatusReason, string OfficialStatus);

public record BrainContradictionsResponse(
    string Topic, IReadOnlyList<BrainConflictItem> Conflicts,
    IReadOnlyList<BrainRejectedClaim> RejectedClaims);

/// <summary>Postgres-kant van de brein-API (#105, docs/BRAIN.md §2.3):
/// semantisch zoeken over de vijf embedding-tabellen met één embed-call en
/// RRF-fusie per laag (vector + full-text), plus node/evidence/contradictions
/// als projecties zonder embeddings. Embedding-uitval is een verwacht pad
/// (#100-patroon): dan degradeert search naar alleen de FTS-kanalen — nooit
/// een kale 500. Geen LLM-calls: alleen DB-reads.</summary>
public class BrainService(
    RbRulesDbContext db, EmbeddingService embeddings, CardResolver resolver,
    ILogger<BrainService> logger)
{
    private const int SnippetChars = 240;

    /// <summary>Zelfde cap als RuleSearchService: een publieke zoekvraag mag
    /// nooit minutenlang aan een koude/haperende Ollama hangen.</summary>
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromSeconds(8);

    // ── search ─────────────────────────────────────────────────────────

    public async Task<BrainSearchResponse> SearchAsync(
        string query, IReadOnlySet<string> layers, int take, CancellationToken ct = default)
    {
        // Eén embed-call voor alle lagen (§2.3) — best-effort: bij uitval
        // blijft qv null en zoeken alle lagen alleen met full-text.
        Vector? qv = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EmbedTimeout);
            qv = await embeddings.EmbedOneAsync(query, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // de aanvrager zelf haakte af — niet maskeren
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Embedding voor brein-zoek mislukt — degradatie naar alleen-FTS");
        }

        // Piramide-volgorde in het resultaat: officieel (rules, kaartdata) >
        // geverifieerde rulings > primer > community — dezelfde volgorde
        // waarin de agent de lagen hoort te wegen.
        var results = new List<BrainSearchItem>();
        if (layers.Contains("rules")) results.AddRange(await SearchRulesAsync(query, qv, take, ct));
        if (layers.Contains("cards")) results.AddRange(await SearchCardsAsync(query, qv, take, ct));
        if (layers.Contains("rulings")) results.AddRange(await SearchRulingsAsync(query, qv, take, ct));
        if (layers.Contains("primer")) results.AddRange(await SearchPrimerAsync(query, qv, take, ct));
        if (layers.Contains("claims")) results.AddRange(await SearchClaimsAsync(query, qv, take, ct));
        return new(results, Degraded: qv is null);
    }

    private async Task<List<BrainSearchItem>> SearchRulesAsync(
        string query, Vector? qv, int take, CancellationToken ct)
    {
        // Ruim ophalen: de sectie-dedupe hieronder (meerdere chunks per §)
        // moet nog wat te kiezen hebben.
        var fetch = Math.Max(take * 2, 12);
        IQueryable<RuleChunk> sections() => db.RuleChunks.AsNoTracking()
            .Where(c => c.SectionCode != null && c.SectionCode != "");

        List<long> vectorIds = [];
        if (qv is not null)
            vectorIds = await sections()
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var textIds = await sections()
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var ranked = RrfFusion.FuseScored([vectorIds, textIds], id => id, fetch);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.Key).ToList();
        var rows = await db.RuleChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.SourceId, c.SectionCode,
                Text = c.Text.Substring(0, Math.Min(c.Text.Length, SnippetChars + 60)),
                s.Name, s.Type, s.TrustTier,
            })
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.Id);

        // Fusie-volgorde aanhouden; één resultaat per § (RuleSearchService-
        // patroon): meerdere chunks van dezelfde sectie zijn één antwoord.
        var seen = new HashSet<(string, string)>();
        var items = new List<BrainSearchItem>();
        foreach (var (id, score) in ranked)
        {
            if (!rowsById.TryGetValue(id, out var row)) continue;
            if (!seen.Add((row.SourceId, row.SectionCode!))) continue;
            items.Add(new(
                BrainRef.Section(row.SourceId, row.SectionCode!).Format(),
                "rules",
                $"§{row.SectionCode} — {row.Name}",
                TextUtils.Snippet(row.Text, SnippetChars),
                Round(score),
                SourceTrustLabel(row.Type, row.TrustTier)));
            if (items.Count == take) break;
        }
        return items;
    }

    private async Task<List<BrainSearchItem>> SearchCardsAsync(
        string query, Vector? qv, int take, CancellationToken ct)
    {
        var fetch = Math.Max(take * 2, 12);
        List<string> vectorIds = [];
        if (qv is not null)
            vectorIds = await db.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.RiftboundId)
                .ToListAsync(ct);
        var textIds = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Where(c => EF.Functions.ToTsVector("english", c.Name + " " + (c.TextPlain ?? ""))
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Name + " " + (c.TextPlain ?? ""))
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);

        var ranked = RrfFusion.FuseScored([vectorIds, textIds], id => id, take);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.Key).ToList();
        var rows = await db.Cards.AsNoTracking()
            .Where(c => ids.Contains(c.RiftboundId))
            .Select(c => new { c.RiftboundId, c.Name, c.Type, c.TextPlain })
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.RiftboundId);

        return [.. ranked
            .Where(r => rowsById.ContainsKey(r.Key))
            .Select(r =>
            {
                var row = rowsById[r.Key];
                return new BrainSearchItem(
                    BrainRef.Card(row.RiftboundId).Format(),
                    "cards",
                    row.Type is null ? row.Name : $"{row.Name} ({row.Type})",
                    TextUtils.Snippet(row.TextPlain ?? "", SnippetChars),
                    Round(r.Score),
                    "officieel (kaartdata)");
            })];
    }

    private async Task<List<BrainSearchItem>> SearchRulingsAsync(
        string query, Vector? qv, int take, CancellationToken ct)
    {
        var fetch = Math.Max(take * 2, 12);
        IQueryable<Correction> verified() => db.Corrections.AsNoTracking()
            .Where(c => c.Status == "verified");

        List<long> vectorIds = [];
        if (qv is not null)
            vectorIds = await verified()
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var textIds = await verified()
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var ranked = RrfFusion.FuseScored([vectorIds, textIds], id => id, take);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.Key).ToList();
        var rows = await db.Corrections.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Question, c.Text })
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.Id);

        return [.. ranked
            .Where(r => rowsById.ContainsKey(r.Key))
            .Select(r =>
            {
                var row = rowsById[r.Key];
                return new BrainSearchItem(
                    BrainRef.Ruling(row.Id).Format(),
                    "rulings",
                    TextUtils.Snippet(row.Question ?? "geverifieerde ruling", 120),
                    TextUtils.Snippet(row.Text, SnippetChars),
                    Round(r.Score),
                    "geverifieerde ruling (gezaghebbend)");
            })];
    }

    private async Task<List<BrainSearchItem>> SearchPrimerAsync(
        string query, Vector? qv, int take, CancellationToken ct)
    {
        var fetch = Math.Max(take * 2, 12);
        IQueryable<KnowledgeDoc> approved() => db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer" && k.Status == "approved");

        List<long> vectorIds = [];
        if (qv is not null)
            vectorIds = await approved()
                .Where(k => k.Embedding != null)
                .OrderBy(k => k.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(k => k.Id)
                .ToListAsync(ct);
        var textIds = await approved()
            .Where(k => EF.Functions.ToTsVector("english", k.Title + " " + k.Body)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(k => EF.Functions.ToTsVector("english", k.Title + " " + k.Body)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(k => k.Id)
            .ToListAsync(ct);

        var ranked = RrfFusion.FuseScored([vectorIds, textIds], id => id, take);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.Key).ToList();
        var rows = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => ids.Contains(k.Id))
            .Select(k => new { k.Id, k.Topic, k.Title, k.Body })
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.Id);

        return [.. ranked
            .Where(r => rowsById.ContainsKey(r.Key))
            .Select(r =>
            {
                var row = rowsById[r.Key];
                return new BrainSearchItem(
                    BrainRef.Concept(row.Topic).Format(),
                    "primer",
                    row.Title,
                    TextUtils.Snippet(row.Body, SnippetChars),
                    Round(r.Score),
                    "primer (gedistilleerd uit de officiële regels)");
            })];
    }

    private async Task<List<BrainSearchItem>> SearchClaimsAsync(
        string query, Vector? qv, int take, CancellationToken ct)
    {
        // Alleen accepted claims zijn kennis voor de agent (docs/BRAIN.md §4);
        // unreviewed is via de graph zichtbaar (mét status-property) en
        // rejected/superseded uitsluitend via contradictions — gelabeld.
        var fetch = Math.Max(take * 2, 12);
        IQueryable<Claim> accepted() => db.Claims.AsNoTracking()
            .Where(c => c.Status == "accepted");

        List<long> vectorIds = [];
        if (qv is not null)
            vectorIds = await accepted()
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var textIds = await accepted()
            .Where(c => EF.Functions.ToTsVector("english", c.Statement)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Statement)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var ranked = RrfFusion.FuseScored([vectorIds, textIds], id => id, take);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.Key).ToList();
        var rows = await db.Claims.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement,
                c.Corroboration, c.TrustScore, c.OfficialStatus,
            })
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.Id);

        return [.. ranked
            .Where(r => rowsById.ContainsKey(r.Key))
            .Select(r =>
            {
                var row = rowsById[r.Key];
                return new BrainSearchItem(
                    BrainRef.Claim(row.Id).Format(),
                    "claims",
                    $"{row.TopicType}:{row.TopicRef}",
                    TextUtils.Snippet(row.Statement, SnippetChars),
                    Round(r.Score),
                    ClaimTrustLabel(row.Corroboration, row.TrustScore, "accepted", row.OfficialStatus));
            })];
    }

    // ── node ───────────────────────────────────────────────────────────

    /// <summary>Eén knoop als Postgres-projectie (nooit embeddings), met laag
    /// en provenance. Null = onbekende ref (404 bij de endpoint).</summary>
    public async Task<BrainNodeResponse?> NodeAsync(BrainRef nodeRef, CancellationToken ct = default)
        => nodeRef.Kind switch
        {
            BrainRefKind.Card => await CardNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Mechanic => await MechanicNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Concept => await ConceptNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Section => await SectionNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Claim => await ClaimNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Ruling => await RulingNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Source => await SourceNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Erratum => await ErratumNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Change => await ChangeNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Set => await SetNodeAsync(nodeRef.Key, ct),
            BrainRefKind.Domain => await FacetNodeAsync(BrainRefKind.Domain, nodeRef.Key, ct),
            BrainRefKind.Tag => await FacetNodeAsync(BrainRefKind.Tag, nodeRef.Key, ct),
            _ => null,
        };

    private async Task<BrainNodeResponse?> CardNodeAsync(string riftboundId, CancellationToken ct)
    {
        var card = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == riftboundId)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct);
        if (card is null) return null;

        // Variant-ids resolven naar de canonieke printing (#57/BRAIN.md §4):
        // de ref in de respons is altijd de canonieke.
        var canonical = await resolver.CanonicalAsync(card, ct);
        var banned = await BanLookup.BannedCanonicalIdsAsync(db, ct);

        return new(
            BrainRef.Card(canonical.RiftboundId).Format(), "card", "cards",
            "officieel (kaartdata)",
            new Dictionary<string, object?>
            {
                ["name"] = canonical.Name,
                ["type"] = canonical.Type,
                ["supertype"] = canonical.Supertype,
                ["rarity"] = canonical.Rarity,
                ["domains"] = canonical.Domains,
                ["tags"] = canonical.Tags,
                ["mechanics"] = canonical.Mechanics,
                ["energy"] = canonical.Energy,
                ["might"] = canonical.Might,
                ["power"] = canonical.Power,
                ["text"] = canonical.TextPlain,
                ["set"] = canonical.SetLabel ?? canonical.SetId,
                ["setRef"] = canonical.SetId is null ? null : BrainRef.Set(canonical.SetId).Format(),
                ["banned"] = BanLookup.IsBanned(banned, canonical),
                ["requestedRef"] = card.RiftboundId == canonical.RiftboundId
                    ? null : BrainRef.Card(card.RiftboundId).Format(),
            });
    }

    private async Task<BrainNodeResponse?> MechanicNodeAsync(string name, CancellationToken ct)
    {
        // Case-herstel: mechanics staan capitalized op de kaarten ("Deflect");
        // een agent die "mechanic:deflect" vraagt hoort niet op een 404.
        var allMechanics = (await db.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.Mechanics != null)
                .Select(c => c.Mechanics!)
                .ToListAsync(ct))
            .SelectMany(m => m)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var canonical = allMechanics
            .FirstOrDefault(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (canonical is null) return null;

        var count = await db.Cards.CountAsync(
            c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(canonical), ct);
        var examples = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(canonical))
            .OrderBy(c => c.RiftboundId)
            .Take(5)
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);

        return new(
            BrainRef.Mechanic(canonical).Format(), "mechanic", "cards",
            "officieel (kaartdata, geminede mechaniek)",
            new Dictionary<string, object?>
            {
                ["name"] = canonical,
                ["cardCount"] = count,
                ["exampleCards"] = examples
                    .Select(e => new { @ref = BrainRef.Card(e.RiftboundId).Format(), name = e.Name })
                    .ToList<object>(),
            });
    }

    private async Task<BrainNodeResponse?> ConceptNodeAsync(string topic, CancellationToken ct)
    {
        var doc = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer" && k.Topic == topic)
            .Select(k => new { k.Topic, k.Title, k.Body, k.Status, k.SectionRefs, k.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (doc is null) return null;

        return new(
            BrainRef.Concept(doc.Topic).Format(), "concept", "primer",
            doc.Status == "approved"
                ? "primer (gedistilleerd uit de officiële regels)"
                : $"primer (status={doc.Status} — nog niet goedgekeurd)",
            new Dictionary<string, object?>
            {
                ["topic"] = doc.Topic,
                ["title"] = doc.Title,
                ["status"] = doc.Status,
                ["body"] = doc.Body,
                ["sectionCodes"] = (doc.SectionRefs ?? "").Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                ["updatedAt"] = doc.UpdatedAt,
            });
    }

    private async Task<BrainNodeResponse?> SectionNodeAsync(string key, CancellationToken ct)
    {
        // Sectie-key is "<sourceId>/<code>" (BrainRef.Section); alleen de
        // eerste slash scheidt — codes zelf bevatten er geen.
        var split = key.IndexOf('/');
        if (split <= 0 || split == key.Length - 1) return null;
        var sourceId = key[..split];
        var code = key[(split + 1)..];

        var chunks = await db.RuleChunks.AsNoTracking()
            .Where(c => c.SourceId == sourceId && c.SectionCode == code)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.Text, c.Page })
            .ToListAsync(ct);
        if (chunks.Count == 0) return null;

        var source = await db.Sources.AsNoTracking()
            .Where(s => s.Id == sourceId)
            .Select(s => new { s.Name, s.Url, s.Type, s.TrustTier })
            .FirstOrDefaultAsync(ct);

        var parents = await RuleParentLookup.FetchAsync(db, [(sourceId, code)], ct);

        return new(
            BrainRef.Section(sourceId, code).Format(), "section", "rules",
            source is null ? "officieel" : SourceTrustLabel(source.Type, source.TrustTier),
            new Dictionary<string, object?>
            {
                ["code"] = code,
                ["sourceId"] = sourceId,
                ["sourceName"] = source?.Name,
                ["sourceUrl"] = source?.Url,
                ["page"] = chunks[0].Page,
                ["text"] = string.Join("\n\n", chunks.Select(c => c.Text)),
                // Ouderketen (#39): een subregel is zonder zijn ouders onleesbaar.
                ["parents"] = parents.GetValueOrDefault((sourceId, code), [])
                    .Select(p => new { code = p.Code, text = p.Text })
                    .ToList<object>(),
            });
    }

    private async Task<BrainNodeResponse?> ClaimNodeAsync(string key, CancellationToken ct)
    {
        if (!long.TryParse(key, out var id)) return null;
        var claim = await db.Claims.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
                c.FirstSeen, c.LastSeen,
            })
            .FirstOrDefaultAsync(ct);
        if (claim is null) return null;

        return new(
            BrainRef.Claim(claim.Id).Format(), "claim", "claims",
            ClaimTrustLabel(claim.Corroboration, claim.TrustScore, claim.Status, claim.OfficialStatus),
            new Dictionary<string, object?>
            {
                ["statement"] = claim.Statement,
                ["topicType"] = claim.TopicType,
                ["topicRef"] = claim.TopicRef,
                ["corroboration"] = claim.Corroboration,
                ["trustScore"] = claim.TrustScore,
                ["status"] = claim.Status,
                ["statusReason"] = claim.StatusReason,
                ["officialStatus"] = claim.OfficialStatus,
                ["firstSeen"] = claim.FirstSeen,
                ["lastSeen"] = claim.LastSeen,
            });
    }

    private async Task<BrainNodeResponse?> RulingNodeAsync(string key, CancellationToken ct)
    {
        // ruling: is per definitie een gevérifieerde correction (§2.1);
        // ongeverifieerde feedback is geen kennis en dus geen knoop.
        if (!long.TryParse(key, out var id)) return null;
        var ruling = await db.Corrections.AsNoTracking()
            .Where(c => c.Id == id && c.Status == "verified")
            .Select(c => new { c.Id, c.Scope, c.Ref, c.Text, c.Question, c.Provenance, c.VerifiedAt })
            .FirstOrDefaultAsync(ct);
        if (ruling is null) return null;

        return new(
            BrainRef.Ruling(ruling.Id).Format(), "ruling", "rulings",
            "geverifieerde ruling (gezaghebbend)",
            new Dictionary<string, object?>
            {
                ["text"] = ruling.Text,
                ["question"] = ruling.Question,
                ["scope"] = ruling.Scope,
                ["scopeRef"] = ruling.Ref,
                ["provenance"] = ruling.Provenance,
                ["verifiedAt"] = ruling.VerifiedAt,
            });
    }

    private async Task<BrainNodeResponse?> SourceNodeAsync(string id, CancellationToken ct)
    {
        var source = await db.Sources.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Id, s.Name, s.Url, s.Type, s.TrustTier, s.Rank, s.Enabled, s.LastChecked })
            .FirstOrDefaultAsync(ct);
        if (source is null) return null;

        return new(
            BrainRef.Source(source.Id).Format(), "source", "rules",
            SourceTrustLabel(source.Type, source.TrustTier),
            new Dictionary<string, object?>
            {
                ["name"] = source.Name,
                ["url"] = source.Url,
                ["type"] = source.Type,
                ["trustTier"] = source.TrustTier,
                ["rank"] = source.Rank,
                ["enabled"] = source.Enabled,
                ["lastChecked"] = source.LastChecked,
            });
    }

    private async Task<BrainNodeResponse?> ErratumNodeAsync(string key, CancellationToken ct)
    {
        if (!long.TryParse(key, out var id)) return null;
        var erratum = await db.Errata.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.Id, e.CardName, e.CardRiftboundId, e.NewText, e.SourceUrl, e.DetectedAt })
            .FirstOrDefaultAsync(ct);
        if (erratum is null) return null;

        // Kaart-ref richting canoniek: dezelfde resolutie als de graph
        // (SUPERSEDES) — het opgeslagen id kan een variant-printing zijn.
        string? cardRef = null;
        if (erratum.CardRiftboundId is { } cardId)
        {
            var canonicalId = await db.Cards.AsNoTracking()
                .Where(c => c.RiftboundId == cardId)
                .Select(c => c.VariantOf ?? c.RiftboundId)
                .FirstOrDefaultAsync(ct);
            if (canonicalId is not null) cardRef = BrainRef.Card(canonicalId).Format();
        }

        return new(
            BrainRef.Erratum(erratum.Id).Format(), "erratum", "rules",
            "officieel (erratum — actuele kaarttekst)",
            new Dictionary<string, object?>
            {
                ["cardName"] = erratum.CardName,
                ["cardRef"] = cardRef,
                ["newText"] = erratum.NewText,
                ["sourceUrl"] = erratum.SourceUrl,
                ["detectedAt"] = erratum.DetectedAt,
            });
    }

    private async Task<BrainNodeResponse?> ChangeNodeAsync(string key, CancellationToken ct)
    {
        if (!long.TryParse(key, out var id)) return null;
        var change = await db.Changes.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Id, c.SourceId, c.ChangeType, c.Severity, c.Summary, c.Meaning, c.DetectedAt })
            .FirstOrDefaultAsync(ct);
        if (change is null) return null;

        return new(
            BrainRef.Change(change.Id).Format(), "change", "rules",
            "officieel (wijzigingsdetectie)",
            new Dictionary<string, object?>
            {
                ["changeType"] = change.ChangeType,
                ["severity"] = change.Severity,
                ["summary"] = change.Summary,
                ["meaning"] = change.Meaning,
                ["sourceRef"] = BrainRef.Source(change.SourceId).Format(),
                ["detectedAt"] = change.DetectedAt,
            });
    }

    private async Task<BrainNodeResponse?> SetNodeAsync(string setId, CancellationToken ct)
    {
        var set = await db.CardSets.AsNoTracking()
            .Where(s => s.SetId == setId)
            .Select(s => new { s.SetId, s.Name, s.PublishedOn, s.CardCount })
            .FirstOrDefaultAsync(ct);
        if (set is null) return null;

        return new(
            BrainRef.Set(set.SetId).Format(), "set", "cards",
            "officieel (kaartdata)",
            new Dictionary<string, object?>
            {
                ["name"] = set.Name,
                ["publishedOn"] = set.PublishedOn,
                ["cardCount"] = set.CardCount,
            });
    }

    private async Task<BrainNodeResponse?> FacetNodeAsync(
        BrainRefKind kind, string name, CancellationToken ct)
    {
        var count = kind == BrainRefKind.Domain
            ? await db.Cards.CountAsync(c => c.VariantOf == null && c.Domains.Contains(name), ct)
            : await db.Cards.CountAsync(c => c.VariantOf == null && c.Tags.Contains(name), ct);
        if (count == 0) return null;

        return new(
            new BrainRef(kind, name).Format(),
            kind == BrainRefKind.Domain ? "domain" : "tag", "cards",
            "officieel (kaartdata)",
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["cardCount"] = count,
            });
    }

    // ── evidence ───────────────────────────────────────────────────────

    /// <summary>Bewijsvoering per claim (§2.3): statement, corroboratie,
    /// trust-score, officiële toets en de bronnen met citaat + URL. Werkt voor
    /// elke status — juist bij een weerlegde claim is het bewijs interessant;
    /// het trust-label benoemt de status expliciet.</summary>
    public async Task<BrainEvidenceResponse?> EvidenceAsync(long claimId, CancellationToken ct = default)
    {
        var claim = await db.Claims.AsNoTracking()
            .Where(c => c.Id == claimId)
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
            })
            .FirstOrDefaultAsync(ct);
        if (claim is null) return null;

        var sources = await db.ClaimSources.AsNoTracking()
            .Where(s => s.ClaimId == claimId)
            .Join(db.Sources, cs => cs.SourceId, s => s.Id, (cs, s) => new
            {
                s.Id, s.Name, s.TrustTier, cs.Url, cs.QuoteExcerpt,
            })
            .ToListAsync(ct);

        return new(
            BrainRef.Claim(claim.Id).Format(), claim.Statement,
            claim.TopicType, claim.TopicRef, claim.Corroboration, claim.TrustScore,
            claim.Status, claim.StatusReason, claim.OfficialStatus,
            ClaimTrustLabel(claim.Corroboration, claim.TrustScore, claim.Status, claim.OfficialStatus),
            [.. sources
                .OrderBy(s => s.TrustTier)
                .Select(s => new BrainEvidenceSource(s.Id, s.Name, s.TrustTier, s.Url, s.QuoteExcerpt))]);
    }

    // ── contradictions ─────────────────────────────────────────────────

    private const int ContradictionsCap = 20;

    /// <summary>Open Conflict-rijen + rejected/superseded claims op een topic
    /// (§2.3) — het enige koppelvlak dat weerlegde kennis toont, en dan
    /// expliciet gelabeld. Topic mag een BrainRef zijn (mechanic:Deflect,
    /// card:ogn-…, section:…, concept:…) of vrije tekst.</summary>
    public async Task<BrainContradictionsResponse> ContradictionsAsync(
        string topic, CancellationToken ct = default)
    {
        // Claims dragen hun topic als (TopicType, TopicRef) waarbij de ref
        // voor kaarten de kááártnaam is (ClaimTopicMapper) — een card:-ref
        // vertalen we dus eerst naar de naam.
        string? topicType = null;
        var needle = topic;
        if (BrainRef.TryParse(topic, out var parsed))
        {
            topicType = parsed.Kind switch
            {
                BrainRefKind.Card => "card",
                BrainRefKind.Mechanic => "mechanic",
                BrainRefKind.Section => "section",
                BrainRefKind.Concept => "concept",
                _ => null,
            };
            if (topicType is not null)
            {
                needle = parsed.Kind switch
                {
                    // section-key is "<bron>/<code>"; claims kennen alleen de code.
                    BrainRefKind.Section when parsed.Key.Contains('/') =>
                        parsed.Key[(parsed.Key.IndexOf('/') + 1)..],
                    BrainRefKind.Card => await db.Cards.AsNoTracking()
                        .Where(c => c.RiftboundId == parsed.Key)
                        .Select(c => c.Name)
                        .FirstOrDefaultAsync(ct) ?? parsed.Key,
                    _ => parsed.Key,
                };
            }
        }

        var pattern = "%" + EscapeLike(needle) + "%";

        var claimQuery = db.Claims.AsNoTracking()
            .Where(c => c.Status == "rejected" || c.Status == "superseded");
        claimQuery = topicType is not null
            ? claimQuery.Where(c => c.TopicType == topicType &&
                EF.Functions.ILike(c.TopicRef, pattern, "\\"))
            : claimQuery.Where(c =>
                EF.Functions.ILike(c.TopicRef, pattern, "\\") ||
                EF.Functions.ILike(c.Statement, pattern, "\\"));
        var claims = await claimQuery
            .OrderByDescending(c => c.LastSeen)
            .Take(ContradictionsCap)
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement,
                c.Status, c.StatusReason, c.OfficialStatus,
            })
            .ToListAsync(ct);

        var conflicts = await db.Conflicts.AsNoTracking()
            .Where(c => c.Status == "open" && EF.Functions.ILike(c.Topic, pattern, "\\"))
            .OrderByDescending(c => c.DetectedAt)
            .Take(ContradictionsCap)
            .Select(c => new BrainConflictItem(
                c.Id, c.Topic, c.Kind, c.Status, c.Explanation,
                c.SourceAId, c.SourceBId, c.WinnerSourceId, c.DetectedAt))
            .ToListAsync(ct);

        return new(topic, conflicts,
            [.. claims.Select(c => new BrainRejectedClaim(
                BrainRef.Claim(c.Id).Format(), c.TopicType, c.TopicRef, c.Statement,
                c.Status, c.StatusReason, c.OfficialStatus))]);
    }

    // ── labels & utils ─────────────────────────────────────────────────

    private static double Round(double score) => Math.Round(score, 4);

    private static string SourceTrustLabel(string sourceType, short trustTier) =>
        sourceType == "official"
            ? $"officieel (trust {trustTier})"
            : $"community-bron (trust {trustTier})";

    /// <summary>Trust-label voor claims: corroboratie + score, met de status
    /// erbij zodra die afwijkt van accepted — de kennispiramide blijft in élk
    /// koppelvlak expliciet (docs/BRAIN.md, leidend principe).</summary>
    private static string ClaimTrustLabel(
        int corroboration, double trustScore, string status, string officialStatus)
    {
        var basis = $"community ({corroboration} " +
            $"{(corroboration == 1 ? "bron" : "bronnen")}, " +
            $"trust {trustScore.ToString("0.00", CultureInfo.InvariantCulture)}";
        basis += officialStatus switch
        {
            "confirmed" => ", officieel bevestigd",
            "contradicted" => ", officieel tegengesproken",
            _ => "",
        };
        basis += status switch
        {
            "accepted" => "",
            "unreviewed" => ", status=unreviewed — nog niet gereviewd",
            _ => $", status={status} — weerlegd/vervangen, géén geldige kennis",
        };
        return basis + ")";
    }

    /// <summary>LIKE/ILIKE-metatekens onschadelijk maken (escape-teken is
    /// backslash, zie de ILike-aanroepen hierboven).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
