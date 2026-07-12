using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Publieke rulings-databank (#127): één doorzoekbare collectie van
/// geverifieerde rulings en officieel bevestigde community-claims, met
/// filters op onderwerp-type en per item de volledige bewijsketen. Dun
/// endpoint — de logica leeft in RulingsService. Geen LLM-calls (alleen
/// DB-reads + één best-effort embed), dus geen llm-rate-limit nodig.</summary>
public static class RulingsEndpoints
{
    public static void MapRulingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/rulings", async (
            string? q, string? topic, int? page, RulingsService rulings, CancellationToken ct) =>
        {
            if (!RulingsTopics.TryParseFilter(topic, out var topicFilter, out var fout))
                return Results.BadRequest(new { error = fout });
            // Publiek endpoint: extreem lange invoer hoort niet de embedder in
            // (zelfde cap als /api/rules/search).
            var query = q?.Trim();
            if (query?.Length > 400) query = query[..400];
            return Results.Ok(await rulings.QueryAsync(
                query, topicFilter, Math.Clamp(page ?? 1, 1, 500), ct));
        });
    }
}
