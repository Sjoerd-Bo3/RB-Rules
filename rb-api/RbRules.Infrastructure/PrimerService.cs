using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record PrimerResult(int Written, int Skipped, int Failed);

/// <summary>Kennislaag 1 (docs/KNOWLEDGE.md): destilleert per concept een
/// primer-doc uit de regelindex — samenhangend spelbegrip mét §-verwijzingen.
/// Nieuwe/gewijzigde docs zijn draft; de beheerder keurt ze in /admin, pas
/// daarna doen ze mee in de /ask-context.</summary>
public class PrimerService(RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
{
    private const int ChunksPerTopic = 10;

    // #187: afgeleide/gesynthetiseerde kennis wordt in de brontaal (Engels)
    // opgeslagen, dicht bij de officiële bewoording (docs/CONVENTIONS.md). De
    // UI en /ask-antwoorden blijven Nederlands — dat scheidt AskService.
    // BasePrompt af, deze primer-tekst is context, geen eindantwoord.
    private const string SystemPrompt = """
        You write a concise game-understanding document for Riftbound TCG
        players, based on the official rule sections provided. Requirements:
        - 200 to 350 words, in English, close to the official wording
        - Explain the FLOW (what happens when, and why), not just isolated
          facts; mention the most common misconception if there is one
        - Reference sections inline as (§123.4) where you base something on
          them
        - No introduction or closing remarks, no markdown headers — just
          running text in short paragraphs
        - Base yourself exclusively on the given sections; don't claim
          anything that isn't in them
        """;

    public async Task<PrimerResult> GenerateAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        var written = 0;
        var skipped = 0;
        var failed = 0;
        var n = 0;
        foreach (var topic in PrimerTopics.All)
        {
            n++;
            var existing = await db.KnowledgeDocs.FirstOrDefaultAsync(
                k => k.Kind == "primer" && k.Topic == topic.Key, ct);
            if (existing is { Status: "approved" } && !force)
            {
                skipped++;
                continue;
            }

            progress?.Invoke($"primer {n}/{PrimerTopics.All.Count}: {topic.Title}");

            // Relevante secties voor dit concept (semantisch).
            var qv = await embeddings.EmbedOneAsync($"{topic.Title}. {topic.Query}", ct);
            var chunks = await db.RuleChunks.AsNoTracking()
                .Where(c => c.Embedding != null && c.SectionCode != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(ChunksPerTopic)
                .Select(c => new { c.SectionCode, c.Text })
                .ToListAsync(ct);
            if (chunks.Count == 0) { failed++; continue; }

            var context = string.Join("\n\n", chunks.Select(c => $"§{c.SectionCode}: {c.Text}"));
            var body = await ai.AskAsync(
                $"Concept: {topic.Title}\n\nOfficiële regelsecties:\n{context}",
                SystemPrompt, ct: ct);
            if (string.IsNullOrWhiteSpace(body)) { failed++; continue; }

            var refs = string.Join(", ", chunks.Select(c => c.SectionCode));
            var docEmbedding = await embeddings.EmbedOneAsync($"{topic.Title}\n{body}", ct);
            if (existing is null)
            {
                db.KnowledgeDocs.Add(new KnowledgeDoc
                {
                    Kind = "primer", Topic = topic.Key, Title = topic.Title,
                    Body = body.Trim(), SectionRefs = refs, Status = "draft",
                    Embedding = docEmbedding, EmbeddingModel = EmbeddingConfig.Model,
                });
            }
            else
            {
                existing.Title = topic.Title;
                existing.Body = body.Trim();
                existing.SectionRefs = refs;
                existing.Status = "draft"; // her-generatie vraagt opnieuw om review
                existing.Embedding = docEmbedding;
                existing.EmbeddingModel = EmbeddingConfig.Model;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            written++;
        }
        return new(written, skipped, failed);
    }
}
