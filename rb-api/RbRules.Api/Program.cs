using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Api;
using RbRules.Api.Endpoints;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Infrastructure;
using RbRules.Infrastructure.GraphRag;

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
// Bron-feeds (#167): index-pagina's zijn externe invoer, net als de bronnen
// zelf — zelfde SSRF-gehardde fetch-laag.
builder.Services.AddHttpClient<FeedCrawlService>(c => c.Timeout = TimeSpan.FromSeconds(60))
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
// Hoeveel werk er per embed-verzoek naar Ollama gaat (#282). Hangt vast aan de
// memory:-cap van rb-v2-ollama in de compose-file — verzet nooit het één zonder het
// ander. Startup-snapshot is hier correct: dit is een infra-/geheugenknop die met een
// containerherstart meekomt, geen gedragsvlag die je live wilt kunnen omzetten.
builder.Services.AddSingleton(sp => EmbeddingSettings.FromEnvironment(
    // Een genegeerde waarde mag niet stil op de default terugvallen (#282-review):
    // dan denk je te hebben bijgesteld terwijl er niets veranderde.
    msg => sp.GetRequiredService<ILoggerFactory>()
        .CreateLogger("EmbeddingSettings").LogWarning("{Message}", msg)));
builder.Services.AddScoped<CardEmbeddingPipeline>();
builder.Services.AddScoped<MechanicMiningService>();
builder.Services.AddScoped<GraphSyncService>();
// Brein-projectie (#227, §3.5): de brein-lagen die GraphSyncService niet dekt
// (CanonicalEntity/MechanicPredicate/OntologyVersion) idempotent naar Neo4j.
builder.Services.AddScoped<BreinProjectionService>();
// Redeneer-laag (#227, §5): Neo4j-native inferentie + contradictie-detectie.
builder.Services.AddScoped<ReasoningService>();
builder.Services.AddScoped<ProvenanceAuditService>();
builder.Services.AddScoped<EntityResolutionService>();
builder.Services.AddScoped<InteractionPromotionService>();
// Brein-mining-orkestratie (#226, §3.1/§3.4): tool-forced extractie via rb-ai →
// entity-resolutie → fase-2-promotie-poort → atomair feit+provenance. Handmatige
// jobs (breinmine-interacties/-predicaten), bewust NIET in de "alles"-keten.
builder.Services.AddScoped<BreinInteractionMiningService>();
builder.Services.AddScoped<BreinPredicateMiningService>();
// Steekproef-audit (#255): 1 op de N gepromoveerde interacties langs het sterkere
// model (rb-ai task "hard"). Meting + provenance, nooit een tier-wijziging.
builder.Services.AddScoped<BreinInteractionAuditService>();
builder.Services.AddScoped<RuleChunkPipeline>();
builder.Services.AddScoped<AskService>();
// Beheerde instellingen (#254): de feature-vlaggen die vroeger alleen via de
// VM-.env te zetten waren (brein-retrieval, nachtrun-noodrem + venster) staan nu in
// de setting-tabel en worden op het GEBRUIKSMOMENT gelezen — een toggle in beheer
// werkt dus zonder SSH of herstart. De omgeving blijft de bootstrap-default: zonder
// DB-rij geldt exact de bestaande env-/codewaarde. Singleton met een korte
// in-memory cache (zie ManagedSettingsService), dus het hete /ask-pad betaalt geen
// query per vraag. Vervangt de vroegere BreinRetrievalSettings-/NightlyRunSettings-
// singletons: die lazen één keer bij startup en zijn bewust NIET meer injecteerbaar,
// zodat niemand per ongeluk een bevroren snapshot gebruikt.
builder.Services.AddSingleton(sp => new ManagedSettingsService(
    sp.GetRequiredService<IDbContextFactory<RbRulesDbContext>>(),
    sp.GetRequiredService<ILogger<ManagedSettingsService>>()));
