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

// AddDbContextFactory registreert beide koppelvlakken (#152): de singleton
// IDbContextFactory<RbRulesDbContext> waarmee AskService zijn retrieval-
// kanalen elk op een eigen context concurrent draait (DbContext is niet
// thread-safe), én RbRulesDbContext zelf als scoped service — migraties bij
// opstart en alle bestaande services blijven dus ongewijzigd op de scoped
// context werken.
builder.Services.AddDbContextFactory<RbRulesDbContext>(o => o
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
    // Ruim boven het harde 5-minutenbudget van research-calls in rb-ai (#64),
    // anders hakt rb-api een nog lopende webzoektocht (#63-scout) af terwijl
    // de sidecar doorwerkt. Overige taken antwoorden of falen veel eerder.
    c.Timeout = TimeSpan.FromMinutes(6);
});
// SSRF-guard (#45), fetch-laag: IngestService praat als enige client met
// URL's van buiten (register/scout/hub) — DNS-check op elk verbindingsdoel
// (ook redirect-hops) + redirect-limiet. De URL-regels zelf checkt
// IngestService vóór de call (UrlGuard.Check).
builder.Services.AddHttpClient<IngestService>(c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(SafeExternalHttp.CreateHandler);
// Deck-ingest (#15): praat alleen met piltoverarchive.com (sitemap + publieke
// deck-pagina's, nooit hun /api/) — zelfde SSRF-gehardde fetch-laag.
builder.Services.AddHttpClient<DeckIngestService>(c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(SafeExternalHttp.CreateHandler);
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
// Rewrite-cache (#152): singleton — moet de levensduur van het proces
// overspannen (AskService zelf is scoped, per request), klein en LRU op de
// genormaliseerde vraag. Zonder registratie zou AskService's optionele
// parameter gewoon null blijven en caching uit staan (patroon dbFactory).
builder.Services.AddSingleton<RewriteCache>();
builder.Services.AddScoped<RuleSearchService>();
builder.Services.AddScoped<PrimerService>();
builder.Services.AddScoped<BanErrataSyncService>();
builder.Services.AddScoped<InteractionService>();
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<ChangeClassificationService>();
builder.Services.AddScoped<SourceScoutService>();
builder.Services.AddScoped<CardResolver>();
builder.Services.AddScoped<CardDetailService>();
builder.Services.AddScoped<CardSimilarityService>();
builder.Services.AddScoped<SimilarityExplainService>();
builder.Services.AddScoped<RuleBrowserService>();
builder.Services.AddScoped<GraphQueryService>();
builder.Services.AddScoped<ClaimMiningService>();
builder.Services.AddScoped<RelationMiningService>();
// Agentic-terugkoppeling (#120): voorstellen die de ask-agent achterlaat.
builder.Services.AddScoped<AgenticRelationService>();
builder.Services.AddScoped<MechanicVocabularyService>();
builder.Services.AddScoped<ReviewNoteService>();
builder.Services.AddScoped<SetReleaseService>();
builder.Services.AddScoped<KnowledgeGapsService>();
// Run_log-grootboek voor periodieke jobs (#122): vensters voor de scheduler
// en "laatste run per job" voor beheer.
builder.Services.AddScoped<JobLedger>();
// Kennis-levenscyclus (#119): regelwijzigingen hertoetsen betrokken
// primer-docs en claims — lift mee in de scan-afronding van IngestService.
builder.Services.AddScoped<KnowledgeRecheckService>();
// Brein-API (#105): Postgres-kant (search/node/evidence/contradictions) en
// Neo4j-kant (neighbors/path) gescheiden — degradatie per koppelvlak.
builder.Services.AddScoped<BrainService>();
builder.Services.AddScoped<BrainGraphService>();
// Publieke rulings-databank (#127).
builder.Services.AddScoped<RulingsService>();
// Accounts + per-gebruiker-quota (#42); passkey-login (#109).
builder.Services.AddScoped<UserAccountService>();
builder.Services.AddScoped<PasskeyService>();
builder.Services.AddScoped<RequestUserContext>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService<ScanScheduler>();

builder.Services.AddOpenApi();

// Rate-limiting op de dure/publieke schrijfroutes (#42): elke /api/ask en
// explain-call is een betaalde LLM-call. Anoniem: partitie op het echte
// client-IP dat rb-web meegeeft (X-Client-Ip) — requests komen anders
// allemaal van het rb-web-container-IP. Ingelogd (X-User-Token): ruimere
// limiet per sessietoken; de echte per-account-dagquota handhaaft
// UserQuotaFilter (ongeldige tokens strandden daar op een 401, dus een
// verzonnen token koopt geen LLM-calls).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("llm", ctx =>
    {
        var userToken = ctx.Request.Headers["X-User-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(userToken))
            return RateLimitPartition.GetFixedWindowLimiter($"user:{userToken}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                });
        return RateLimitPartition.GetFixedWindowLimiter(
            "ip:" + (ctx.Request.Headers["X-Client-Ip"].FirstOrDefault()
                ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            });
    });
    // Login-routes (#42): strikt per IP — mailbombing en token-raden remmen.
    // Bewust nooit op X-User-Token partitioneren: die is hier onbevestigd.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "auth:" + (ctx.Request.Headers["X-Client-Ip"].FirstOrDefault()
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
        }));
    // Passkey-ceremonies (#109): ruimer dan "auth", want elke ceremonie kost
    // twee requests (options + verify) en er is geen mail om te bombarderen —
    // dit remt alleen challenge-rijen flooden (die bovendien elke options-
    // aanroep opgeruimd worden). 8/15min bleek in de praktijk al te krap voor
    // registreren + een paar keer in-/uitloggen achter één (gedeeld) IP.
    o.AddPolicy("webauthn", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "webauthn:" + (ctx.Request.Headers["X-Client-Ip"].FirstOrDefault()
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(15),
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

    // Migratie-retry (#45): na een VM-reboot start rb-api soms eerder dan
    // Postgres klaar is (de compose-healthcheck gate dekt een verse `up`,
    // niet elke reboot-race). Kort en begrensd — blijft het misgaan, dan
    // faalt de start alsnog hard en vangt de deploy-healthcheck-verify het.
    const int migrateAttempts = 5;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < migrateAttempts)
        {
            var delay = TimeSpan.FromSeconds(2 * attempt); // 2+4+6+8s ≪ start_period 90s
            app.Logger.LogWarning(
                ex, "Migratie-poging {Attempt}/{Max} mislukt (Postgres nog niet klaar?); opnieuw over {Delay}s",
                attempt, migrateAttempts, delay.TotalSeconds);
            await Task.Delay(delay);
        }
    }

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
app.MapRulingsEndpoints();
app.MapKnowledgeEndpoints();
app.MapBrainEndpoints();
app.MapAskEndpoints();
app.MapAuthEndpoints();
app.MapFeedEndpoints();
app.MapPushEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program;
