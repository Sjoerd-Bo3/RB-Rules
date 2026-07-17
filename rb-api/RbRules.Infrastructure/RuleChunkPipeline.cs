using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record RuleIndexResult(string SourceId, int Chunks);

/// <summary>Indexeert het nieuwste document per bron: sectie-parse → chunks met
/// chunk_index + section_code (audit-fixes) → embeddings. Idempotent per
/// document: al geïndexeerde documenten worden overgeslagen.</summary>
public class RuleChunkPipeline(RbRulesDbContext db, EmbeddingService embeddings)
{
    private const int EmbedBatch = 16;

    public async Task<List<RuleIndexResult>> RunAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<RuleIndexResult>();
        // IgnoredAt (#180): een genegeerde bron levert per beoordeling niets
        // op — geen her-indexering/embeddings meer (zelfde bereik-afspraak
        // als de scan-lus; bestaande rule_chunks blijven gewoon staan).
        var sources = await db.Sources
            .Where(s => s.Enabled && s.IgnoredAt == null)
            .ToListAsync(ct);

        foreach (var src in sources)
        {
            progress?.Invoke($"document van {src.Name} controleren");
            var doc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);
            if (doc is null) continue;

            // force herbouwt ook al geïndexeerde documenten (bijv. na een
            // parser-verbetering); standaard alleen nieuwe documenten.
            var alreadyIndexed = await db.RuleChunks.AnyAsync(c => c.DocumentId == doc.Id, ct);
            if (alreadyIndexed && !force) continue;

            var sections = RuleSectionParser.Parse(doc.Content);
            if (sections.Count == 0) continue;

            var chunks = sections.Select((s, i) => new RuleChunk
            {
                DocumentId = doc.Id,
                SourceId = src.Id,
                SectionCode = string.IsNullOrEmpty(s.Code) || s.Code == "intro" ? null : s.Code,
                ChunkIndex = i,
                Text = s.Text,
                Page = s.Page,
            }).ToList();

            // Eerst volledig embedden (minutenlange, fallibele netwerkstap) —
            // pas daarna oud-weg/nieuw-erin in één transactie, zodat er nooit
            // een venster zonder regelindex is (review-fix).
            foreach (var batch in chunks.Chunk(EmbedBatch))
            {
                var vectors = await embeddings.EmbedAsync(
                    [.. batch.Select(c => c.Text)], ct);
                for (var k = 0; k < batch.Length; k++)
                {
                    batch[k].Embedding = vectors[k];
                    batch[k].EmbeddingModel = EmbeddingConfig.Model;
                }
            }

            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await db.RuleChunks.Where(c => c.SourceId == src.Id).ExecuteDeleteAsync(ct);
                db.RuleChunks.AddRange(chunks);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            results.Add(new(src.Id, chunks.Count));
        }
        return results;
    }
}
