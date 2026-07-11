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

    public async Task<EmbedRunResult> RunAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        // Varianten (alt-art/promo) slaan we over: identieke tekst, en zo
        // blijven 'vergelijkbare kaarten' vrij van duplicaten. Alleen de
        // te-embedden kaarten laden (review-fix #43: niet elk uur alle
        // 1024-dim vectoren naar de client trekken om te filteren).
        var model = EmbeddingConfig.Model;
        var baseQuery = db.Cards.Where(c => c.VariantOf == null);
        var total = await baseQuery.CountAsync(ct);
        var todo = await (force
                ? baseQuery
                : baseQuery.Where(c => c.Embedding == null || c.EmbeddingModel != model))
            .OrderBy(c => c.RiftboundId)
            .ToListAsync(ct);
        var skipped = total - todo.Count;

        for (var i = 0; i < todo.Count; i += BatchSize)
        {
            progress?.Invoke($"embeddings berekenen: kaart {i + 1}–{Math.Min(i + BatchSize, todo.Count)} van {todo.Count}");
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
