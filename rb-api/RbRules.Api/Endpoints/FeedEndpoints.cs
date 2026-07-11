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

        app.MapGet("/api/changes", async (
            string? severity, string? type, string? source, RbRulesDbContext db) =>
        {
            var query = db.Changes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(c => c.Severity == severity);
            if (!string.IsNullOrWhiteSpace(type)) query = query.Where(c => c.ChangeType == type);
            if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);
            return await query
                .OrderByDescending(c => c.DetectedAt)
                .Take(50)
                .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
                {
                    c.Id, c.SourceId, c.ChangeType, c.Severity,
                    c.Summary, c.Meaning, c.Diff, c.DetectedAt,
                    SourceName = s.Name, SourceUrl = s.Url, s.TrustTier,
                })
                .ToListAsync();
        });

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
