using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class CardEndpoints
{
    public static void MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cards", async (
            string? q, string? domain, string? type, string? set, string? rarity,
            string? mechanic, int? maxEnergy, int? page, bool? all,
            RbRulesDbContext db) =>
        {
            var query = db.Cards.AsQueryable();
            // Standaard één kaart per naam; alt-art/promo-printings tellen niet mee.
            if (all != true) query = query.Where(c => c.VariantOf == null);
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => EF.Functions.ILike(c.Name, $"%{q}%"));
            query = ApplyCardFilters(query, domain, type, set, rarity, mechanic, maxEnergy);

            const int pageSize = 60;
            var cards = await query.OrderBy(c => c.Name)
                .Skip(Math.Max(0, (page ?? 1) - 1) * pageSize)
                .Take(pageSize)
                .Select(c => new
                {
                    c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
                    c.Energy, c.Might, c.SetId, c.TextPlain, c.ImageUrl,
                })
                .ToListAsync();

            // Aantal extra printings per kaart (alt-art/promo) voor een subtiel label.
            var ids = cards.Select(c => c.RiftboundId).ToList();
            var variantCounts = await db.Cards
                .Where(c => c.VariantOf != null && ids.Contains(c.VariantOf))
                .GroupBy(c => c.VariantOf!)
                .Select(g => new { Id = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.N);
            var legality = await SetLegalityLookupAsync(db);
            return Results.Ok(cards.Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
                c.Energy, c.Might, c.SetId, c.TextPlain, c.ImageUrl,
                Variants = variantCounts.GetValueOrDefault(c.RiftboundId),
                LegalFrom = legality.DateOf(c.SetId),
                Legality = legality.KeyOf(c.SetId),
            }));
        });

        // Filteropties voor de kaart-browser (à la Piltover Archive), incl. onze
        // geminede mechanieken als extra facet.
        app.MapGet("/api/cards/facets", async (RbRulesDbContext db) =>
        {
            var rows = await db.Cards
                .Where(c => c.VariantOf == null)
                .Select(c => new { c.SetId, c.SetLabel, c.Type, c.Rarity, c.Domains, c.Mechanics })
                .ToListAsync();
            return Results.Ok(new
            {
                Sets = rows.Where(r => r.SetId != null)
                    .GroupBy(r => r.SetId!)
                    .Select(g => new { Id = g.Key, Label = g.Select(x => x.SetLabel).FirstOrDefault(l => l != null) ?? g.Key })
                    .OrderBy(s => s.Id),
                Types = rows.Select(r => r.Type).OfType<string>().Distinct().Order(),
                Rarities = rows.Select(r => r.Rarity).OfType<string>().Distinct().Order(),
                Domains = rows.SelectMany(r => r.Domains).Distinct().Order(),
                Mechanics = rows.SelectMany(r => r.Mechanics ?? []).Distinct().Order(),
            });
        });

        app.MapGet("/api/cards/{id}", async (string id, CardDetailService details) =>
            await details.GetAsync(id) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound());

        // ── Interacties (S3) ───────────────────────────────────────────
        // Variantgroep-bewust (#57): een alt-art-pagina toont de interacties
        // van zijn canonieke kaart.
        app.MapGet("/api/cards/{id}/interactions", async (
                string id, InteractionService interactions) =>
            await interactions.NeighborsForCardAsync(id, take: 40) is { } neighbors
                ? Results.Ok(neighbors)
                : Results.NotFound());

        // ── Semantisch kaartzoeken (S1) ────────────────────────────────
        app.MapGet("/api/cards/search", async (
            string q, string? domain, string? type, string? set, string? rarity,
            string? mechanic, int? maxEnergy, int? limit,
            RbRulesDbContext db, EmbeddingService embeddings) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is verplicht" });

            var queryVector = await embeddings.EmbedOneAsync(q);
            var cards = ApplyCardFilters(
                db.Cards.Where(c => c.Embedding != null && c.VariantOf == null),
                domain, type, set, rarity, mechanic, maxEnergy);

            var results = await cards
                .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
                .Take(Math.Clamp(limit ?? 20, 1, 60))
                .Select(c => new
                {
                    c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
                    c.Energy, c.Might, c.SetId, c.TextPlain, c.ImageUrl,
                    Distance = c.Embedding!.CosineDistance(queryVector),
                })
                .ToListAsync();
            var legality = await SetLegalityLookupAsync(db);
            return Results.Ok(results.Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
                c.Energy, c.Might, c.SetId, c.TextPlain, c.ImageUrl, c.Distance,
                LegalFrom = legality.DateOf(c.SetId),
                Legality = legality.KeyOf(c.SetId),
            }));
        });

        app.MapGet("/api/cards/{id}/similar", async (
            string id, int? limit, CardSimilarityService similarity) =>
        {
            var result = await similarity.SimilarAsync(id, Math.Clamp(limit ?? 10, 1, 30));
            if (!result.Found) return Results.NotFound();
            if (!result.HasEmbedding)
                return Results.BadRequest(new { error = "kaart heeft nog geen embedding" });
            return Results.Ok(result.Items);
        });

        // Waarom lijken twee kaarten op elkaar? LLM-uitleg met cache (#30).
        app.MapGet("/api/cards/{id}/similar/{otherId}/explain", async (
            string id, string otherId, SimilarityExplainService explain) =>
        {
            var result = await explain.ExplainAsync(id, otherId);
            if (!result.Found) return Results.NotFound();
            if (result.Explanation is null)
                return Results.Problem(title: "AI niet beschikbaar", statusCode: 503);
            return Results.Ok(new { explanation = result.Explanation, cached = result.Cached });
        }).RequireRateLimiting("llm");

        // Graph-verkenner (#29): buren van een kaart via gedeelde mechanieken,
        // domeinen en geverifieerde interacties.
        app.MapGet("/api/graph/neighbors", async (string card, GraphQueryService graph) =>
            await graph.NeighborsAsync(card) is { } neighbors
                ? Results.Ok(neighbors)
                : Results.NotFound());

        // Regels & errata die bij deze kaart horen (voor de kaartpagina).
        app.MapGet("/api/cards/{id}/rules", async (string id, CardDetailService details) =>
            await details.RulesAsync(id) is { } links
                ? Results.Ok(links)
                : Results.NotFound());
    }

    /// <summary>Set-releasedatums één keer laden (handvol rijen) en per kaart
    /// vertalen naar een legaliteitsstatus (#22).</summary>
    private static async Task<LegalityLookup> SetLegalityLookupAsync(RbRulesDbContext db)
    {
        var dates = await db.CardSets.AsNoTracking()
            .ToDictionaryAsync(s => s.SetId, s => s.PublishedOn);
        return new(dates, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private sealed record LegalityLookup(Dictionary<string, DateOnly?> Dates, DateOnly Today)
    {
        public DateOnly? DateOf(string? setId) =>
            setId is null ? null : Dates.GetValueOrDefault(setId);

        public string KeyOf(string? setId) =>
            SetLegality.Key(SetLegality.StatusFor(DateOf(setId), Today));
    }

    private static IQueryable<Card> ApplyCardFilters(
        IQueryable<Card> query,
        string? domain, string? type, string? set, string? rarity,
        string? mechanic, int? maxEnergy)
    {
        if (!string.IsNullOrWhiteSpace(domain)) query = query.Where(c => c.Domains.Contains(domain));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(c => c.Type == type);
        if (!string.IsNullOrWhiteSpace(set)) query = query.Where(c => c.SetId == set);
        if (!string.IsNullOrWhiteSpace(rarity)) query = query.Where(c => c.Rarity == rarity);
        if (!string.IsNullOrWhiteSpace(mechanic))
            query = query.Where(c => c.Mechanics != null && c.Mechanics.Contains(mechanic));
        if (maxEnergy is not null) query = query.Where(c => c.Energy != null && c.Energy <= maxEnergy);
        return query;
    }
}
