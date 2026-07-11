using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class RuleEndpoints
{
    public static void MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Regels-browser ─────────────────────────────────────────────
        app.MapGet("/api/rules/toc", async (RuleBrowserService rules, CancellationToken ct) =>
            Results.Ok(await rules.TocAsync(ct)));

        // ── Hybride zoeken in de regelsecties (#72) ────────────────────
        app.MapGet("/api/rules/search", async (
            string? q, int? limit, RuleSearchService search, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is verplicht" });
            // Publiek endpoint: extreem lange invoer hoort niet de embedder in.
            var query = q.Trim();
            if (query.Length > 400) query = query[..400];
            var hits = await search.SearchAsync(query, Math.Clamp(limit ?? 10, 1, 30), ct);
            return Results.Ok(hits);
        });

        app.MapGet("/api/rules/section/{code}", async (
                string code, string? source, RuleBrowserService rules, CancellationToken ct) =>
            await rules.SectionAsync(code, source, ct) is { } section
                ? Results.Ok(section)
                : Results.NotFound());
    }
}
