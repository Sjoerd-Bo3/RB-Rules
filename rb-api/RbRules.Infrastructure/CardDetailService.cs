using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record CardVersion(
    string RiftboundId, string? SetId, string? SetLabel, string? Rarity,
    int? CollectorNumber, string? ImageUrl);

public record CardErratumRef(string NewText, string SourceUrl, DateTimeOffset DetectedAt);
public record CardRelevantRule(string? Section, string Snippet, string SourceName, string Url);
public record CardRuleLinks(
    IReadOnlyList<CardErratumRef> Errata, IReadOnlyList<CardRelevantRule> RelevantRules);

public record CardDetail(
    string RiftboundId, string Name, string? Type, string? Supertype,
    string? Rarity, string[] Domains, int? Energy, int? Might, int? Power,
    string? SetId, string? SetLabel, int? CollectorNumber, string? TextPlain,
    string? ImageUrl, string[] Tags, string[]? Mechanics, string[]? Triggers,
    string[]? Effects, DateTimeOffset UpdatedAt, bool Banned, string? ErrataText,
    string? VariantOf, IReadOnlyList<CardVersion> Versions,
    DateOnly? LegalFrom, string Legality);

/// <summary>Detail-opbouw van de kaartpagina (#59, uit het endpoint):
/// ban-status per variantgroep, laatste erratum, alle printings en de
/// canonical-fallback voor mining-resultaten.</summary>
public class CardDetailService(RbRulesDbContext db, CardResolver resolver)
{
    public async Task<CardDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        // Zonder embedding-vector (#43): de detailpagina toont kaartfeiten,
        // niet de 1024 floats.
        var c = await db.Cards.AsNoTracking()
            .Where(x => x.RiftboundId == id)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct);
        if (c is null) return null;

        // Ban geldt voor de hele variantgroep (#44) — een ban op één
        // printing is op alle printings zichtbaar.
        var bannedGroups = await BanLookup.BannedCanonicalIdsAsync(db, ct);
        var banned = BanLookup.IsBanned(bannedGroups, c);

        var erratum = await db.Errata
            .Where(e => e.CardRiftboundId == id)
            .OrderByDescending(e => e.DetectedAt)
            .Select(e => e.NewText)
            .FirstOrDefaultAsync(ct);

        // Alle printings van deze kaart (alt-art/showcase/promo/herdruk).
        var canonicalId = CardText.CanonicalId(c);
        var versions = await db.Cards
            .Where(x => x.RiftboundId != c.RiftboundId &&
                        (x.RiftboundId == canonicalId || x.VariantOf == canonicalId))
            .OrderBy(x => x.RiftboundId)
            .Select(x => new CardVersion(
                x.RiftboundId, x.SetId, x.SetLabel, x.Rarity, x.CollectorNumber, x.ImageUrl))
            .ToListAsync(ct);

        // Mining draait alleen op canonieke printings — varianten tonen de
        // analyse van hun canonieke kaart (zelfde tekst, zelfde spel-gedrag).
        var canonical = await resolver.CanonicalAsync(c, ct);

        // Set-legaliteit (#22): status afgeleid van de releasedatum van de set.
        var set = c.SetId is null ? null : await db.CardSets.FindAsync([c.SetId], ct);

        return new CardDetail(
            c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
            c.Energy, c.Might, c.Power, c.SetId, c.SetLabel, c.CollectorNumber,
            c.TextPlain, c.ImageUrl, c.Tags,
            c.Mechanics ?? canonical.Mechanics,
            c.Triggers ?? canonical.Triggers,
            c.Effects ?? canonical.Effects,
            c.UpdatedAt, banned, erratum, c.VariantOf, versions,
            set?.PublishedOn,
            SetLegality.Key(SetLegality.StatusFor(
                set?.PublishedOn, DateOnly.FromDateTime(DateTime.UtcNow))));
    }

    /// <summary>Regels & errata die bij een kaart horen (voor de kaartpagina):
    /// regelsecties semantisch dichtstbij de kaart-embedding; varianten lenen
    /// de embedding van hun canonieke printing (CardResolver). Null als de
    /// kaart niet bestaat.</summary>
    public async Task<CardRuleLinks?> RulesAsync(string id, CancellationToken ct = default)
    {
        var card = await db.Cards.FindAsync([id], ct);
        if (card is null) return null;

        var errata = await db.Errata
            .Where(e => e.CardRiftboundId == id)
            .OrderByDescending(e => e.DetectedAt)
            .Select(e => new CardErratumRef(e.NewText, e.SourceUrl, e.DetectedAt))
            .ToListAsync(ct);

        var embeddingSource = await resolver.EmbeddingAnchorAsync(card, ct);
        IReadOnlyList<CardRelevantRule> relevantRules = [];
        if (embeddingSource.Embedding is not null)
        {
            var anchor = embeddingSource.Embedding;
            relevantRules = await db.RuleChunks
                .Where(c => c.Embedding != null && c.SectionCode != null)
                .OrderBy(c => c.Embedding!.CosineDistance(anchor))
                .Take(3)
                .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new CardRelevantRule(
                    c.SectionCode,
                    c.Text.Substring(0, Math.Min(c.Text.Length, 260)),
                    s.Name,
                    s.Url))
                .ToListAsync(ct);
        }

        return new(errata, relevantRules);
    }
}
