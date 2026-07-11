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

public record KnowledgeGapsReport(
    GapCoverage Coverage,
    IReadOnlyList<GapQuestion> Questions,
    IReadOnlyList<GapSourceStatus> Sources);

/// <summary>Kennis-gaten-rapport (#52): meet waar de kennisbank dun is in
/// plaats van te raden. Drie invalshoeken: dekking (kaarten zonder
/// embedding/mechanics, secties zonder embedding, ontbrekende primer-
/// concepten), vraag-signalen (lege retrieval, AI-uitval, negatieve
/// feedback uit de ask-traces en correcties) en bron-versheid (bronnen die
/// al lang niets nieuws leverden). Alleen reads — het rapport wordt bij
/// elke aanvraag vers berekend, er is niets om te verversen of cachen.</summary>
public class KnowledgeGapsService(RbRulesDbContext db)
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
            sources);
    }
}
