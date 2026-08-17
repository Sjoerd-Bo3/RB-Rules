using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record SimilarCardItem(
    string RiftboundId, string Name, string? Type, string[] Domains,
    int? Energy, int? Might, string? ImageUrl, double Similarity,
    string[] SharedMechanics, string[] SharedDomains, bool SameType);

/// <summary>Uitkomst: <c>Found</c> = kaart bestaat; <c>HasEmbedding</c> =
/// er is een geëmbedde printing om op te ankeren.</summary>
public record SimilarCardsResult(
    bool Found, bool HasEmbedding, IReadOnlyList<SimilarCardItem> Items)
{
    public static readonly SimilarCardsResult NotFound = new(false, false, []);
    public static readonly SimilarCardsResult NoEmbedding = new(true, false, []);
}

/// <summary>Vergelijkbare kaarten (#59, uit het endpoint): canonical-anchor-
/// keten via CardResolver plus "waarom vergelijkbaar"-verrijking (gedeelde
/// mechanieken/domeinen, tekst-gelijkenis als percentage).</summary>
public class CardSimilarityService(RbRulesDbContext db, CardResolver resolver)
{
    public async Task<SimilarCardsResult> SimilarAsync(
        string id, int limit, CancellationToken ct = default)
    {
        var card = await db.Cards.FindAsync([id], ct);
        if (card is null) return SimilarCardsResult.NotFound;

        // Varianten hebben geen eigen embedding — anker op de canonieke
        // printing, met als vangnet elke geëmbedde printing van dezelfde naam.
        card = await resolver.EmbeddingAnchorAsync(card, ct);
        if (card.Embedding is null) return SimilarCardsResult.NoEmbedding;

        var anchor = card.Embedding;
        var rows = await db.Cards
            .Where(c => c.Embedding != null && c.RiftboundId != id
                        && c.VariantOf == null && c.Name != card.Name)
            .OrderBy(c => c.Embedding!.CosineDistance(anchor))
            .Take(limit)
            .Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Domains, c.Mechanics,
                c.Energy, c.Might, c.ImageUrl,
                Distance = c.Embedding!.CosineDistance(anchor),
            })
            .ToListAsync(ct);

        // "Waarom vergelijkbaar": gedeelde facetten + tekst-gelijkenis expliciet maken.
        var items = rows.Select(c => new SimilarCardItem(
                c.RiftboundId, c.Name, c.Type, c.Domains, c.Energy, c.Might, c.ImageUrl,
                Math.Round((1 - c.Distance) * 100),
                (c.Mechanics ?? []).Intersect(card.Mechanics ?? []).ToArray(),
                c.Domains.Intersect(card.Domains).ToArray(),
                c.Type != null && c.Type == card.Type))
            .ToList();
        return new(true, true, items);
    }
}
