using RbRules.Domain;

namespace RbRules.Infrastructure;

public static class CardQueries
{
    /// <summary>Kaart-projectie zonder embedding (#43): de 1024-dim vector is
    /// ±4 KB per rij en read-paden die alleen kaartfeiten tonen of prompten
    /// hoeven die niet over de lijn te trekken. EmbeddingModel blijft ook
    /// leeg — provenance hoort bij de vector (NeedsEmbedding zou anders
    /// verkeerd oordelen over een geprojecteerde kaart). Zonder tracking:
    /// EF trackt projectie-resultaten niet.</summary>
    public static IQueryable<Card> WithoutEmbedding(this IQueryable<Card> query) =>
        query.Select(c => new Card
        {
            RiftboundId = c.RiftboundId,
            Name = c.Name,
            Type = c.Type,
            Supertype = c.Supertype,
            Rarity = c.Rarity,
            Domains = c.Domains,
            Energy = c.Energy,
            Might = c.Might,
            Power = c.Power,
            SetId = c.SetId,
            SetLabel = c.SetLabel,
            CollectorNumber = c.CollectorNumber,
            TextPlain = c.TextPlain,
            ImageUrl = c.ImageUrl,
            Tags = c.Tags,
            Mechanics = c.Mechanics,
            Triggers = c.Triggers,
            Effects = c.Effects,
            VariantOf = c.VariantOf,
            UpdatedAt = c.UpdatedAt,
        });
}
