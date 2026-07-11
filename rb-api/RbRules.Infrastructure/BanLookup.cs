using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Ban-semantiek per variantgroep (review-fix #44): een ban die aan
/// één printing hangt geldt voor álle printings van die kaart.</summary>
public static class BanLookup
{
    /// <summary>Canonieke groeps-id's waarvan (een printing van) de kaart op
    /// de banlijst staat.</summary>
    public static async Task<HashSet<string>> BannedCanonicalIdsAsync(
        RbRulesDbContext db, CancellationToken ct = default)
    {
        var rows = await db.BanEntries.AsNoTracking()
            .Where(b => b.CardRiftboundId != null)
            .Join(db.Cards, b => b.CardRiftboundId, c => c.RiftboundId,
                (b, c) => c.VariantOf ?? c.RiftboundId)
            .ToListAsync(ct);
        return [.. rows];
    }

    public static bool IsBanned(HashSet<string> bannedCanonicals, Card c) =>
        bannedCanonicals.Contains(CardText.CanonicalId(c));
}
