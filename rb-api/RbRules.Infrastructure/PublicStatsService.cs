using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

/// <summary>Publieke telstanden voor het Overzicht-dashboard (#214): kaarten,
/// geverifieerde rulings, actieve bans en recente wijzigingen. Read-time, geen
/// kolom/migratie — vier goedkope COUNT-queries.</summary>
public record PublicStats(int Cards, int VerifiedRulings, int Bans, int RecentChanges);

public class PublicStatsService(RbRulesDbContext db)
{
    /// <summary>Venster voor "recente wijzigingen" — dezelfde primaire,
    /// niet-editoriale changes als de feed (#206/#207), binnen 14 dagen.</summary>
    public const int RecentDays = 14;

    public async Task<PublicStats> GetAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-RecentDays);
        // Canonieke kaarten (VariantOf == null) — alt-art/herdrukken tellen niet
        // dubbel, zelfde telling als de kaartbrowser toont.
        var cards = await db.Cards.CountAsync(c => c.VariantOf == null, ct);
        var rulings = await db.Corrections.CountAsync(c => c.Status == "verified", ct);
        var bans = await db.BanEntries.CountAsync(ct);
        var recent = await db.Changes.CountAsync(
            c => c.ConsolidatedWithId == null && c.ChangeType != "editorial" && c.DetectedAt >= since, ct);
        return new PublicStats(cards, rulings, bans, recent);
    }
}
