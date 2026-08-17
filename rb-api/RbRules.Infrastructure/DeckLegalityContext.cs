using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>De platte feiten die <see cref="DeckLegality"/> nodig heeft, één
/// keer geladen per aanvraag: set-releasedatums (handvol rijen), de set per
/// canonieke kaart en de gebande canonieke kaarten voor dit format. Daarna is
/// elke kaartregel een dictionary-lookup in plaats van een eigen query.
/// Gedeeld door <see cref="DeckBrowserService"/> (opgeslagen PA-decks) en
/// <see cref="DeckCodeService"/> (een geplakte deck-code, #264) — dezelfde
/// legaliteitsuitspraak hoort uit dezelfde feiten te komen.</summary>
public sealed record DeckLegalityContext(
    Dictionary<string, DateOnly?> SetPublishedOn,
    Dictionary<string, string?> CanonicalCardSetId,
    HashSet<string> BannedCanonicalIds)
{
    public static async Task<DeckLegalityContext> LoadAsync(
        RbRulesDbContext db, string format, CancellationToken ct = default)
    {
        var setDates = await db.CardSets.AsNoTracking()
            .ToDictionaryAsync(s => s.SetId, s => s.PublishedOn, ct);
        var cardSets = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new { c.RiftboundId, c.SetId })
            .ToDictionaryAsync(c => c.RiftboundId, c => c.SetId, ct);
        var banned = await BanLookup.BannedCanonicalIdsAsync(db, format, ct);
        return new(setDates, cardSets, banned);
    }

    public DeckLegalityCard ToLegalityCard(string cardCode, string? cardName, string? canonicalId)
    {
        var setId = canonicalId is not null ? CanonicalCardSetId.GetValueOrDefault(canonicalId) : null;
        var publishedOn = setId is not null ? SetPublishedOn.GetValueOrDefault(setId) : null;
        var banned = canonicalId is not null && BannedCanonicalIds.Contains(canonicalId);
        return new(cardCode, cardName, canonicalId, publishedOn, banned);
    }
}
