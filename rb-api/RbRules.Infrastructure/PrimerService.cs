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

    private const string SystemPrompt = """
        Je schrijft een beknopt spelbegrip-document voor Riftbound TCG-spelers,
        gebaseerd op de meegegeven officiële regelsecties. Eisen:
        - 200 tot 350 woorden, Nederlands, Engelse speltermen onvertaald
        - Leg de FLOW uit (wat gebeurt er wanneer, en waarom), niet alleen
          losse feiten; noem de meest voorkomende misvatting als die er is
        - Verwijs inline naar secties als (§123.4) waar je iets op baseert
        - Geen inleiding of afsluiting, geen markdown-koppen — alleen lopende
          tekst in korte alinea's
        - Baseer je uitsluitend op de meegegeven secties; wat er niet in
          staat, beweer je niet
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
