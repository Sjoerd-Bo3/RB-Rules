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
            string? domain, string? sort, int? page, string? format, string? card,
            string? legality, string? q,
            DeckBrowserService decks, CancellationToken ct) =>
            Results.Ok(await decks.ListAsync(
                domain, sort, Math.Clamp(page ?? 1, 1, 5000),
                format ?? DeckBrowserService.DefaultFormat, card, legality, q, ct)));

        app.MapGet("/api/decks/facets", async (DeckBrowserService decks, CancellationToken ct) =>
            Results.Ok(await decks.FacetsAsync(ct)));

        // Deck-code-import (#264): leest een geplakte code uit en projecteert
        // hem op onze kaarten mét legaliteitsoordeel. Elke ongeldige code is
        // een 400 met de uitleg van DeckCodeException — nooit een 500.
        app.MapPost("/api/decks/decode", async (
            DeckDecodeRequest req, DeckCodeService codes, CancellationToken ct) =>
        {
            var result = await codes.DecodeAsync(
                req.Code, req.Format ?? DeckBrowserService.DefaultFormat, ct);
            return result.Deck is { } deck
                ? Results.Ok(deck)
                : Results.Problem(
                    title: "Ongeldige deck-code", detail: result.Error, statusCode: 400);
        });

        // On-demand deck ophalen (#346): staat het deck al in de DB dan een
        // gewone read, anders één gerichte Piltover-fetch via het ingest-pad.
        // De status-mapping (400/404/502) is een service-beslissing; de
        // rate-limit is er omdat elke miss een externe request kost — wij
        // zijn te gast bij Piltover Archive.
        app.MapPost("/api/decks/fetch", async (
            DeckFetchRequest req, DeckFetchService fetch, CancellationToken ct) =>
        {
            var result = await fetch.FetchAsync(
                req.Id, req.Format ?? DeckBrowserService.DefaultFormat, ct);
            return result.Deck is { } deck
                ? Results.Ok(deck)
                : Results.Problem(
                    title: result.Status switch
                    {
                        400 => "Ongeldige deck-id",
                        404 => "Deck niet gevonden",
                        _ => "Piltover Archive niet bereikbaar",
                    },
                    detail: result.Error, statusCode: result.Status);
        }).RequireRateLimiting("deckfetch");

        app.MapGet("/api/decks/{id}", async (
            string id, string? format, DeckBrowserService decks, CancellationToken ct) =>
            await decks.DetailAsync(id, format ?? DeckBrowserService.DefaultFormat, ct) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound());
    }
}
