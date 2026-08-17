using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Kennisgraaf-endpoints (#377). Dun: alle query-logica zit in
/// GraphQueryService, conform CONVENTIONS.md.</summary>
public static class GraphEndpoints
{
    public static void MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        // Verkenner: buren van een kaart — mechanieken, afgeleide regels,
        // geverifieerde interacties, errata en bans.
        app.MapGet("/api/graph/neighbors", async (string card, GraphQueryService graph) =>
        {
            var result = await graph.NeighborsAsync(card);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // Bewijsketen tussen twee kaarten: via welke knopen hangen ze samen?
        app.MapGet("/api/graph/paths", async (
            string from, string to, int? maxLength, GraphQueryService graph) =>
            Results.Ok(await graph.PathsAsync(from, to, maxLength ?? 4)));

        // De ontologie zelf: welke knooptypen en relaties kent de graaf, en
        // wat wordt afgeleid. Voedt de uitlegpagina en later de brein-API.
        app.MapGet("/api/graph/ontology", () => Results.Ok(new
        {
            IdentityRule = GraphOntology.IdentityRule,
            NodeTypes = Enum.GetNames<NodeType>(),
            Edges = GraphOntology.Edges.Select(e => new
            {
                Type = e.Type.ToString(),
                From = e.From.ToString(),
                To = e.To.ToString(),
                e.Description,
                Inferred = GraphOntology.IsInferred(e.Type),
            }),
        }));
    }
}
