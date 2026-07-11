using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Api;
using RbRules.Domain;
using RbRules.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Database=rbrules;Username=rbrules;Password=rbrules";

builder.Services.AddDbContext<RbRulesDbContext>(o => o
    .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<IDriver>(_ => GraphDatabase.Driver(
    Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687",
    AuthTokens.Basic(
        Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j",
        Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "neo4j")));

builder.Services.AddHttpClient<RbAiClient>(c =>
{
    c.BaseAddress = new Uri(Environment.GetEnvironmentVariable("RB_AI_URL") ?? "http://localhost:8090");
    c.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddHttpClient<IngestService>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<CardSyncService>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient<EmbeddingService>(c =>
{
    c.BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434");
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddScoped<CardEmbeddingPipeline>();
builder.Services.AddScoped<MechanicMiningService>();
builder.Services.AddScoped<GraphSyncService>();
builder.Services.AddScoped<RuleChunkPipeline>();
builder.Services.AddScoped<AskService>();
builder.Services.AddScoped<BanErrataSyncService>();
builder.Services.AddScoped<InteractionService>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService<ScanScheduler>();

builder.Services.AddOpenApi();

var app = builder.Build();

// Migraties, source-seed en graph-constraints bij start. Graph is best-effort:
// de API blijft bruikbaar als Neo4j even weg is; DB-migratie is hard vereist.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RbRulesDbContext>();
    await db.Database.MigrateAsync();

    // Seed alleen ontbrekende bronnen — /admin blijft de bron van waarheid.
    var existing = await db.Sources.Select(s => s.Id).ToHashSetAsync();
    foreach (var src in SourceSeed.Defaults.Where(s => !existing.Contains(s.Id)))
        db.Sources.Add(src);
    await db.SaveChangesAsync();

    try
    {
        await GraphSchema.EnsureAsync(scope.ServiceProvider.GetRequiredService<IDriver>());
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Neo4j-constraints niet toegepast (Neo4j onbereikbaar?)");
    }
}

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "rb-api" }));

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
    return Results.Ok(cards.Select(c => new
    {
        c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
        c.Energy, c.Might, c.SetId, c.TextPlain, c.ImageUrl,
        Variants = variantCounts.GetValueOrDefault(c.RiftboundId),
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
    var banned = await db.BanEntries.AnyAsync(b => b.CardRiftboundId == id);
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
    });
});

// ── Rulings-Q&A (S2): hybrid retrieval + §-citaten ─────────────
app.MapPost("/api/ask", async (AskRequest req, AskService ask) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "question is verplicht" });
    var result = await ask.AskAsync(req.Question.Trim());
    return Results.Ok(result);
});

app.MapGet("/api/bans", async (RbRulesDbContext db) =>
    await db.BanEntries.OrderBy(b => b.Kind).ThenBy(b => b.Name).ToListAsync());

// ── Interacties (S3) ───────────────────────────────────────────
app.MapPost("/api/resolve", async (ResolveRequest req, InteractionService interactions) =>
{
    if (req.CardIds is not { Length: >= 2 and <= 3 })
        return Results.BadRequest(new { error = "geef 2 of 3 card-ids" });
    var result = await interactions.ResolveAsync(req.CardIds);
    return result is null
        ? Results.BadRequest(new { error = "kaarten niet gevonden" })
        : Results.Ok(result);
});

