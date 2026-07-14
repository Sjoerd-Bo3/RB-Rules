using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Deck-browser (#15 fase 3, spoor A): read-only projectie boven op
/// de Piltover Archive-decks, met legaliteitscheck en deep-link naar de bron.
/// Géén editor, géén deck-mutatie. Dun endpoint — de logica leeft in
/// DeckBrowserService.</summary>
public static class DeckEndpoints
{
    public static void MapDeckEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/decks", async (
            string? domain, string? sort, int? page, string? format,
            DeckBrowserService decks, CancellationToken ct) =>
            Results.Ok(await decks.ListAsync(
                domain, sort, Math.Clamp(page ?? 1, 1, 5000),
                format ?? DeckBrowserService.DefaultFormat, ct)));

        app.MapGet("/api/decks/facets", async (DeckBrowserService decks, CancellationToken ct) =>
            Results.Ok(await decks.FacetsAsync(ct)));

        app.MapGet("/api/decks/{id}", async (
            string id, string? format, DeckBrowserService decks, CancellationToken ct) =>
            await decks.DetailAsync(id, format ?? DeckBrowserService.DefaultFormat, ct) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound());
    }
}
