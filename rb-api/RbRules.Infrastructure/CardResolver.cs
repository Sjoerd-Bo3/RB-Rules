using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Canonieke-variant-fallbacks op één plek (#44/#59): varianten
/// (alt-art/promo) hebben geen eigen embedding of mining-resultaat en lenen
/// die van hun canonieke printing. Deze resolver vervangt de drie uiteen-
/// lopende kopieën in de endpoints (similar, kaart-regels, detail).</summary>
public class CardResolver(RbRulesDbContext db)
{
    /// <summary>De canonieke printing van een kaart; de kaart zelf als die al
    /// canoniek is of de canonieke printing (tijdelijk) ontbreekt. Zonder
    /// embedding-vector (#43) — de aanroepers tonen kaartfeiten; wie het
    /// embedding-anker nodig heeft gebruikt <see cref="EmbeddingAnchorAsync"/>.</summary>
    public async Task<Card> CanonicalAsync(Card card, CancellationToken ct = default)
    {
        if (card.VariantOf is null) return card;
        return await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == card.VariantOf)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct) ?? card;
    }

    /// <summary>Kaart om op te ankeren voor embedding-zoekopdrachten:
    /// de kaart zelf → de canonieke printing → elke printing met dezelfde
    /// naam die wél geëmbed is. Kan alsnog zonder embedding terugkomen
    /// (dan is de hele variantgroep nog niet geëmbed).</summary>
    public async Task<Card> EmbeddingAnchorAsync(Card card, CancellationToken ct = default)
    {
        var anchor = card;
        if (anchor.Embedding is null && card.VariantOf is not null)
            anchor = await db.Cards.FindAsync([card.VariantOf], ct) ?? card;
        if (anchor.Embedding is null)
            anchor = await db.Cards
                .FirstOrDefaultAsync(c => c.Name == card.Name && c.Embedding != null, ct)
                ?? anchor;
        return anchor;
    }
}
