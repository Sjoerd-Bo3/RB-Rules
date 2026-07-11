using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Api;
using RbRules.Api.Endpoints;
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
builder.Services.AddScoped<RuleSearchService>();
builder.Services.AddScoped<PrimerService>();
builder.Services.AddScoped<BanErrataSyncService>();
builder.Services.AddScoped<InteractionService>();
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<ChangeClassificationService>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService<ScanScheduler>();

builder.Services.AddOpenApi();

// Rate-limiting op de dure/publieke schrijfroutes (#42-quick-win): elke
// /api/ask en explain-call is een betaalde LLM-call. Partitie op het echte
// client-IP dat rb-web meegeeft (X-Client-Ip) — requests komen anders
// allemaal van het rb-web-container-IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("llm", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Client-Ip"].FirstOrDefault()
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));
});

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

app.UseRateLimiter();

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "rb-api" }));

app.MapCardEndpoints();
app.MapRuleEndpoints();
app.MapAskEndpoints();
app.MapFeedEndpoints();
app.MapPushEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program;