// Brein-GraphRAG-retrieval in /ask (#228, §4) ACHTER de default-uit feature-flag:
// staat de flag uit, dan raakt AskService deze laag nooit aan. De vier fase-4-poorten
// draaien tegen de live Neo4j + pgvector (INTEGRATIE-FOLLOW-UP: niet in CI — de
// adapters degraderen bij uitval naar leeg, zodat /ask nooit een 500 krijgt). De
// orchestrator + service zijn scoped (per request), net als AskService zelf.
// Brein-mining-parallellisme (#279): singleton uit env (default 3 workers — precies
// rb-ai's achtergrond-deelcap, zodat /ask altijd slots overhoudt).
builder.Services.AddSingleton(_ => BreinMiningSettings.FromEnvironment());
builder.Services.AddScoped<IGazetteerSource, PostgresGazetteerSource>();
builder.Services.AddScoped<INodeContextSimilarity, PgVectorNodeSimilarity>();
builder.Services.AddScoped<INodeAdjacency, Neo4jNodeAdjacency>();
builder.Services.AddScoped<IGraphRetriever, BreinGraphRetriever>();
builder.Services.AddScoped<RetrievalOrchestrator>();
builder.Services.AddScoped<BreinRetrievalService>();
// Rewrite-cache (#152): singleton — moet de levensduur van het proces
// overspannen (AskService zelf is scoped, per request), klein en LRU op de
// genormaliseerde vraag. Zonder registratie zou AskService's optionele
// parameter gewoon null blijven en caching uit staan (patroon dbFactory).
builder.Services.AddSingleton<RewriteCache>();
// Eigen ask-geschiedenis (#157): user_id resp. ip_hash van RequestUserContext.
builder.Services.AddScoped<AskHistoryService>();
builder.Services.AddScoped<RuleSearchService>();
builder.Services.AddScoped<PrimerService>();
builder.Services.AddScoped<BanErrataSyncService>();
builder.Services.AddScoped<InteractionService>();
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<ChangeClassificationService>();
// Changeconsolidatie (#206): koppelt changes die hetzelfde event vanuit
// meerdere bronnen melden (feed-presentatie, geen inhoudelijke waarheid).
builder.Services.AddScoped<ChangeFeedService>();
builder.Services.AddScoped<PublicStatsService>();
builder.Services.AddScoped<ChangeConsolidationService>();
builder.Services.AddScoped<SourceScoutService>();
builder.Services.AddScoped<CardResolver>();
builder.Services.AddScoped<CardDetailService>();
builder.Services.AddScoped<DeckBrowserService>();
builder.Services.AddScoped<DeckCodeService>();
builder.Services.AddScoped<SourceDossierService>();
builder.Services.AddScoped<SourceListService>();
builder.Services.AddScoped<CardSimilarityService>();
builder.Services.AddScoped<SimilarityExplainService>();
builder.Services.AddScoped<RuleBrowserService>();
builder.Services.AddScoped<GraphQueryService>();
builder.Services.AddScoped<ClaimMiningService>();
// FAQ-/clarificatie-concept-extractie (#177): losse verduidelijkingen uit
// officiële FAQ-artikelen als geverifieerde rulings.
builder.Services.AddScoped<ClarificationMiningService>();
builder.Services.AddScoped<RelationMiningService>();
// Relatie-triage (#199 v1): LLM-aanbeveling per open voorstel + het bestaande
// accept-/reject-pad (losse acties én de bulk-actie per aanbevelingsgroep).
builder.Services.AddScoped<RelationTriageService>();
// Agentic-terugkoppeling (#120): voorstellen die de ask-agent achterlaat.
builder.Services.AddScoped<AgenticRelationService>();
// Wipe-mechanisme voor de LLM-afgeleide kennislaag (#187): expliciete
// admin-job, nooit automatisch.
builder.Services.AddScoped<KnowledgeRegenerationService>();
// Gerichte brein-mining-reset (#263): alleen de mining-laag terug naar nul zodat
// een verbeterde extractie dezelfde pool opnieuw kan minen. Ook expliciet.
builder.Services.AddScoped<BreinMiningResetService>();
builder.Services.AddScoped<MechanicVocabularyService>();
builder.Services.AddScoped<ReviewNoteService>();
// Her-evaluatie van één Correction op een beheerder-opmerking (#184): draait
// de hybride poort (#177/#185) opnieuw, met een optionele anker-correctie.
builder.Services.AddScoped<CorrectionReevaluationService>();
// In-chat rulings vanuit /ask (#166): autoriteit bepaalt verified vs pending.
builder.Services.AddScoped<ChatRulingService>();
builder.Services.AddScoped<SetReleaseService>();
builder.Services.AddScoped<KnowledgeGapsService>();
// Judge-benchmark (#158): draait de vaste vragenset met de isolatie-vlag aan
// (AskOptions.Benchmark) — voedt geen ask_trace/ask_metric/relaties.
builder.Services.AddScoped<BenchmarkService>();
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
// Brein-verkenner & inspectie (#236): read-only projecties over de brein-
// tabellen voor de admin-console. Puur leesbaar, geen live-Neo4j.
builder.Services.AddScoped<BrainExplorerService>();
// Publieke rulings-databank (#127).
builder.Services.AddScoped<RulingsService>();
// Accounts + per-gebruiker-quota (#42); passkey-login (#109).
builder.Services.AddScoped<UserAccountService>();
builder.Services.AddScoped<PasskeyService>();
builder.Services.AddScoped<RequestUserContext>();
// Grondig-quotum TOCTOU-reservering (#153): proces-breed, dus singleton —
// zie AgenticInFlightTracker (single-instance-aanname).
builder.Services.AddSingleton<AgenticInFlightTracker>();
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
    // Voorverwarmsignaal (#154): strikt per IP en bewust los van "llm" —
    // een paginalaad mag geen vraagbudget opeten, maar boot wel (indirect)
    // een subprocess op de VM en is dus niet ongelimiteerd. rb-ai's pool is
    // daarnaast zelf idempotent en op één warme sessie gecapt.
    o.AddPolicy("prewarm", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "prewarm:" + (ctx.Request.Headers["X-Client-Ip"].FirstOrDefault()
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"),
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

    // Benchmark-seed (#158): idempotent op ExternalKey, zelfde patroon als
    // hierboven — nieuwe delen van de judge-test komen vanzelf mee bij de
    // volgende deploy (BenchmarkSeed.Defaults uitbreiden volstaat); bestaande
    // vragen (en een eventuele CorrectIndex-update) blijven ongemoeid.
    var existingBq = await db.BenchmarkQuestions.Select(q => q.ExternalKey).ToHashSetAsync();
    foreach (var q in BenchmarkSeed.Defaults.Where(q => !existingBq.Contains(q.ExternalKey)))
        db.BenchmarkQuestions.Add(q);
    await db.SaveChangesAsync();

    // Bron-feeds (#167): zelfde seed-alleen-ontbrekende-semantiek.
    var existingFeeds = await db.SourceFeeds.Select(f => f.Id).ToHashSetAsync();
    foreach (var feed in SourceFeedSeed.Defaults.Where(f => !existingFeeds.Contains(f.Id)))
        db.SourceFeeds.Add(feed);
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
app.MapDeckEndpoints();
app.MapRuleEndpoints();
app.MapRulingsEndpoints();
app.MapKnowledgeEndpoints();
app.MapBrainEndpoints();
app.MapAskEndpoints();
app.MapAuthEndpoints();
app.MapFeedEndpoints();
app.MapPushEndpoints();
app.MapAdminEndpoints();
app.MapBrainAdminEndpoints();
app.MapSettingsAdminEndpoints();

app.Run();

public partial class Program;