app.MapGet("/api/cards/{id}/interactions", async (string id, RbRulesDbContext db) =>
{
    var rows = await db.CardInteractions
        .Where(x => x.CardAId == id || x.CardBId == id)
        .OrderBy(x => x.Kind)
        .Take(40)
        .ToListAsync();
    var otherIds = rows.Select(r => r.CardAId == id ? r.CardBId : r.CardAId).ToList();
    var names = await db.Cards
        .Where(c => otherIds.Contains(c.RiftboundId))
        .ToDictionaryAsync(c => c.RiftboundId, c => c.Name);
    return Results.Ok(rows.Select(r =>
    {
        var otherId = r.CardAId == id ? r.CardBId : r.CardAId;
        return new
        {
            OtherId = otherId,
            OtherName = names.GetValueOrDefault(otherId, otherId),
            r.Kind,
            r.Explanation,
        };
    }));
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
    return Results.Ok(results);
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
    var (a, b) = string.CompareOrdinal(id, otherId) < 0 ? (id, otherId) : (otherId, id);
    var cached = await db.SimilarityExplanations
        .FirstOrDefaultAsync(e => e.CardAId == a && e.CardBId == b);
    if (cached is not null) return Results.Ok(new { explanation = cached.Text, cached = true });

    var cardA = await db.Cards.FindAsync(id);
    var cardB = await db.Cards.FindAsync(otherId);
    if (cardA is null || cardB is null) return Results.NotFound();

    string Describe(RbRules.Domain.Card c) =>
        $"{c.Name} ({c.Supertype} {c.Type}, {string.Join("/", c.Domains)}, energy {c.Energy?.ToString() ?? "—"})" +
        (c.Mechanics is { Length: > 0 } m ? $", mechanieken: {string.Join(", ", m)}" : "") +
        (c.TextPlain is null ? "" : $"\nTekst: {c.TextPlain}");

    var raw = await ai.AskAsync(
        $"Kaart 1: {Describe(cardA)}\n\nKaart 2: {Describe(cardB)}",
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
});

// Graph-verkenner (#29): buren van een kaart via gedeelde mechanieken,
// domeinen en geverifieerde interacties.
app.MapGet("/api/graph/neighbors", async (string card, RbRulesDbContext db) =>
{
    var center = await db.Cards.FindAsync(card);
    if (center is null) return Results.NotFound();

    var mechanics = center.Mechanics ?? [];
    var mechanicGroups = new List<object>();
    foreach (var m in mechanics.Take(6))
    {
        var sharing = await db.Cards
            .Where(c => c.RiftboundId != card && c.VariantOf == null &&
                        c.Mechanics != null && c.Mechanics.Contains(m))
            .OrderBy(c => c.Name)
            .Take(6)
            .Select(c => new { c.RiftboundId, c.Name, c.ImageUrl })
            .ToListAsync();
        mechanicGroups.Add(new { Mechanic = m, Cards = sharing });
    }

    var interactions = await db.CardInteractions
        .Where(x => x.CardAId == card || x.CardBId == card)
        .Take(12)
        .ToListAsync();
    var otherIds = interactions
        .Select(x => x.CardAId == card ? x.CardBId : x.CardAId)
        .ToList();
    var names = await db.Cards
        .Where(c => otherIds.Contains(c.RiftboundId))
        .ToDictionaryAsync(c => c.RiftboundId, c => c.Name);

    return Results.Ok(new
    {
        Center = new { center.RiftboundId, center.Name, center.ImageUrl, center.Domains },
        Mechanics = mechanicGroups,
        Interactions = interactions.Select(x =>
        {
            var otherId = x.CardAId == card ? x.CardBId : x.CardAId;
            return new
            {
                OtherId = otherId,
                OtherName = names.GetValueOrDefault(otherId, otherId),
                x.Kind,
            };
        }),
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

// ── Web-push (#28): meldingen bij belangrijke wijzigingen ──────
app.MapGet("/api/push/vapid", (PushService push) =>
    push.Enabled
        ? Results.Ok(new { publicKey = push.PublicKey })
        : Results.NotFound(new { error = "push niet geconfigureerd (VAPID-keys ontbreken)" }));

app.MapPost("/api/push/subscribe", async (PushSubscribe body, RbRulesDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(body.Endpoint) ||
        string.IsNullOrWhiteSpace(body.P256dh) || string.IsNullOrWhiteSpace(body.Auth))
        return Results.BadRequest(new { error = "endpoint, p256dh en auth zijn verplicht" });
    // SSRF-guard: alleen echte https-push-endpoints, geen interne adressen.
    if (!Uri.TryCreate(body.Endpoint, UriKind.Absolute, out var uri) ||
        uri.Scheme != "https" || uri.IsLoopback ||
        System.Net.IPAddress.TryParse(uri.Host, out _))
        return Results.BadRequest(new { error = "ongeldig push-endpoint" });

    var existing = await db.PushSubscriptions.FindAsync(body.Endpoint);
    if (existing is null)
    {
        db.PushSubscriptions.Add(new RbRules.Domain.PushSubscription
        {
            Endpoint = body.Endpoint, P256dh = body.P256dh, Auth = body.Auth,
        });
    }
    else
    {
        existing.P256dh = body.P256dh;
        existing.Auth = body.Auth;
    }
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException) { /* dubbele gelijktijdige subscribe — al geregistreerd */ }
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/push/unsubscribe", async (PushUnsubscribe body, RbRulesDbContext db) =>
{
    await db.PushSubscriptions.Where(s => s.Endpoint == body.Endpoint).ExecuteDeleteAsync();
    return Results.Ok(new { ok = true });
});

// ── Feedback op antwoorden (self-learning, #24) ────────────────
app.MapPost("/api/corrections", async (CorrectionSubmit body, RbRulesDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(body.Question) || body.Question.Length > 2000)
        return Results.BadRequest(new { error = "question ontbreekt of is te lang" });
    if (body.Verdict is not ("up" or "down"))
        return Results.BadRequest(new { error = "verdict moet 'up' of 'down' zijn" });
    var text = string.IsNullOrWhiteSpace(body.Text)
        ? (body.Verdict == "up" ? "Door gebruiker als juist bevestigd." : "Door gebruiker als onjuist gemarkeerd.")
        : body.Text.Trim();
    if (text.Length > 4000)
        return Results.BadRequest(new { error = "correctie is te lang (max 4000 tekens)" });
    // Spam-rem: de reviewqueue is handwerk — cap de open items.
    if (await db.Corrections.CountAsync(c => c.Status == "unverified") >= 500)
        return Results.StatusCode(429);

    db.Corrections.Add(new Correction
    {
        Scope = "answer",
        Ref = body.Verdict,
        Text = text,
        Question = body.Question.Trim(),
        Provenance = "web-feedback",
    });
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ── Regels-browser ─────────────────────────────────────────────
app.MapGet("/api/rules/toc", async (RbRulesDbContext db) =>
{
    var rows = await db.RuleChunks
        .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
        .OrderBy(c => c.ChunkIndex)
        .Select(c => new
        {
            c.SourceId, c.SectionCode, c.ChunkIndex,
            Preview = c.Text.Substring(0, Math.Min(c.Text.Length, 140)),
        })
        .ToListAsync();
    var sources = await db.Sources.ToDictionaryAsync(s => s.Id, s => s.Name);
    var toc = rows
        .GroupBy(r => r.SourceId)
        .Select(g => new
        {
            SourceId = g.Key,
            SourceName = sources.GetValueOrDefault(g.Key, g.Key),
            Sections = g.GroupBy(r => r.SectionCode!)
                .Select(sg => new
                {
                    Code = sg.Key,
                    Preview = sg.OrderBy(x => x.ChunkIndex).First().Preview,
                    Index = sg.Min(x => x.ChunkIndex),
                })
                .OrderBy(s => s.Index)
                .Select(s => new { s.Code, s.Preview })
                .ToList(),
        })
        .OrderBy(g => g.SourceId);
    return Results.Ok(toc);
});

app.MapGet("/api/rules/section/{code}", async (string code, string? source, RbRulesDbContext db) =>
{
    var query = db.RuleChunks.Where(c => c.SectionCode == code);
    if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);
    var chunks = await query
        .OrderBy(c => c.ChunkIndex)
        .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
        {
            c.SourceId, SourceName = s.Name, SourceUrl = s.Url, c.ChunkIndex, c.Text,
        })
        .ToListAsync();
    if (chunks.Count == 0) return Results.NotFound();

    // Bij codes die in meerdere bronnen voorkomen: houd één bron aan.
    var srcId = chunks[0].SourceId;
    chunks = [.. chunks.Where(c => c.SourceId == srcId)];

    // Buursecties in leesvolgorde van dezelfde bron.
    var codes = await db.RuleChunks
        .Where(c => c.SourceId == srcId && c.SectionCode != null &&
                    c.SectionCode != "" && c.SectionCode != "intro")
        .OrderBy(c => c.ChunkIndex)
        .Select(c => c.SectionCode!)
        .ToListAsync();
    var distinct = codes.Distinct().ToList();
    var idx = distinct.IndexOf(code);

    return Results.Ok(new
    {
        Code = code,
        SourceId = srcId,
        chunks[0].SourceName,
        chunks[0].SourceUrl,
        Text = string.Join("\n\n", chunks.Select(c => c.Text)),
        Prev = idx > 0 ? distinct[idx - 1] : null,
        Next = idx >= 0 && idx < distinct.Count - 1 ? distinct[idx + 1] : null,
    });
});

// ── Beheer (X-Admin-Key) ───────────────────────────────────────
var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

admin.MapGet("/ping", () => Results.Ok(new { ok = true }));

admin.MapPost("/scan", async (string? sourceId, IngestService ingest) =>
    Results.Ok(await ingest.ScanAsync(onlyDue: false, sourceId)));

admin.MapPost("/cards/sync", async (CardSyncService cards, RbRulesDbContext db) =>
{
    try
    {
        var r = await cards.SyncAsync();
        db.RunLogs.Add(new RunLog
        {
            Kind = "cards", Ref = r.Source, Status = "ok",
            Detail = $"{r.Sets} sets, {r.Cards} kaarten",
        });
        await db.SaveChangesAsync();
        return Results.Ok(r);
    }
    catch (Exception ex)
    {
        // Nooit een kale 500 — de fout hoort zichtbaar te zijn in admin/logs.
        db.ChangeTracker.Clear();
        db.RunLogs.Add(new RunLog { Kind = "cards", Ref = "sync", Status = "error", Detail = ex.Message });
        await db.SaveChangesAsync();
        return Results.Problem(title: "Kaarten-sync mislukt", detail: ex.Message, statusCode: 502);
    }
});

admin.MapPost("/cards/embed", async (
    bool? force, CardEmbeddingPipeline pipeline, RbRulesDbContext db) =>
{
    try
    {
        var r = await pipeline.RunAsync(force ?? false);
        db.RunLogs.Add(new RunLog
        {
            Kind = "embed", Ref = "cards", Status = "ok",
            Detail = $"{r.Embedded} geembed, {r.Skipped} al actueel",
        });
        await db.SaveChangesAsync();
        return Results.Ok(r);
    }
    catch (Exception ex)
    {
        db.RunLogs.Add(new RunLog { Kind = "embed", Ref = "cards", Status = "error", Detail = ex.Message });
        await db.SaveChangesAsync();
        return Results.Problem(ex.Message);
    }
});

admin.MapPost("/cards/mine", async (
    int? maxBatches, MechanicMiningService mining, RbRulesDbContext db) =>
{
    var r = await mining.RunAsync(Math.Clamp(maxBatches ?? 25, 1, 200));
    db.RunLogs.Add(new RunLog
    {
        Kind = "mine", Ref = "mechanics", Status = r.Failed > 0 ? "info" : "ok",
        Detail = $"{r.Mined} gemined, {r.Failed} mislukt, {r.Remaining} resterend",
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
});

admin.MapPost("/graph/sync", async (GraphSyncService graph, RbRulesDbContext db) =>
{
    try
    {
        var r = await graph.SyncAsync();
        db.RunLogs.Add(new RunLog
        {
            Kind = "graph", Ref = null, Status = "ok",
            Detail = $"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics",
        });
        await db.SaveChangesAsync();
        return Results.Ok(r);
    }
    catch (Exception ex)
    {
        db.RunLogs.Add(new RunLog { Kind = "graph", Ref = null, Status = "error", Detail = ex.Message });
        await db.SaveChangesAsync();
        return Results.Problem(ex.Message);
    }
});

admin.MapPost("/rules/index", async (RuleChunkPipeline pipeline, RbRulesDbContext db) =>
{
    try
    {
        var results = await pipeline.RunAsync();
        var total = results.Sum(r => r.Chunks);
        db.RunLogs.Add(new RunLog
        {
            Kind = "embed", Ref = "rules", Status = "ok",
            Detail = $"{results.Count} bronnen, {total} sectie-chunks",
        });
        await db.SaveChangesAsync();
        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        db.RunLogs.Add(new RunLog { Kind = "embed", Ref = "rules", Status = "error", Detail = ex.Message });
        await db.SaveChangesAsync();
        return Results.Problem(ex.Message);
    }
});

admin.MapPost("/bans/sync", async (BanErrataSyncService sync, RbRulesDbContext db) =>
{
    var r = await sync.SyncAsync();
    db.RunLogs.Add(new RunLog
    {
        Kind = "bans", Ref = null, Status = r.Bans + r.Errata > 0 ? "ok" : "info",
        Detail = $"{r.Bans} bans, {r.Errata} errata gestructureerd",
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
});

admin.MapPost("/interactions/mine", async (
    int? max, InteractionService interactions, RbRulesDbContext db) =>
{
    var r = await interactions.MineAsync(Math.Clamp(max ?? 60, 1, 300));
    db.RunLogs.Add(new RunLog
    {
        Kind = "mine", Ref = "interactions", Status = "ok",
        Detail = $"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd",
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
});

// ── Levendige admin: async jobs + live status ──────────────────
admin.MapPost("/jobs/{name}", (string name, JobRunner jobs) =>
{
    Func<IServiceProvider, Action<string>, CancellationToken, Task<string>>? work = name switch
    {
        "scan" => async (sp, report, ct) =>
        {
            var scanStart = DateTimeOffset.UtcNow;
            var r = await sp.GetRequiredService<IngestService>()
                .ScanAsync(onlyDue: false, progress: report, ct: ct);
            // Ook handmatige scans sturen pushmeldingen bij high-severity.
            try
            {
                var db = sp.GetRequiredService<RbRulesDbContext>();
                var push = sp.GetRequiredService<PushService>();
                var important = await db.Changes
                    .Where(c => c.DetectedAt >= scanStart && c.Severity == "high")
                    .ToListAsync(ct);
                foreach (var c in important)
                    await push.SendToAllAsync(db, "Belangrijke Riftbound-wijziging",
                        c.Summary ?? c.ChangeType, "https://riftbound-v2.bo3.dev/", ct);
            }
            catch
            {
                // push is best-effort
            }
            return string.Join(", ", r.Select(x => $"{x.SourceId}={x.Status}"));
        },
        "cards" => async (sp, report, ct) =>
        {
            var r = await sp.GetRequiredService<CardSyncService>().SyncAsync(report, ct);
            return $"{r.Sets} sets, {r.Cards} kaarten via {r.Source}";
        },
        "embed" => async (sp, report, ct) =>
        {
            var r = await sp.GetRequiredService<CardEmbeddingPipeline>()
                .RunAsync(progress: report, ct: ct);
            return $"{r.Embedded} kaarten geembed, {r.Skipped} al actueel";
        },
        "mine" => async (sp, report, ct) =>
        {
            var r = await sp.GetRequiredService<MechanicMiningService>()
                .RunAsync(progress: report, ct: ct);
            return $"{r.Mined} kaarten gemined, {r.Remaining} resterend";
        },
        "rules" => async (sp, report, ct) =>
        {
            var r = await sp.GetRequiredService<RuleChunkPipeline>().RunAsync(report, ct);
            return $"{r.Sum(x => x.Chunks)} sectie-chunks over {r.Count} bronnen";
        },
        "bans" => async (sp, report, ct) =>
        {
            report("officiële documenten structureren via LLM");
            var r = await sp.GetRequiredService<BanErrataSyncService>().SyncAsync(ct);
            return $"{r.Bans} bans, {r.Errata} errata gestructureerd";
        },
        "graph" => async (sp, report, ct) =>
        {
            report("kaarten, domeinen, tags en mechanieken naar Neo4j schrijven");
            var r = await sp.GetRequiredService<GraphSyncService>().SyncAsync(ct);
            return $"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics";
        },
        "interactions" => async (sp, report, ct) =>
        {
            var r = await sp.GetRequiredService<InteractionService>()
                .MineAsync(progress: report, ct: ct);
            return $"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd";
        },
        _ => null,
    };
    if (work is null) return Results.NotFound(new { error = $"onbekende job '{name}'" });
    return jobs.TryStart(name, work)
        ? Results.Accepted("/api/admin/status", new { started = name })
        : Results.Conflict(new { error = "er draait al een job — wacht tot die klaar is" });
});

admin.MapGet("/status", async (JobRunner jobs, RbRulesDbContext db) =>
{
    var (running, last) = jobs.Snapshot();
    return Results.Ok(new
    {
        Running = running,
        LastJob = last,
        Counts = new
        {
            Sources = await db.Sources.CountAsync(s => s.Enabled),
            Changes = await db.Changes.CountAsync(),
            Cards = await db.Cards.CountAsync(),
            CardsEmbedded = await db.Cards.CountAsync(c => c.Embedding != null),
            CardsMined = await db.Cards.CountAsync(c => c.Mechanics != null),
            RuleChunks = await db.RuleChunks.CountAsync(),
            Bans = await db.BanEntries.CountAsync(),
            Errata = await db.Errata.CountAsync(),
            Interactions = await db.CardInteractions.CountAsync(),
            OpenCorrections = await db.Corrections.CountAsync(c => c.Status == "unverified"),
        },
        Logs = await db.RunLogs.OrderByDescending(l => l.CreatedAt).Take(15).ToListAsync(),
    });
});

admin.MapGet("/logs", async (string? kind, RbRulesDbContext db) =>
{
    var query = db.RunLogs.AsQueryable();
    if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(l => l.Kind == kind);
    return await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync();
});

admin.MapPost("/sources", async (Source src, RbRulesDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(src.Id) || string.IsNullOrWhiteSpace(src.Url))
        return Results.BadRequest(new { error = "id en url zijn verplicht" });
    db.Sources.Add(src);
    await db.SaveChangesAsync();
    return Results.Created($"/api/sources/{src.Id}", src);
});

admin.MapPatch("/sources/{id}", async (string id, SourcePatch patch, RbRulesDbContext db) =>
{
    var src = await db.Sources.FindAsync(id);
    if (src is null) return Results.NotFound();
    if (patch.Name is not null) src.Name = patch.Name;
    if (patch.Url is not null) src.Url = patch.Url;
    if (patch.TrustTier is not null) src.TrustTier = patch.TrustTier.Value;
    if (patch.Rank is not null) src.Rank = patch.Rank.Value;
    if (patch.Cadence is not null) src.Cadence = patch.Cadence;
    if (patch.Enabled is not null) src.Enabled = patch.Enabled.Value;
    await db.SaveChangesAsync();
    return Results.Ok(src);
});

admin.MapDelete("/sources/{id}", async (string id, RbRulesDbContext db) =>
{
    var src = await db.Sources.FindAsync(id);
    if (src is null) return Results.NotFound();
    // FK's zijn cascade/set-null geconfigureerd (audit-fix) — geen wees-rijen.
    await db.Documents.Where(d => d.SourceId == id).ExecuteDeleteAsync();
    await db.Changes.Where(c => c.SourceId == id).ExecuteDeleteAsync();
    db.Sources.Remove(src);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Feed-curatie: ruis (bijv. oude flip-flop-spam) handmatig kunnen opruimen.
admin.MapDelete("/changes/{id:long}", async (long id, RbRulesDbContext db) =>
{
    var c = await db.Changes.FindAsync(id);
    if (c is null) return Results.NotFound();
    db.Changes.Remove(c);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

admin.MapGet("/corrections", async (RbRulesDbContext db) =>
    await db.Corrections.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync());

admin.MapPost("/corrections/{id:long}/verify", async (
    long id, RbRulesDbContext db, EmbeddingService embeddings) =>
{
    var c = await db.Corrections.FindAsync(id);
    if (c is null) return Results.NotFound();
    c.Status = "verified";
    c.VerifiedAt = DateTimeOffset.UtcNow;
    try
    {
        // Embedding op vraag+correctie zodat /ask de ruling semantisch vindt.
        c.Embedding = await embeddings.EmbedOneAsync($"{c.Question}\n{c.Text}");
    }
    catch
    {
        // Ollama tijdelijk weg — verificatie telt, embedding volgt bij her-verify.
    }
    await db.SaveChangesAsync();
    return Results.Ok(c);
});

admin.MapDelete("/corrections/{id:long}", async (long id, RbRulesDbContext db) =>
{
    var c = await db.Corrections.FindAsync(id);
    if (c is null) return Results.NotFound();
    db.Corrections.Remove(c);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.Run();

static IQueryable<RbRules.Domain.Card> ApplyCardFilters(
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

public record SourcePatch(
    string? Name, string? Url, short? TrustTier, int? Rank, string? Cadence, bool? Enabled);

public record AskRequest(string Question);

public record ResolveRequest(string[] CardIds);

public record CorrectionSubmit(string Question, string Verdict, string? Text);

public record PushSubscribe(string Endpoint, string P256dh, string Auth);

public record PushUnsubscribe(string Endpoint);

public partial class Program;
