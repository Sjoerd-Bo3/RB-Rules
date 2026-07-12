using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GapCoverage(
    int Cards, int CardsWithoutEmbedding, int CardsWithoutMechanics,
    int RuleChunks, int RuleChunksWithoutEmbedding,
    int PrimerTopics, int PrimerTopicsMissing, int PrimerDrafts,
    int OpenMechanicCandidates, int TracesConsidered);

public record GapQuestion(
    string Signal, string Question, string? QuestionType, DateTimeOffset CreatedAt);

public record GapSourceStatus(
    string Id, string Name, short TrustTier, int Documents, int Chunks,
    DateTimeOffset? LastChecked, DateTimeOffset? LastChangeAt);

/// <summary>Drift Postgres ↔ Neo4j (#108): per knooptype de verwachte
/// telling (Postgres, sync-predicaten) naast de werkelijke graph-telling.
/// Niet beschikbaar (Neo4j plat) is een geldige meting: GraphAvailable=false
/// met de reden in Detail — de rest van het rapport blijft gewoon staan.</summary>
public record GapDrift(
    bool GraphAvailable, string? Detail, IReadOnlyList<GraphDriftEntry> Entries);

public record KnowledgeGapsReport(
    GapCoverage Coverage,
    IReadOnlyList<GapQuestion> Questions,
    IReadOnlyList<GapSourceStatus> Sources,
    GapDrift Drift);

/// <summary>Kennis-gaten-rapport (#52): meet waar de kennisbank dun is in
/// plaats van te raden. Vier invalshoeken: dekking (kaarten zonder
/// embedding/mechanics, secties zonder embedding, ontbrekende primer-
/// concepten), vraag-signalen (lege retrieval, AI-uitval, negatieve
/// feedback uit de ask-traces en correcties), bron-versheid (bronnen die
/// al lang niets nieuws leverden) en graph-drift (#108: loopt de
/// Neo4j-projectie achter op Postgres). Alleen reads — het rapport wordt
/// bij elke aanvraag vers berekend, er is niets om te verversen of cachen.</summary>
public class KnowledgeGapsService(RbRulesDbContext db, BrainGraphService graph)
{
    /// <summary>Hoeveel recente traces meewegen in de vraag-signalen.</summary>
    private const int TraceWindow = 200;

