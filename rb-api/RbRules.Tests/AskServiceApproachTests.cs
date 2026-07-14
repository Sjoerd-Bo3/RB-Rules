using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Servicetests voor de aanpak-keuze (#153): de pure beslislogica is
/// in AgenticGateTests gedekt, hier gaat het om de bedrading in AskService —
/// alleen-ingelogd-honorering (via RequestUserContext), de quota-terugval,
/// de attributie op metric/trace (gate vs gebruiker, op beide paden) en de
/// quota-teller in UsageTodayAsync. Zelfde testopzet als
/// AskServiceAgenticTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen) en dezelfde xUnit-collectie: deze tests zetten de
/// proces-brede ASK_AGENTIC-env.</summary>
[Collection("ask-service-env")]
public class AskServiceApproachTests
{
    private const string SourceId = "riot-core-rules";
    private const long UserId = 7;
    private const string InteractionQuestion =
        "When Viktor attacks while Yasuo has Deflect during a showdown, what happens?";
    private const string PlainQuestion = "How does Deflect work during a showdown?";
    private const string AgentAnswer = "**Oordeel:** Antwoord van de agent. [1]";
    private const string SinglePassAnswer = "**Oordeel:** Antwoord via single-pass. [1]";

    // ── Thorough gehonoreerd: agent antwoordt, attributie = gebruiker ──

    [Fact]
    public async Task Thorough_IngelogdMetQuota_AgentAntwoordtAlsGebruikersEscalatie()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        // Bewust een vraag die de auto-gate NIET escaleert (geen twee
        // kaartnamen, retrieval heeft bewijs): de gebruiker forceert.
        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls);
        Assert.Equal("thorough", result.Approach);
        Assert.Null(result.ApproachReason);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.True(metric.Agentic);
        Assert.Equal("user", metric.EscalatedBy);
        Assert.Equal(UserId, metric.UserId);
        var trace = await db.AskTraces.SingleAsync();
        Assert.True(trace.Agentic);
        Assert.Equal("user", trace.EscalatedBy);

        // De teller van het Grondig-dagquotum ziet precies deze ene rij.
        var usage = await Accounts(db).UsageTodayAsync(UserId);
        Assert.Equal(1, usage.AgenticForced);
    }

    // ── Anoniem: het veld wordt genegeerd, alles blijft Auto ───────────

    [Fact]
    public async Task Thorough_Anoniem_VeldGenegeerdBlijftSinglePass()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, new RequestUserContext());

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("auto", result.Approach);
        Assert.Null(result.ApproachReason);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.False(metric.Agentic);
        Assert.Null(metric.EscalatedBy);
    }

    // ── Quota op: terugvallen op Auto mét reden voor de UI-melding ─────

    [Fact]
    public async Task Thorough_QuotaOp_ValtTerugOpAutoMetRedenQuota()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        // Tegoed van 2, al 2 geforceerd vandaag (zoals de quota-filter het
        // dit request telde): op.
        var svc = Svc(db, ai, LoggedIn(dailyAgenticQuota: 2, agenticForcedToday: 2));

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("auto", result.Approach);
        Assert.Equal(AgenticGate.ReasonQuota, result.ApproachReason);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Null(metric.EscalatedBy);
    }

    // ── Flag off: Grondig bestaat niet, ook niet via de API ────────────

    [Fact]
    public async Task Thorough_FlagOff_GrondigBestaatNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        var result = await WithModeAsync("off", () => svc.AskAsync(
            InteractionQuestion, approach: AskApproach.Thorough));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("auto", result.Approach);
        Assert.Equal(AgenticGate.ReasonDisabled, result.ApproachReason);
    }

    // ── Foto onder Grondig: het vision-pad wint (bestaande gate-regel) ─

    [Fact]
    public async Task Thorough_MetFoto_BlijftOpHetVisionpadMetRedenPhoto()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, images: [new RbAiClient.AiImage("image/jpeg", "dGVzdA==")],
            approach: AskApproach.Thorough));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("auto", result.Approach);
        Assert.Equal(AgenticGate.ReasonPhoto, result.ApproachReason);
        Assert.Equal("hard", (await db.AskMetrics.SingleAsync()).Model);
    }

    // ── Fast: ook een gate-waardige vraag blijft single-pass ───────────

    [Fact]
    public async Task Fast_Interactievraag_BlijftSinglePass()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        // Deze vraag zou onder auto via de gate escaleren; Snel wint.
        var result = await WithModeAsync("auto", () => svc.AskAsync(
            InteractionQuestion, approach: AskApproach.Fast));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("fast", result.Approach);
        Assert.Null(result.ApproachReason);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Null(metric.EscalatedBy);
    }

    // ── Gate-escalatie: attributie "gate", telt niet tegen het quotum ──

    [Fact]
    public async Task Auto_GateEscalatie_BoektGateEnRaaktHetQuotumNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal("auto", result.Approach);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal("gate", metric.EscalatedBy);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal("gate", trace.EscalatedBy);

        // Gate-escalaties verbruiken het Grondig-dagquotum niet.
        var usage = await Accounts(db).UsageTodayAsync(UserId);
        Assert.Equal(0, usage.AgenticForced);
        Assert.Equal(1, usage.Questions);
    }

    // ── Vangnet: de geforceerde póging telt (kosten zijn gemaakt) ──────

    [Fact]
    public async Task Thorough_AgentFaalt_PogingTeltTochTegenHetQuotum()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi(agenticFails: true);
        var svc = Svc(db, ai, LoggedIn());

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        // Vangnet levert het antwoord; de attributie blijft op de poging
        // staan — conservatief, net als mislukte vragen in het dagquotum.
        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.False(metric.Agentic);
        Assert.Equal("user", metric.EscalatedBy);
        Assert.Equal(1, (await Accounts(db).UsageTodayAsync(UserId)).AgenticForced);
    }

    // ── TOCTOU (#153): een nog-lopende geforceerde run bezet het laatste
    //    permit → een gelijktijdig Grondig-request valt terug op quota ─────

    [Fact]
    public async Task Thorough_LaatstePermitAlInFlight_ValtTerugOpQuota()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        // Dagtegoed 3, waarvan er 2 als voltooid in de db-teller zitten; er is
        // dus nog één permit. Een concurrent Grondig-request "reserveert" dat
        // laatste permit al (simuleert een nog-lopende run die zijn metric nog
        // niet schreef).
        var tracker = new AgenticInFlightTracker();
        using var concurrent = tracker.TryReserve(UserId, dbCountToday: 2, dailyQuota: 3);
        Assert.NotNull(concurrent);
        var svc = Svc(db, ai, LoggedIn(dailyAgenticQuota: 3, agenticForcedToday: 2), tracker);

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        // De db-teller alleen (2 < 3) zou nog "ruimte" zien — maar mét de
        // lopende reservering is het quotum vol: geen escalatie, nette terugval.
        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.Equal("auto", result.Approach);
        Assert.Equal(AgenticGate.ReasonQuota, result.ApproachReason);
        Assert.Null((await db.AskMetrics.SingleAsync()).EscalatedBy);
        // De reservering van de concurrent-run blijft ongemoeid staan.
        Assert.Equal(1, tracker.InFlight(UserId));
    }

    [Fact]
    public async Task Thorough_GeslaagdeRun_GeeftPermitWeerVrij()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var tracker = new AgenticInFlightTracker();
        var svc = Svc(db, ai, LoggedIn(), tracker);

        await WithModeAsync("auto", () => svc.AskAsync(
            PlainQuestion, approach: AskApproach.Thorough));

        // Na afronding (metric geschreven) is het permit vrij — geen leak.
        Assert.Equal(1, ai.AgenticCalls);
        Assert.Equal(0, tracker.InFlight(UserId));
    }

    // ── Streamingpad: zelfde honorering, meta draagt de terugmelding ───

    [Fact]
    public async Task Streaming_Thorough_MetaDraagtAanpakEnMetricBoektGebruiker()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn());

        AskStreamMeta? meta = null;
        var deltas = new StringBuilder();
        var result = await WithModeAsync("auto", () => svc.AskStreamingAsync(
            PlainQuestion, images: null, history: null,
            onMeta: m => { meta = m; return Task.CompletedTask; },
            onDelta: d => { deltas.Append(d); return Task.CompletedTask; },
            approach: AskApproach.Thorough));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal("thorough", result.Approach);
        Assert.NotNull(meta);
        Assert.Equal("thorough", meta!.Approach);
        Assert.Null(meta.ApproachReason);
        Assert.Equal(AgentAnswer, deltas.ToString());
        Assert.Equal("user", (await db.AskMetrics.SingleAsync()).EscalatedBy);
        Assert.Equal("user", (await db.AskTraces.SingleAsync()).EscalatedBy);
    }

    [Fact]
    public async Task Streaming_QuotaOp_MetaMeldtDeTerugvalVoorHetAntwoord()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai, LoggedIn(dailyAgenticQuota: 1, agenticForcedToday: 1));

        AskStreamMeta? meta = null;
        var result = await WithModeAsync("auto", () => svc.AskStreamingAsync(
            PlainQuestion, images: null, history: null,
            onMeta: m => { meta = m; return Task.CompletedTask; },
            onDelta: _ => Task.CompletedTask,
            approach: AskApproach.Thorough));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        Assert.NotNull(meta);
        Assert.Equal("auto", meta!.Approach);
        Assert.Equal(AgenticGate.ReasonQuota, meta.ApproachReason);
        Assert.Equal(AgenticGate.ReasonQuota, result.ApproachReason);
    }

    // --- testinfra (patroon AskServiceAgenticTests) ------------------------

    /// <summary>Alleen het FTS-kanaal vervangen door een woord-match — zelfde
    /// test-seam als AskServiceDegradationTests (tsvector vertaalt niet naar
    /// EF InMemory). De RequestUserContext is hier injecteerbaar: precies de
    /// poort waarlangs de aanpak-keuze alleen-ingelogd gehonoreerd wordt.</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RequestUserContext userContext, AgenticInFlightTracker? inFlight)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            // #152 schoof dbFactory/rewriteCache vóór agenticInFlight — named
            // arg zodat de tracker niet positioneel op dbFactory belandt.
            userContext, NullLogger<AskService>.Instance, agenticInFlight: inFlight)
    {
        private readonly RbRulesDbContext _db = db;

        protected override async Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            var words = searchText.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .ToList();
            var rows = await _db.RuleChunks.AsNoTracking()
                .Select(c => new { c.Id, c.SourceId, c.Text })
                .ToListAsync(ct);
            return [.. rows
                .Where(r => words.Any(w => r.Text.ToLowerInvariant().Contains(w)))
                .Select(r => (r.Id, r.SourceId))];
        }
    }

    /// <summary>Ingelogde context zoals de quota-filter die zet (#153): de
    /// gebruiker plus het al getelde verbruik van vandaag.</summary>
    private static RequestUserContext LoggedIn(
        int dailyAgenticQuota = 5, int agenticForcedToday = 0) => new()
    {
        User = new AppUser
        {
            Id = UserId, Email = "test@example.com",
            DailyAgenticQuota = dailyAgenticQuota,
        },
        Usage = new UsageToday(agenticForcedToday, 0, agenticForcedToday),
    };

    private static UserAccountService Accounts(RbRulesDbContext db) =>
        new(db, new MailService(), NullLogger<UserAccountService>.Instance);

    /// <summary>Echte RbAiClient op een handler die de agentic-taak apart
    /// behandelt (patroon AskServiceAgenticTests): telt agentic-calls, kan
    /// laten slagen of falen; rewrite + single-pass antwoorden gewoon.</summary>
    private sealed class AgenticAwareAi(bool agenticFails = false)
    {
        public int AgenticCalls { get; private set; }

        public RbAiClient Client => new(
            new HttpClient(new Handler(this, agenticFails))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        private sealed class Handler(AgenticAwareAi owner, bool fails) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(ct);
                if (body.Contains("\"task\":\"agentic\""))
                {
                    owner.AgenticCalls++;
                    return fails
                        ? Json(HttpStatusCode.InternalServerError, new
                        {
                            error = "agentic-call afgebroken na 120s (harde timeout)",
                            steps = new[] { "semantic_search {\"q\":\"Deflect showdown\"}" },
                        })
                        : Json(HttpStatusCode.OK, new
                        {
                            answer = AgentAnswer,
                            steps = new[] { "semantic_search {\"q\":\"Deflect showdown\"}" },
                        });
                }
                if (request.RequestUri!.AbsolutePath == "/ask/stream")
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { type = "delta", text = SinglePassAnswer }) + "\n" +
                            JsonSerializer.Serialize(new { type = "done", answer = SinglePassAnswer }) + "\n",
                            Encoding.UTF8, "application/x-ndjson"),
                    };
                // Rewrite- én single-pass-antwoord; bevat geen accolades, dus
                // de rewrite-parse levert null op (rauwe-vraag-pad).
                return Json(HttpStatusCode.OK, new { answer = SinglePassAnswer });
            }

            private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
                new(status)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
        }
    }

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => throw exception();
    }

    private static TestableAskService Svc(
        RbRulesDbContext db, AgenticAwareAi ai, RequestUserContext userContext,
        AgenticInFlightTracker? inFlight = null) =>
        new(db, FailingEmbeddings(), ai.Client, userContext, inFlight);

    /// <summary>Failing Ollama (patroon #100): qv blijft null — deze tests
    /// draaien om de aanpak-keuze, niet om de vector-kanalen.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new ThrowingHandler(() => new HttpRequestException("ollama plat")))
        { BaseAddress = new Uri("http://ollama.test") });

    /// <summary>Zet ASK_AGENTIC voor de duur van één test; altijd terug naar
    /// ongezet zodat de proces-brede default (off) nooit blijft hangen.</summary>
    private static async Task<T> WithModeAsync<T>(string mode, Func<Task<T>> body)
    {
        Environment.SetEnvironmentVariable("ASK_AGENTIC", mode);
        try
        {
            return await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASK_AGENTIC", null);
        }
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }

    private static async Task SeedAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "466.2",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
        });
        // Twee canonieke kaarten voor de kaartnamen-trigger van de gate.
        db.Cards.AddRange(
            new Card { RiftboundId = "test-viktor", Name = "Viktor" },
            new Card { RiftboundId = "test-yasuo", Name = "Yasuo", Mechanics = ["Deflect"] });
        await db.SaveChangesAsync();
    }
}
