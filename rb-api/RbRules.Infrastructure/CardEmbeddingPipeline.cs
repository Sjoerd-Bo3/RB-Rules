using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record EmbedRunResult(int Embedded, int Skipped);

/// <summary>S1-fundament F2: embed kaarten in batches. Idempotent — alleen
/// kaarten zonder embedding of met een verouderd model (provenance-guard);
/// `force` her-embed alles.</summary>
public class CardEmbeddingPipeline(RbRulesDbContext db, EmbeddingService embeddings)
{
    private const int BatchSize = 16;

    public async Task<EmbedRunResult> RunAsync(bool force = false, CancellationToken ct = default)
    {
        var cards = await db.Cards.OrderBy(c => c.RiftboundId).ToListAsync(ct);
        var todo = force ? cards : [.. cards.Where(CardText.NeedsEmbedding)];
        var skipped = cards.Count - todo.Count;

        for (var i = 0; i < todo.Count; i += BatchSize)
        {
            var batch = todo.Skip(i).Take(BatchSize).ToList();
            var vectors = await embeddings.EmbedAsync(
                [.. batch.Select(CardText.Compose)], ct);
            for (var k = 0; k < batch.Count; k++)
            {
                batch[k].Embedding = vectors[k];
                batch[k].EmbeddingModel = EmbeddingConfig.Model;
            }
            await db.SaveChangesAsync(ct);
        }
        return new(todo.Count, skipped);
    }
}
