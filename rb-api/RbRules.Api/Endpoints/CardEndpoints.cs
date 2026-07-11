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

        app.MapGet("/api/cards/{id}", async (string id, RbRulesDbContext db) =>
        {
            var c = await db.Cards.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RiftboundId == id);
            if (c is null) return Results.NotFound();
            // Ban geldt voor de hele variantgroep (#44) — een ban op één
            // printing is op alle printings zichtbaar.
            var bannedGroups = await BanLookup.BannedCanonicalIdsAsync(db);
            var banned = BanLookup.IsBanned(bannedGroups, c);
            var erratum = await db.Errata
                .Where(e => e.CardRiftboundId == id)
                .OrderByDescending(e => e.DetectedAt)
                .Select(e => e.NewText)
                .FirstOrDefaultAsync();
            // Alle printings van deze kaart (alt-art/showcase/promo/herdruk).
            var canonicalId = c.VariantOf ?? c.RiftboundId;
            var versions = await db.Cards
                .Where(x => x.RiftboundId != c.RiftboundId &&
                            (x.RiftboundId == canonicalId || x.VariantOf == canonicalId))
                .OrderBy(x => x.RiftboundId)
                .Select(x => new
                {
                    x.RiftboundId, x.SetId, x.SetLabel, x.Rarity, x.CollectorNumber, x.ImageUrl,
                })
                .ToListAsync();
            // Mining draait alleen op canonieke printings — varianten tonen de
            // analyse van hun canonieke kaart (zelfde tekst, zelfde spel-gedrag).
            var canonical = c.VariantOf is null ? null : await db.Cards.FindAsync(c.VariantOf);
            // Set-legaliteit (#22): status afgeleid van de releasedatum van de set.
            var set = c.SetId is null ? null : await db.CardSets.FindAsync(c.SetId);
            return Results.Ok(new
            {
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
                c.Energy, c.Might, c.Power, c.SetId, c.SetLabel, c.CollectorNumber,
                c.TextPlain, c.ImageUrl, c.Tags,
                Mechanics = c.Mechanics ?? canonical?.Mechanics,
                Triggers = c.Triggers ?? canonical?.Triggers,
                Effects = c.Effects ?? canonical?.Effects,
                c.UpdatedAt, Banned = banned, ErrataText = erratum,
                c.VariantOf, Versions = versions,
                LegalFrom = set?.PublishedOn,
                Legality = SetLegality.Key(SetLegality.StatusFor(
                    set?.PublishedOn, DateOnly.FromDateTime(DateTime.UtcNow))),
            });
        });

        // ── Interacties (S3) ───────────────────────────────────────────
        // Variantgroep-bewust (#57): een alt-art-pagina toont de interacties
        // van zijn canonieke kaart, en rijen van vóór de variantgroepering
        // die nog aan een variant-id hangen tellen gewoon mee.
        app.MapGet("/api/cards/{id}/interactions", async (string id, RbRulesDbContext db) =>
        {
            var card = await db.Cards.FindAsync(id);
            if (card is null) return Results.NotFound();
            var neighbors = await GroupInteractionsAsync(db, CardText.CanonicalId(card), take: 40);
            return Results.Ok(neighbors);
        });

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
            string id, int? limit, RbRulesDbContext db) =>
        {
            var card = await db.Cards.FindAsync(id);
            if (card is null) return Results.NotFound();
            // Varianten hebben geen eigen embedding — anker op de canonieke printing,
            // met als vangnet elke printing van dezelfde naam die wél geëmbed is.
            var anchorCard = card;
            if (anchorCard.Embedding is null && card.VariantOf is not null)
                anchorCard = await db.Cards.FindAsync(card.VariantOf) ?? card;
            if (anchorCard.Embedding is null)
                anchorCard = await db.Cards
                    .FirstOrDefaultAsync(c => c.Name == card.Name && c.Embedding != null) ?? anchorCard;
            if (anchorCard.Embedding is null)
                return Results.BadRequest(new { error = "kaart heeft nog geen embedding" });
            card = anchorCard;

            var anchor = card.Embedding;
            var rows = await db.Cards
                .Where(c => c.Embedding != null && c.RiftboundId != id
                            && c.VariantOf == null && c.Name != card.Name)
                .OrderBy(c => c.Embedding!.CosineDistance(anchor))
                .Take(Math.Clamp(limit ?? 10, 1, 30))
                .Select(c => new
                {
                    c.RiftboundId, c.Name, c.Type, c.Domains, c.Mechanics,
                    c.Energy, c.Might, c.ImageUrl,
                    Distance = c.Embedding!.CosineDistance(anchor),
                })
                .ToListAsync();

            // "Waarom vergelijkbaar": gedeelde facetten + tekst-gelijkenis expliciet maken.
            var results = rows.Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Domains, c.Energy, c.Might, c.ImageUrl,
                Similarity = Math.Round((1 - c.Distance) * 100),
                SharedMechanics = (c.Mechanics ?? []).Intersect(card.Mechanics ?? []).ToArray(),
                SharedDomains = c.Domains.Intersect(card.Domains).ToArray(),
                SameType = c.Type != null && c.Type == card.Type,
            });
            return Results.Ok(results);
        });

        // Waarom lijken twee kaarten op elkaar? LLM-uitleg met cache (#30).
        app.MapGet("/api/cards/{id}/similar/{otherId}/explain", async (
            string id, string otherId, RbRulesDbContext db, RbAiClient ai) =>
        {
            var (a, b) = CardText.OrderedPair(id, otherId);
            var cached = await db.SimilarityExplanations
                .FirstOrDefaultAsync(e => e.CardAId == a && e.CardBId == b);
            if (cached is not null) return Results.Ok(new { explanation = cached.Text, cached = true });

            var cardA = await db.Cards.FindAsync(id);
            var cardB = await db.Cards.FindAsync(otherId);
            if (cardA is null || cardB is null) return Results.NotFound();

            var raw = await ai.AskAsync(
                $"Kaart 1: {CardText.DescribeForPrompt(cardA)}\n\nKaart 2: {CardText.DescribeForPrompt(cardB)}",
                """
                Je legt in één of twee Nederlandse zinnen uit op welk semantisch vlak twee
                Riftbound-kaarten op elkaar lijken: welk gedrag, welke rol of welk
                spelplan delen ze? Wees concreet ("beide sturen units terug naar de
                base") en noem geen voor de hand liggende metadata zoals set of rarity.
                Antwoord met alleen die uitleg, zonder inleiding.
                """);
            if (raw is null)
                return Results.Problem(title: "AI niet beschikbaar", statusCode: 503);

            db.SimilarityExplanations.Add(new SimilarityExplanation
            {
                CardAId = a, CardBId = b, Text = raw.Trim(), Model = "rb-ai",
            });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* race met parallel verzoek — cache bestaat al */ }
            return Results.Ok(new { explanation = raw.Trim(), cached = false });
        }).RequireRateLimiting("llm");

        // Graph-verkenner (#29): buren van een kaart via gedeelde mechanieken,
        // domeinen en geverifieerde interacties. Een variant-id als center
        // resolvet naar de canonieke kaart (#57): mining en graph kennen
        // alleen canonieke printings, dus deeplinks vanaf een alt-art-pagina
        // blijven zo gewoon werken.
        app.MapGet("/api/graph/neighbors", async (string card, RbRulesDbContext db) =>
        {
            var center = await db.Cards.FindAsync(card);
            if (center is null) return Results.NotFound();
            if (center.VariantOf is not null)
                center = await db.Cards.FindAsync(center.VariantOf) ?? center;
            var centerId = center.RiftboundId;

            var mechanics = center.Mechanics ?? [];
            var mechanicGroups = new List<object>();
            foreach (var m in mechanics.Take(6))
            {
                var sharing = await db.Cards
                    .Where(c => c.RiftboundId != centerId && c.VariantOf == null &&
                                c.Mechanics != null && c.Mechanics.Contains(m))
                    .OrderBy(c => c.Name)
                    .Take(6)
                    .Select(c => new { c.RiftboundId, c.Name, c.ImageUrl })
                    .ToListAsync();
                mechanicGroups.Add(new { Mechanic = m, Cards = sharing });
            }

            var interactions = await GroupInteractionsAsync(db, centerId, take: 12);

            return Results.Ok(new
            {
                Center = new { center.RiftboundId, center.Name, center.ImageUrl, center.Domains },
                Mechanics = mechanicGroups,
                Interactions = interactions.Select(n => new { n.OtherId, n.OtherName, n.Kind }),
            });
        });

        // Regels & errata die bij deze kaart horen (voor de kaartpagina).
        app.MapGet("/api/cards/{id}/rules", async (string id, RbRulesDbContext db) =>
        {
            var card = await db.Cards.FindAsync(id);
            if (card is null) return Results.NotFound();

            var errata = await db.Errata
                .Where(e => e.CardRiftboundId == id)
                .OrderByDescending(e => e.DetectedAt)
                .Select(e => new { e.NewText, e.SourceUrl, e.DetectedAt })
                .ToListAsync();

            // Relevante regelsecties via de kaart-embedding (semantisch dichtstbij).
            // Varianten lenen de embedding van hun canonieke printing.
            var embeddingSource = card;
            if (embeddingSource.Embedding is null && card.VariantOf is not null)
                embeddingSource = await db.Cards.FindAsync(card.VariantOf) ?? card;
            object relevantRules = Array.Empty<object>();
            if (embeddingSource.Embedding is not null)
            {
                var anchor = embeddingSource.Embedding;
                relevantRules = await db.RuleChunks
                    .Where(c => c.Embedding != null && c.SectionCode != null)
                    .OrderBy(c => c.Embedding!.CosineDistance(anchor))
                    .Take(3)
                    .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
                    {
                        Section = c.SectionCode,
                        Snippet = c.Text.Substring(0, Math.Min(c.Text.Length, 260)),
                        SourceName = s.Name,
                        s.Url,
                    })
                    .ToListAsync();
            }

            return Results.Ok(new { Errata = errata, RelevantRules = relevantRules });
        });
    }

    /// <summary>Geverifieerde interacties variantgroep-bewust ophalen (#57):
    /// match op alle printing-ids van de groep (rijen van vóór de groepering
    /// kunnen nog variant-ids bevatten) en canonicaliseer de buren.</summary>
    private static async Task<List<InteractionNeighbor>> GroupInteractionsAsync(
        RbRulesDbContext db, string canonicalId, int take)
    {
        var groupIds = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == canonicalId || c.VariantOf == canonicalId)
            .Select(c => c.RiftboundId)
            .ToListAsync();
        if (groupIds.Count == 0) groupIds = [canonicalId];

        var rows = await db.CardInteractions.AsNoTracking()
            .Where(x => groupIds.Contains(x.CardAId) || groupIds.Contains(x.CardBId))
            .OrderBy(x => x.Kind)
            .Take(take)
            .ToListAsync();

        var groupSet = groupIds.ToHashSet();
        var otherIds = rows
            .Select(r => groupSet.Contains(r.CardAId) ? r.CardBId : r.CardAId)
            .ToList();
        // Projectie zonder embedding-vectoren (#43).
        var others = await db.Cards.AsNoTracking()
            .Where(c => otherIds.Contains(c.RiftboundId))
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf,
            })
            .ToDictionaryAsync(c => c.RiftboundId, c => c);

        return VariantGrouping.InteractionNeighbors(rows, groupSet, others);
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

    private static IQueryable<RbRules.Domain.Card> ApplyCardFilters(
        IQueryable<RbRules.Domain.Card> query,
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