    public async Task<KnowledgeGapsReport> BuildAsync(CancellationToken ct = default)
    {
        // ── Dekking — zelfde predicaten als de pijplijnen zelf ──────────
        var canonical = db.Cards.Where(c => c.VariantOf == null);
        var cardsTotal = await canonical.CountAsync(ct);
        var cardsWithoutEmbedding = await canonical.CountAsync(c => c.Embedding == null, ct);
        var cardsWithoutMechanics = await canonical.CountAsync(
            c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != "", ct);

        var chunksTotal = await db.RuleChunks.CountAsync(ct);
        var chunksWithoutEmbedding = await db.RuleChunks.CountAsync(c => c.Embedding == null, ct);

        var primerTopics = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer")
            .Select(k => new { k.Topic, k.Status })
            .ToListAsync(ct);
        var haveTopic = primerTopics.Select(t => t.Topic).ToHashSet();
        var topicsMissing = PrimerTopics.All.Count(t => !haveTopic.Contains(t.Key));
        var primerDrafts = primerTopics.Count(t => t.Status != "approved");

        var openCandidates = await db.MechanicKeywords.CountAsync(k => k.Status == "candidate", ct);

        // ── Vraag-signalen — het kompas voor de volgende harvest ────────
        var traces = await db.AskTraces.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(TraceWindow)
            .Select(t => new
            {
                t.Question, t.QuestionType, t.Sections, t.ContextCards,
                t.PrimerDocs, t.Ok, t.CreatedAt,
            })
            .ToListAsync(ct);

        var questions = new List<GapQuestion>();
        foreach (var t in traces)
        {
            // "Lege retrieval": de prompt had níets — geen secties, geen
            // kaartcontext, geen primer. Hier weet de bank aantoonbaar niets.
            if (t.Ok && string.IsNullOrEmpty(t.Sections)
                     && string.IsNullOrEmpty(t.ContextCards)
                     && string.IsNullOrEmpty(t.PrimerDocs))
                questions.Add(new("lege-retrieval", t.Question, t.QuestionType, t.CreatedAt));
            else if (!t.Ok)
                questions.Add(new("ai-uitval", t.Question, t.QuestionType, t.CreatedAt));
        }

        // Negatieve feedback ("gemeld als onjuist") met de gestelde vraag erbij.
        var negative = await db.Corrections.AsNoTracking()
            .Where(c => c.Ref == "down" && c.Question != null)
            .OrderByDescending(c => c.CreatedAt)
            .Take(100)
            .Select(c => new { c.Question, c.CreatedAt })
            .ToListAsync(ct);
        questions.AddRange(negative.Select(c =>
            new GapQuestion("negatieve-feedback", c.Question!, null, c.CreatedAt)));
        questions = [.. questions.OrderByDescending(q => q.CreatedAt)];

        // ── Bron-versheid: wanneer leverde elke bron voor het laatst iets ─
        var docCounts = await db.Documents.AsNoTracking()
            .GroupBy(d => d.SourceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var chunkCounts = await db.RuleChunks.AsNoTracking()
            .GroupBy(c => c.SourceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var lastChanges = await db.Changes.AsNoTracking()
            .GroupBy(c => c.SourceId)
            .Select(g => new { g.Key, Last = g.Max(c => c.DetectedAt) })
            .ToDictionaryAsync(g => g.Key, g => g.Last, ct);

        var sources = (await db.Sources.AsNoTracking()
                .Where(s => s.Enabled)
                .OrderBy(s => s.TrustTier).ThenBy(s => s.Id)
                .ToListAsync(ct))
            .Select(s => new GapSourceStatus(
                s.Id, s.Name, s.TrustTier,
                docCounts.GetValueOrDefault(s.Id),
                chunkCounts.GetValueOrDefault(s.Id),
                s.LastChecked,
                lastChanges.TryGetValue(s.Id, out var last) ? last : null))
            .ToList();

        return new(
            new GapCoverage(
                cardsTotal, cardsWithoutEmbedding, cardsWithoutMechanics,
                chunksTotal, chunksWithoutEmbedding,
                PrimerTopics.All.Count, topicsMissing, primerDrafts,
                openCandidates, traces.Count),
            questions,
            sources,
            await BuildDriftAsync(ct));
    }

    /// <summary>Graph-drift (#108, docs/BRAIN.md §4): telt per knooptype wat
    /// Postgres nú zou projecteren (exact de predicaten van GraphSyncService)
    /// en zet dat naast de werkelijke Neo4j-tellingen. Best-effort: zonder
    /// Neo4j geen drift-cijfers maar wél een rapport — de fout is de data.</summary>
    private async Task<GapDrift> BuildDriftAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, int> graphCounts;
        try
        {
            graphCounts = await graph.CountsByLabelAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, ex.Message, []);
        }

        var canonical = db.Cards.AsNoTracking().Where(c => c.VariantOf == null);

        // Facetten (Domain/Tag/Mechanic) zijn array-kolommen: distinct over
        // de elementen kan niet in één vertaalbare LINQ-query — bewust
        // gematerialiseerd (drie smalle kolommen over honderden kaarten),
        // met exacte (ordinal) vergelijking zoals Neo4j's MERGE op name.
        var facets = await canonical
            .Select(c => new { c.SetId, c.Domains, c.Tags, c.Mechanics })
            .ToListAsync(ct);

        var postgres = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Card"] = facets.Count,
            ["Set"] = facets.Where(f => f.SetId != null).Select(f => f.SetId!).Distinct().Count(),
            ["Domain"] = facets.SelectMany(f => f.Domains).Distinct().Count(),
            ["Tag"] = facets.SelectMany(f => f.Tags).Distinct().Count(),
            ["Mechanic"] = facets.SelectMany(f => f.Mechanics ?? []).Distinct().Count(),
            // Sectie-knopen vouwen chunks samen tot één per (bron, §-code).
            ["RuleSection"] = await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && r.SectionCode != "")
                .Select(r => new { r.SourceId, r.SectionCode })
                .Distinct()
                .CountAsync(ct),
            ["Concept"] = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer")
                .Select(k => k.Topic)
                .Distinct()
                .CountAsync(ct),
            // Scope-keuze uit de sync: alleen accepted/unreviewed claims.
            ["Claim"] = await db.Claims.CountAsync(
                c => c.Status == "accepted" || c.Status == "unreviewed", ct),
            ["Source"] = await db.Sources.CountAsync(ct),
            ["Erratum"] = await db.Errata.CountAsync(ct),
            ["Change"] = await db.Changes.CountAsync(ct),
        };

        return new(true, null, GraphDrift.Compare(postgres, graphCounts));
    }
}
