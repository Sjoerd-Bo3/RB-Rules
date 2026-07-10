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

app.MapGet("/api/changes", async (RbRulesDbContext db) =>
    await db.Changes
        .OrderByDescending(c => c.DetectedAt)
        .Take(50)
        .ToListAsync());

app.MapGet("/api/cards", async (string? q, RbRulesDbContext db) =>
{
    var query = db.Cards.AsQueryable();
    if (!string.IsNullOrWhiteSpace(q))
        query = query.Where(c => EF.Functions.ILike(c.Name, $"%{q}%"));
    return await query.OrderBy(c => c.Name).Take(60)
        .Select(c => new
        {
            c.RiftboundId, c.Name, c.Type, c.Rarity, c.Domains,
            c.Energy, c.Might, c.SetId, c.ImageUrl,
        })
        .ToListAsync();
});

// ── Semantisch kaartzoeken (S1) ────────────────────────────────
app.MapGet("/api/cards/search", async (
    string q, string? domain, string? type, int? maxEnergy, int? limit,
    RbRulesDbContext db, EmbeddingService embeddings) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is verplicht" });

    var queryVector = await embeddings.EmbedOneAsync(q);
    var cards = db.Cards.Where(c => c.Embedding != null);
    if (!string.IsNullOrWhiteSpace(domain)) cards = cards.Where(c => c.Domains.Contains(domain));
    if (!string.IsNullOrWhiteSpace(type)) cards = cards.Where(c => c.Type == type);
    if (maxEnergy is not null) cards = cards.Where(c => c.Energy != null && c.Energy <= maxEnergy);

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
    if (card.Embedding is null)
        return Results.BadRequest(new { error = "kaart heeft nog geen embedding" });

    var anchor = card.Embedding;
    var results = await db.Cards
        .Where(c => c.Embedding != null && c.RiftboundId != id)
        .OrderBy(c => c.Embedding!.CosineDistance(anchor))
        .Take(Math.Clamp(limit ?? 10, 1, 30))
        .Select(c => new
        {
            c.RiftboundId, c.Name, c.Type, c.Domains, c.Energy, c.Might, c.ImageUrl,
            Distance = c.Embedding!.CosineDistance(anchor),
        })
        .ToListAsync();
    return Results.Ok(results);
});

// ── Beheer (X-Admin-Key) ───────────────────────────────────────
var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

admin.MapGet("/ping", () => Results.Ok(new { ok = true }));

admin.MapPost("/scan", async (string? sourceId, IngestService ingest) =>
    Results.Ok(await ingest.ScanAsync(onlyDue: false, sourceId)));

admin.MapPost("/cards/sync", async (CardSyncService cards, RbRulesDbContext db) =>
{
    var r = await cards.SyncAsync();
    db.RunLogs.Add(new RunLog
    {
        Kind = "cards", Ref = r.Source, Status = "ok",
        Detail = $"{r.Sets} sets, {r.Cards} kaarten",
    });
    await db.SaveChangesAsync();
    return Results.Ok(r);
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

admin.MapGet("/corrections", async (RbRulesDbContext db) =>
    await db.Corrections.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync());

admin.MapPost("/corrections/{id:long}/verify", async (long id, RbRulesDbContext db) =>
{
    var c = await db.Corrections.FindAsync(id);
    if (c is null) return Results.NotFound();
    c.Status = "verified";
    c.VerifiedAt = DateTimeOffset.UtcNow;
    // Embedding volgt in de S1-embed-pijplijn (bge-m3) — status is leidend.
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

public record SourcePatch(
    string? Name, string? Url, short? TrustTier, int? Rank, string? Cadence, bool? Enabled);

public partial class Program;
