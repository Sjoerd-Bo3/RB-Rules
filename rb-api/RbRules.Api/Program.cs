using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
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

builder.Services.AddOpenApi();

var app = builder.Build();

// Migraties + graph-constraints bij start (best-effort voor de graph: de API
// blijft bruikbaar als Neo4j even weg is; DB-migratie is wél hard vereist).
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RbRulesDbContext>().Database.MigrateAsync();
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

app.MapGet("/api/sources", async (RbRulesDbContext db) =>
    await db.Sources
        .OrderBy(s => s.TrustTier).ThenByDescending(s => s.Rank)
        .ToListAsync());

app.MapGet("/api/changes", async (RbRulesDbContext db) =>
    await db.Changes
        .OrderByDescending(c => c.DetectedAt)
        .Take(50)
        .ToListAsync());

app.Run();

public partial class Program;
