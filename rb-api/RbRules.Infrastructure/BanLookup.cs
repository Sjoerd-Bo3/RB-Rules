using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Ban-semantiek per variantgroep (review-fix #44): een ban die aan
/// één printing hangt geldt voor álle printings van die kaart.</summary>
public static class BanLookup
{
    /// <summary>Canonieke groeps-id's waarvan (een printing van) de kaart op
    /// de banlijst staat, ongeacht format — voor de kaartpagina volstaat één
    /// ban-status voor de hele kaart.</summary>
    public static Task<HashSet<string>> BannedCanonicalIdsAsync(
        RbRulesDbContext db, CancellationToken ct = default) =>
        BannedCanonicalIdsAsync(db, format: null, ct);

    /// <summary>Zelfde opzoeking, maar geschaald naar één format (#15
    /// deck-browser: een deck is per format legaal/illegaal, dus een ban in
    /// een ander format telt daar niet mee). <paramref name="format"/> null
    /// betekent "elk format", net als de niet-geschaalde overload.</summary>
    public static async Task<HashSet<string>> BannedCanonicalIdsAsync(
        RbRulesDbContext db, string? format, CancellationToken ct = default)
    {
        var bans = db.BanEntries.AsNoTracking().Where(b => b.CardRiftboundId != null);
        if (format is not null) bans = bans.Where(b => b.Format == format);
        var rows = await bans
            .Join(db.Cards, b => b.CardRiftboundId, c => c.RiftboundId,
                (b, c) => c.VariantOf ?? c.RiftboundId)
            .ToListAsync(ct);
        return [.. rows];
    }

    public static bool IsBanned(HashSet<string> bannedCanonicals, Card c) =>
        bannedCanonicals.Contains(CardText.CanonicalId(c));
}
