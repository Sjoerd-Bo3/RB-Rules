using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record MiningResult(int Mined, int Remaining, int Failed);

/// <summary>F3: mine mechanieken/triggers/effects uit kaartteksten via rb-ai.
/// Idempotent: alleen kaarten met tekst die nog niet gemined zijn
/// (Mechanics == null). Herhaalbaar per set-release.</summary>
public class MechanicMiningService(RbRulesDbContext db, RbAiClient ai)
{
    private const int BatchSize = 8;

    public async Task<MiningResult> RunAsync(
        int maxBatches = 25, Action<string>? progress = null, CancellationToken ct = default)
    {
        var todo = await db.Cards
            .Where(c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != ""
                        && c.VariantOf == null)
            .OrderBy(c => c.RiftboundId)
            .Take(maxBatches * BatchSize)
            .ToListAsync(ct);

        var mined = 0;
        var failed = 0;
        var done = 0;
        foreach (var batch in todo.Chunk(BatchSize))
        {
            done += batch.Length;
            progress?.Invoke($"kaartteksten analyseren via LLM: {done}/{todo.Count} in deze run");
            var raw = await ai.AskAsync(
                MechanicMiner.BuildPrompt(batch), MechanicMiner.GetSystemPrompt(), ct: ct);
            var parsed = raw is null ? [] : MechanicMiner.ParseBatch(raw);
            var byId = parsed.ToDictionary(p => p.Id);

            foreach (var card in batch)
            {
                if (byId.TryGetValue(card.RiftboundId, out var m))
                {
                    card.Mechanics = m.Mechanics;
                    card.Triggers = m.Triggers;
                    card.Effects = m.Effects;
                    mined++;
                }
                else
                {
                    failed++; // blijft null → volgende run opnieuw
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // Zelfde predicaat als de todo-query (review-fix: zonder het
        // VariantOf-filter werd Remaining nooit 0 zolang er varianten bestaan).
        var remaining = await db.Cards.CountAsync(
            c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != ""
                 && c.VariantOf == null, ct);
        return new(mined, remaining, failed);
    }
}
