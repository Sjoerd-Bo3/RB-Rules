using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Bouwt de rijen van het kosten-grootboek (#328): één
/// <see cref="AiUsageEvent"/> per gemeterde AI-gebeurtenis, mét de tariefversie
/// die op dat moment gold — zo blijft het schaduwbedrag later exact
/// reproduceerbaar als rij × tarief, óók nadat prijzen zijn bijgewerkt.
/// De schrijvers (AskService, mining, audit, primer) voegen de rij toe aan hun
/// eigen unit-of-work; meting mag daar nooit het echte werk blokkeren.</summary>
public static class AiUsageMeter
{
    public static async Task<AiUsageEvent> CreateEventAsync(
        RbRulesDbContext db, string origin, string kind, string model,
        long? userId, long? inputTokens, long? outputTokens,
        int durationMs, bool ok, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Tariefresolutie op schrijfmoment: recentste ingangsdatum ≤ nu voor
        // exact dit model; geen match = geen versie (nooit een gok).
        var tariffId = await db.AiTariffs.AsNoTracking()
            .Where(t => t.Model == model && t.EffectiveFrom <= now)
            .OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.Id)
            .Select(t => (long?)t.Id)
            .FirstOrDefaultAsync(ct);
        return new AiUsageEvent
        {
            Origin = origin,
            Kind = kind,
            Model = model,
            UserId = userId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            DurationMs = durationMs,
            Ok = ok,
            TariffVersion = tariffId,
            CreatedAt = now,
        };
    }
}
