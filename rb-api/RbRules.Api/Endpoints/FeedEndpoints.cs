using Microsoft.EntityFrameworkCore;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Publiek ────────────────────────────────────────────────────
        app.MapGet("/api/sources", async (RbRulesDbContext db) =>
            await db.Sources
                .OrderBy(s => s.TrustTier).ThenByDescending(s => s.Rank)
                .ToListAsync());

        // #206: alleen primaire changes (secundaire/bevestigende changes
        // genest via ConfirmedBy) — de query zelf leeft in ChangeFeedService,
        // gedeeld met het admin-overzicht.
        app.MapGet("/api/changes", async (
                string? severity, string? type, string? source, ChangeFeedService feed) =>
            await feed.ListAsync(severity, type, source, take: 50));

        app.MapGet("/api/bans", async (RbRulesDbContext db) =>
            await db.BanEntries.OrderBy(b => b.Kind).ThenBy(b => b.Name).ToListAsync());

        // Aankomende-set-signaal (#52): sets met een bekende releasedatum in
        // de toekomst (SetLegality: upcoming). Voedt de banner in de feed en
        // het beheer — spelers zien wanneer nieuwe kaarten legaal worden.
        app.MapGet("/api/sets/upcoming", async (RbRulesDbContext db) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return await db.CardSets.AsNoTracking()
                .Where(s => s.PublishedOn != null && s.PublishedOn > today)
                .OrderBy(s => s.PublishedOn)
                .Select(s => new { s.SetId, s.Name, s.PublishedOn, s.CardCount })
                .ToListAsync();
        });
    }
}
