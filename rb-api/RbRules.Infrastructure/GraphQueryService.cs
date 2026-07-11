using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphCardRef(string RiftboundId, string Name, string? ImageUrl);
public record GraphMechanicGroup(string Mechanic, IReadOnlyList<GraphCardRef> Cards);
public record GraphInteractionRef(string OtherId, string OtherName, string Kind);
public record GraphCenter(string RiftboundId, string Name, string? ImageUrl, string[] Domains);
public record GraphNeighbors(
    GraphCenter Center,
    IReadOnlyList<GraphMechanicGroup> Mechanics,
    IReadOnlyList<GraphInteractionRef> Interactions);

/// <summary>Graph-verkenner (#29, #59 — uit het endpoint): buren van een
/// kaart via gedeelde mechanieken en geverifieerde interacties. Een
/// variant-id als center resolvet naar de canonieke kaart (#57): mining en
/// graph kennen alleen canonieke printings, dus deeplinks vanaf een
/// alt-art-pagina blijven gewoon werken.</summary>
public class GraphQueryService(
    RbRulesDbContext db, CardResolver resolver, InteractionService interactions)
{
    public async Task<GraphNeighbors?> NeighborsAsync(
        string cardId, CancellationToken ct = default)
    {
        // Read-only en zonder embedding-vector (#43): het center toont naam,
        // beeld, domeinen en mechanieken — geen 1024 floats, geen tracking.
        var center = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == cardId)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct);
        if (center is null) return null;
        center = await resolver.CanonicalAsync(center, ct);
        var centerId = center.RiftboundId;

        var mechanics = center.Mechanics ?? [];
        var mechanicGroups = new List<GraphMechanicGroup>();
        foreach (var m in mechanics.Take(6))
        {
            var sharing = await db.Cards
                .Where(c => c.RiftboundId != centerId && c.VariantOf == null &&
                            c.Mechanics != null && c.Mechanics.Contains(m))
                .OrderBy(c => c.Name)
                .Take(6)
                .Select(c => new GraphCardRef(c.RiftboundId, c.Name, c.ImageUrl))
                .ToListAsync(ct);
            mechanicGroups.Add(new(m, sharing));
        }

        var neighbors = await interactions.NeighborsAsync(centerId, take: 12, ct);

        return new(
            new(center.RiftboundId, center.Name, center.ImageUrl, center.Domains),
            mechanicGroups,
            [.. neighbors.Select(n => new GraphInteractionRef(n.OtherId, n.OtherName, n.Kind))]);
    }
}
