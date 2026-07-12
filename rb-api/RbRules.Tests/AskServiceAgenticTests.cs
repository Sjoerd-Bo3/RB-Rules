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

/// <summary>Servicetests voor de agentic-gate-bedrading in AskService (#107,
/// review-opvolging): de pure gate is elders getest, hier gaat het om wat
/// AskService de gate voedt en hoe escalatie/vangnet/registratie zich in de
/// echte pipeline gedragen. Zelfde testopzet als AskServiceDegradationTests
/// (EF InMemory, echte RbAiClient op een gestubde handler, FTS vervangen).
/// In dezelfde xUnit-collectie als de degradatietests omdat deze tests de
/// proces-brede ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServiceAgenticTests
{
    private const string SourceId = "riot-core-rules";
    private const string InteractionQuestion =
        "When Viktor attacks while Yasuo has Deflect during a showdown, what happens?";
    private const string AgentAnswer = "**Oordeel:** Antwoord van de agent. [1]";
    private const string SinglePassAnswer = "**Oordeel:** Antwoord via single-pass. [1]";

    // ── Trigger (a) telt alleen de huidige vraag (review #107) ─────────

    [Fact]
    public async Task Auto_FollowUpZonderKaartnamen_EscaleertNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai);

        // De historie noemt twee kaarten; de vervolgvraag zelf geen enkele.
        var result = await WithModeAsync("auto", () => svc.AskAsync(
            "And does that also apply during a showdown?",
            history: [new AskTurn(InteractionQuestion, "**Oordeel:** Ja. [1]")]));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        var trace = await db.AskTraces.SingleAsync();
        Assert.False(trace.Agentic);
        Assert.Null(trace.BrainSteps);
        Assert.Equal("cheap", trace.Model);
    }

    [Fact]
    public async Task Auto_InteractievraagMetTweeKaartnamen_AgentAntwoordt()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls);
        var trace = await db.AskTraces.SingleAsync();
        Assert.True(trace.Agentic);
        Assert.Equal("agentic", trace.Model);
        Assert.Contains("semantic_search", trace.BrainSteps);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.True(metric.Agentic);
        Assert.Equal("agentic", metric.Model);
        // #121: de agent-run droeg usage; de andere calls (rewrite) niet —
        // het gemeten deel staat op de metric, onder het agentic-pad.
        Assert.Equal(52_000, metric.InputTokens);
        Assert.Equal(700, metric.OutputTokens);
    }

    // ── Foto-vragen escaleren nooit (review #107) ──────────────────────

    [Fact]
    public async Task Force_FotoVraag_BlijftOpHetVisionpad()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai);

        var result = await WithModeAsync("force", () => svc.AskAsync(
            InteractionQuestion,
            images: [new RbAiClient.AiImage("image/jpeg", "dGVzdA==")]));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(0, ai.AgenticCalls);
        var trace = await db.AskTraces.SingleAsync();
        Assert.False(trace.Agentic);
        Assert.Equal("hard", trace.Model);
    }

    // ── Vangnet: agent faalt → single-pass, partial steps bewaard ─────

    [Fact]
    public async Task Auto_AgentFaalt_VangnetLevertSinglePassMetPartialSteps()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi(agenticFails: true);
        var svc = Svc(db, ai);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        Assert.Equal(SinglePassAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls);
        var trace = await db.AskTraces.SingleAsync();
        // Agentic = daadwerkelijk door de agent beantwoord — vangnet telt niet…
        Assert.False(trace.Agentic);
        Assert.Equal("cheap", trace.Model);
        // …maar de vóór de uitval gedane tool-calls én de marker staan in de trace.
        Assert.Contains("semantic_search", trace.BrainSteps);
        Assert.Contains("[vangnet:", trace.BrainSteps);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.False(metric.Agentic);
        // #121: de fout-body van rb-ai draagt geen usage en de vangnet-calls
        // hier evenmin — dan blijft het totaal eerlijk "onbekend" (null).
        Assert.Null(metric.InputTokens);
        Assert.Null(metric.OutputTokens);
    }

    // ── Agentic-terugkoppeling (#120): ontdekte verbanden als voorstel ─

    /// <summary>Voorstellenblok zoals rb-ai het als `relations` meestuurt:
    /// twee echte verbanden (waarvan één met een ref in de verkeerde case,
    /// mechanic:deflect) plus één gehallucineerde ref die geweerd moet
    /// worden.</summary>
    private const string ProposalsBlock = """
        {"relations": [
          {"from": "card:test-viktor", "to": "card:test-yasuo", "kind": "counters", "explanation": "Viktor omzeilt Yasuo's Deflect met een niet-targeted effect."},
          {"from": "mechanic:deflect", "to": "card:test-yasuo", "kind": "synergie met", "explanation": "Deflect beschermt Yasuo tijdens een showdown."},
          {"from": "card:test-viktor", "to": "card:verzonnen-999", "kind": "counters", "explanation": "Deze ref bestaat niet in het brein."}
        ]}
        """;

    [Fact]
    public async Task Agentic_VoorstellenGevalideerdEnOpgeslagen_GehallucineerdeRefGeweerd()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi(relations: ProposalsBlock);
        var svc = Svc(db, ai);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        // Het antwoord zelf blijft schoon — het blok is metadata, geen tekst.
        Assert.Equal(AgentAnswer, result.Answer);

        var relations = await db.Relations.ToListAsync();
        Assert.Equal(2, relations.Count);
        Assert.All(relations, r =>
        {
            Assert.Equal("unreviewed", r.Status);
            Assert.StartsWith("agentic-ask: When Viktor attacks", r.Provenance);
            Assert.Equal(ClaimScoring.TierWeight(2), r.Trust);
        });
        Assert.Contains(relations, r =>
            r.FromRef == "card:test-viktor" && r.ToRef == "card:test-yasuo" && r.Kind == "counters");
        // Refs canonicaliseren op wat het brein kent: mechanic:deflect → mechanic:Deflect.
        Assert.Contains(relations, r => r.FromRef == "mechanic:Deflect" && r.Kind == "synergie met");
        // De verzonnen ref komt de database nooit in (hallucinatie-weer).
        Assert.DoesNotContain(relations, r =>
            r.FromRef.Contains("verzonnen") || r.ToRef.Contains("verzonnen"));

        // Onbekend kind → kandidaat in de reviewqueue (#116-poort).
        var kind = await db.RelationKinds.SingleAsync();
        Assert.Equal("synergie met", kind.Kind);
        Assert.Equal("candidate", kind.Status);

        // Teller zichtbaar in de trace, naast de bestaande brein-stappen.
        var trace = await db.AskTraces.SingleAsync();
        Assert.True(trace.Agentic);
        Assert.Contains("semantic_search", trace.BrainSteps);
        Assert.Contains(
            "[relatievoorstellen: 2 opgeslagen, 1 geweerd (onbekende ref), 0 al bekend, 1 nieuwe kind-kandidaten]",
            trace.BrainSteps);

        // Herleidbaar in run_log, in de bestaande relaties-pijplijn.
        var log = await db.RunLogs.SingleAsync(l => l.Ref == "agentic-ask");
        Assert.Equal("relations", log.Kind);
        Assert.Equal("ok", log.Status);
    }

    [Fact]
    public async Task Agentic_BekendVoorstelEnVerworpenKind_NietOpnieuwOpgevoerd()
    {
        using var db = NewDb();
        await SeedAsync(db);
        // Eerder verworpen voorstel én verworpen kind zijn definitief: het
        // zelfde voorstel komt niet opnieuw de reviewqueue in (#116-poort).
        db.Relations.Add(new Relation
        {
            FromRef = "card:test-viktor", ToRef = "card:test-yasuo", Kind = "counters",
            Explanation = "eerder voorstel", Provenance = "concept:combat",
            Trust = 0.6, Status = "rejected",
        });
        db.RelationKinds.Add(new RelationKind
        {
            Kind = "synergie met", Status = "rejected", Occurrences = 1,
        });
        await db.SaveChangesAsync();
        var ai = new AgenticAwareAi(relations: ProposalsBlock);
        var svc = Svc(db, ai);

        await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        // Alleen de vooraf bestaande rij; beide voorstellen tellen als bekend.
        Assert.Equal(1, await db.Relations.CountAsync());
        Assert.Equal(1, (await db.RelationKinds.SingleAsync()).Occurrences);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains(
            "[relatievoorstellen: 0 opgeslagen, 1 geweerd (onbekende ref), 2 al bekend]",
            trace.BrainSteps);
    }

    [Fact]
    public async Task Agentic_ZonderVoorstellenBlok_GeenOogstEnGeenTraceregel()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        // Byte-gelijk aan het gedrag vóór #120: geen rijen, geen extra regel.
        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal(0, await db.Relations.CountAsync());
        var trace = await db.AskTraces.SingleAsync();
        Assert.DoesNotContain("[relatievoorstellen", trace.BrainSteps);
    }

    [Fact]
    public async Task Agentic_OnparseerbaarBlok_RunLogDiagnoseEnTracemelding()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new AgenticAwareAi(relations: "dit is geen JSON");
        var svc = Svc(db, ai);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        // Het antwoord blijft staan (#93-discipline: diagnose, geen crash).
        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal(0, await db.Relations.CountAsync());
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("[relatievoorstellen: blok onparseerbaar", trace.BrainSteps);
        var log = await db.RunLogs.SingleAsync(l => l.Ref == "agentic-ask");
        Assert.Equal("error", log.Status);
        Assert.Contains("dit is geen JSON", log.Detail);
    }

    // ── Client-abort tijdens de agentic call: registratie landt tóch ───
    //    (review #107 — zelfde invariant als #110/StreamAnswerAsync)

    [Fact]
    public async Task Streaming_ClientAbortTijdensAgentic_RegistreertMetricEnTrace()
    {
        using var db = NewDb();
        await SeedAsync(db);
        using var cts = new CancellationTokenSource();
        // De handler annuleert het request-token zodra de agentic call start —
        // de client "loopt weg" midden in de (lange) agent-beurt.
        var ai = new AgenticAwareAi(onAgentic: () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var svc = Svc(db, ai);

        var deltas = new StringBuilder();
        var result = await WithModeAsync("auto", () => svc.AskStreamingAsync(
            InteractionQuestion, images: null, history: null,
            onMeta: _ => Task.CompletedTask,
            onDelta: d => { deltas.Append(d); return Task.CompletedTask; },
            cts.Token));

        // Geen exception, geen vangnet (nieuwe kosten zonder luisteraar) —
        // wél volledige registratie: metric én trace bestaan.
        Assert.False(result.Ok);
        Assert.Equal(RbAiClient.UnavailableAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls);
        Assert.Equal(0, deltas.Length);
        var trace = await db.AskTraces.SingleAsync();
        Assert.False(trace.Agentic);
        Assert.False(trace.Ok);
        Assert.Contains("[client afgehaakt", trace.BrainSteps);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.False(metric.Ok);
    }

    // ── RbAiClient: HttpClient-timeout wordt vangnet-null (review #107) ─

    [Fact]
    public async Task AskAgenticAsync_HttpClientTimeout_GeeftNullVoorHetVangnet()
    {
        // TaskCanceledException zónder geannuleerd token = HttpClient-timeout;
        // die moet null opleveren (vangnet), niet doorbubbelen.
        var client = new RbAiClient(
            new HttpClient(new ThrowingHandler(() => new TaskCanceledException("timeout")))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        Assert.Null(await client.AskAgenticAsync("vraag"));
    }

    [Fact]
    public async Task AskAgenticAsync_EchteClientAnnulering_BubbeltDoor()
    {
        using var cts = new CancellationTokenSource();
        var client = new RbAiClient(
            new HttpClient(new ThrowingHandler(() =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AskAgenticAsync("vraag", ct: cts.Token));
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>Alleen het FTS-kanaal vervangen door een woord-match — zelfde
    /// test-seam als AskServiceDegradationTests (tsvector vertaalt niet naar
    /// EF InMemory).</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance)
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

    /// <summary>Echte RbAiClient op een handler die de agentic-taak apart
    /// behandelt: telt de agentic-calls, kan hem laten slagen (antwoord +
    /// steps, optioneel met een relatievoorstellen-blok zoals rb-ai dat na
    /// de marker-splitsing als `relations` meestuurt, #120), laten falen
    /// (500 mét de al gedane steps in de fout-body, zoals rb-ai dat doet) of
    /// een eigen effect uitvoeren (bv. client-abort). Alle andere /ask-calls
    /// (rewrite + single-pass) antwoorden gewoon.</summary>
    private sealed class AgenticAwareAi(
        bool agenticFails = false, Action? onAgentic = null, string? relations = null)
    {
        public int AgenticCalls { get; private set; }

        public RbAiClient Client => new(
            new HttpClient(new Handler(this, agenticFails, onAgentic, relations))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        private sealed class Handler(
            AgenticAwareAi owner, bool fails, Action? onAgentic, string? relations)
            : HttpMessageHandler
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
                    onAgentic?.Invoke();
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
                            relations,
                            // #121: de som over alle agent-beurten, zoals
                            // rb-ai die uit het SDK-result rapporteert.
                            usage = new { inputTokens = 52_000, outputTokens = 700 },
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

    private static TestableAskService Svc(RbRulesDbContext db, AgenticAwareAi ai) =>
        new(db, FailingEmbeddings(), ai.Client);

    /// <summary>Failing Ollama (patroon #100): qv blijft null, dus de
    /// vector-kanalen vervallen — én het lege-retrieval-signaal mag dan per
    /// definitie niet vuren (embedding-uitval is geen lege bank).</summary>
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
        // Twee canonieke kaarten voor de kaartnamen-trigger van de gate;
        // Yasuo draagt Deflect zodat mechanic:deflect als brein-knoop bestaat
        // (ref-validatie + case-canonicalisatie in de #120-tests).
        db.Cards.AddRange(
            new Card { RiftboundId = "test-viktor", Name = "Viktor" },
            new Card { RiftboundId = "test-yasuo", Name = "Yasuo", Mechanics = ["Deflect"] });
        await db.SaveChangesAsync();
    }
}
