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

/// <summary>De isolatie-eis van #158, bewezen: een benchmarkrun
/// (AskOptions.Benchmark = true) mag NIETS leren of meten buiten de
/// benchmark-tabellen. Elke test hier staat naast een niet-benchmark
/// controletest die aantoont dat de harness de rij(en) wél schrijft zodra de
/// vlag uitstaat — anders zou "0 rijen" evengoed kunnen betekenen dat er
/// iets kapot is in plaats van bewust onderdrukt. Zelfde testopzet als
/// AskServiceAgenticTests/AskServiceDegradationTests (EF InMemory, echte
/// RbAiClient op een gestubde handler, FTS vervangen). In dezelfde
/// xUnit-collectie omdat de agentic-scenario's de proces-brede
/// ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServiceBenchmarkIsolationTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string InteractionQuestion =
        "When Viktor attacks while Yasuo has Deflect during a showdown, what happens?";
    private const string AgentAnswer = "**Oordeel:** Antwoord van de agent. [1]";

    // ── Niet-agentic pad: metric + trace ───────────────────────────────

    [Fact]
    public async Task Benchmark_GeenAskTraceEnGeenAskMetric()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai("**Oordeel:** Antwoord. [1]"));

        var result = await svc.AskAsync(Question, options: new AskOptions { Benchmark = true });

        Assert.True(result.Ok);
        Assert.Empty(await db.AskTraces.ToListAsync());
        Assert.Empty(await db.AskMetrics.ToListAsync());
    }

    [Fact]
    public async Task ControleTest_ZonderBenchmarkVlag_SchrijftWelTraceEnMetric()
    {
        // Bewijst dat de harness zelf ask_trace/ask_metric echt vult — anders
        // zou de test hierboven ook slagen als er iets stuk was.
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai("**Oordeel:** Antwoord. [1]"));

        await svc.AskAsync(Question);

        Assert.Single(await db.AskTraces.ToListAsync());
        Assert.Single(await db.AskMetrics.ToListAsync());
    }

    [Fact]
    public async Task Benchmark_LegeRetrieval_OokDanGeenAskMetric()
    {
        // Het vroege return-pad (topIds leeg) heeft een eigen RecordMetricAsync-
        // aanroep — apart bewezen omdat die niet dezelfde codeweg volgt als de
        // afronding onderaan AskCoreAsync.
        using var db = NewDb();
        // Bewust geen RuleChunks: topIds blijft leeg.
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = Svc(db, Ai("irrelevant"));

        var result = await svc.AskAsync(
            "Vraag zonder enige match", options: new AskOptions { Benchmark = true });

        Assert.False(result.Ok);
        Assert.Empty(await db.AskMetrics.ToListAsync());
        Assert.Empty(await db.AskTraces.ToListAsync());
    }

    // ── Model-sweep (#174): AskOptions.Model reist door naar rb-ai ─────

    [Fact]
    public async Task Benchmark_MetModelOverride_ReistDoorNaarDeRbAiPayload()
    {
        // Directe bedradingsproef (los van de sweep-servicetests): de
        // model-override op AskOptions komt daadwerkelijk als "model"-veld
        // in de JSON-payload naar rb-ai terecht — niet alleen door de
        // C#-objectgraaf, maar op de wire. Zonder override (de bestaande
        // /ask-calls) blijft "model" gewoon afwezig/null.
        using var db = NewDb();
        await SeedRulesAsync(db);
        var seenModels = new List<string?>();
        var svc = Svc(db, AiCapturingModel(seenModels, "**Oordeel:** Antwoord. [1]"));

        await svc.AskAsync(Question, options: new AskOptions { Benchmark = true, Model = "claude-opus-4-8" });

        Assert.Contains("claude-opus-4-8", seenModels);
    }

    [Fact]
    public async Task ControleTest_ZonderModelOverride_GeenModelVeldOpDePayload()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var seenModels = new List<string?>();
        var svc = Svc(db, AiCapturingModel(seenModels, "**Oordeel:** Antwoord. [1]"));

        await svc.AskAsync(Question, options: new AskOptions { Benchmark = true });

        Assert.DoesNotContain(seenModels, m => m is not null);
    }

    // ── Agentic pad: relatie-terugkoppeling (#120) ─────────────────────

    private const string ProposalsBlock = """
        {"relations": [
          {"from": "card:test-viktor", "to": "card:test-yasuo", "kind": "counters", "explanation": "Viktor omzeilt Yasuo's Deflect met een niet-targeted effect."}
        ]}
        """;

    [Fact]
    public async Task Benchmark_AgenticMetRelatievoorstellen_GeenRelationsEnGeenRelationKinds()
    {
        using var db = NewDb();
        await SeedAgenticAsync(db);
        var ai = new AgenticAwareAi(relations: ProposalsBlock);
        var svc = Svc(db, ai.Client);

        var result = await WithModeAsync("auto", () => svc.AskAsync(
            InteractionQuestion, options: new AskOptions { Benchmark = true }));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Equal(1, ai.AgenticCalls); // de agentic-gate zelf werkte gewoon
        Assert.Empty(await db.Relations.ToListAsync());
        Assert.Empty(await db.RelationKinds.ToListAsync());
        Assert.Empty(await db.AskTraces.ToListAsync());
        Assert.Empty(await db.AskMetrics.ToListAsync());
    }

    [Fact]
    public async Task ControleTest_AgenticZonderBenchmarkVlag_SlaatRelatievoorstellenWelOp()
    {
        using var db = NewDb();
        await SeedAgenticAsync(db);
        var ai = new AgenticAwareAi(relations: ProposalsBlock);
        var svc = Svc(db, ai.Client);

        var result = await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        Assert.Equal(AgentAnswer, result.Answer);
        Assert.Single(await db.Relations.ToListAsync());
        Assert.Single(await db.AskTraces.ToListAsync());
    }

    // ── Nieuwe schrijfpaden na de rebase: #153 (escalated_by op ask_metric
    //    + ask_trace) en #157 (ip_hash op ask_trace) — bewijs dat óók die
    //    niets schrijven onder benchmark ──────────────────────────────────

    private const string TestIpHash = "test-ip-hash-abc123";

    [Fact]
    public async Task ControleTest_GateEscalatieMetIpHash_SchrijftEscalatedByEnIpHash()
    {
        // Zonder benchmark, mét een gate-escalatie (interactievraag, 2
        // kaartnamen) én een IpHash op de context (zoals de quota-filter die
        // zet, #157): de metric- én trace-rij dragen dan escalated_by="gate"
        // (#153) en de trace draagt ip_hash (#157). Dit is de "levende"
        // referentie waartegen de benchmark-onderdrukking hieronder telt.
        using var db = NewDb();
        await SeedAgenticAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai.Client, new RequestUserContext { IpHash = TestIpHash });

        await WithModeAsync("auto", () => svc.AskAsync(InteractionQuestion));

        var metric = Assert.Single(await db.AskMetrics.ToListAsync());
        Assert.Equal("gate", metric.EscalatedBy);
        var trace = Assert.Single(await db.AskTraces.ToListAsync());
        Assert.Equal("gate", trace.EscalatedBy);
        Assert.Equal(TestIpHash, trace.IpHash);
    }

    [Fact]
    public async Task Benchmark_GateEscalatieMetIpHash_SchrijftGeenEscalatedByEnGeenIpHash()
    {
        // Exact hetzelfde scenario, maar mét de benchmark-vlag: de escalatie
        // gebeurt nog steeds (agentic-call werd gedaan), maar er komt géén
        // metric- en géén trace-rij — dus ook geen escalated_by-quotumtelling
        // (#153) en geen ip_hash-stempel (#157). De nieuwe schrijfpaden lekken
        // niets onder benchmark.
        using var db = NewDb();
        await SeedAgenticAsync(db);
        var ai = new AgenticAwareAi();
        var svc = Svc(db, ai.Client, new RequestUserContext { IpHash = TestIpHash });

        await WithModeAsync("auto", () => svc.AskAsync(
            InteractionQuestion, options: new AskOptions { Benchmark = true }));

        Assert.Equal(1, ai.AgenticCalls); // de escalatie zélf werkte gewoon
        Assert.Empty(await db.AskMetrics.ToListAsync());
        Assert.Empty(await db.AskTraces.ToListAsync());
    }

    // ── Claims/rulings: alleen gelezen, nooit geschreven — met of zonder
    //    benchmark-vlag verandert het aantal rijen niet ─────────────────

    [Fact]
    public async Task Benchmark_BestaandeClaimsEnVerifiedRulings_AantalBlijftOngewijzigd()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        db.Claims.Add(new Claim
        {
            TopicType = "mechanic", TopicRef = "Deflect",
            Statement = "Bestaande community-claim.", Status = "accepted",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "answer", Ref = "up", Text = "Bestaande geverifieerde ruling.",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = Svc(db, Ai("**Oordeel:** Antwoord. [1]"));

        await svc.AskAsync(Question, options: new AskOptions { Benchmark = true });

        // Retrieval leest deze tabellen (rulingBlock/claimsBlock) maar schrijft
        // er nooit in — vóór en na de benchmarkrun exact 1 rij van elk.
        Assert.Single(await db.Claims.ToListAsync());
        Assert.Single(await db.Corrections.ToListAsync());
        Assert.Empty(await db.AskTraces.ToListAsync());
    }

    // --- testinfra (patroon AskServiceDegradationTests/AskServiceAgenticTests) ---

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RequestUserContext userContext)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            userContext, NullLogger<AskService>.Instance)
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

    private static TestableAskService Svc(
        RbRulesDbContext db, RbAiClient ai, RequestUserContext? userContext = null) =>
        new(db, FailingEmbeddings(), ai, userContext ?? new RequestUserContext());

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Echte RbAiClient op een simpele handler: elke /ask-call
    /// (rewrite én single-pass) antwoordt hetzelfde.</summary>
    private static RbAiClient Ai(string answer) => new(
        new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, new { answer })))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Zelfde vaste antwoord als <see cref="Ai"/>, maar legt het
    /// "model"-veld van élke /ask-payload vast in <paramref name="seenModels"/>
    /// (#174: bewijs dat AskOptions.Model daadwerkelijk op de wire naar rb-ai
    /// gaat). Synchrone body-lezing is hier veilig — geen echte netwerk-I/O.</summary>
    private static RbAiClient AiCapturingModel(List<string?> seenModels, string answer) => new(
        new HttpClient(new StubHandler(req =>
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            seenModels.Add(
                doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null);
            return Json(HttpStatusCode.OK, new { answer });
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Zelfde agentic-bewuste handler als AskServiceAgenticTests:
    /// telt agentic-calls en kan een relatievoorstellen-blok meesturen (#120).</summary>
    private sealed class AgenticAwareAi(string? relations = null)
    {
        public int AgenticCalls { get; private set; }

        public RbAiClient Client => new(
            new HttpClient(new Handler(this, relations))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        private sealed class Handler(AgenticAwareAi owner, string? relations) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                if (body.Contains("\"task\":\"agentic\""))
                {
                    owner.AgenticCalls++;
                    return Json(HttpStatusCode.OK, new
                    {
                        answer = AgentAnswer,
                        steps = new[] { "semantic_search {\"q\":\"Deflect showdown\"}" },
                        relations,
                        usage = new { inputTokens = 1000, outputTokens = 100 },
                    });
                }
                return Json(HttpStatusCode.OK, new { answer = AgentAnswer });
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    /// <summary>Failing Ollama (patroon #100): qv blijft null — de degradatie
    /// naar FTS is voor deze isolatietests irrelevant, dus dit is de
    /// eenvoudigste stub (geen echte Ollama nodig in de testomgeving).</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static async Task<T> WithModeAsync<T>(string mode, Func<Task<T>> body)
    {
        Environment.SetEnvironmentVariable("ASK_AGENTIC", mode);
        try { return await body(); }
        finally { Environment.SetEnvironmentVariable("ASK_AGENTIC", null); }
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

    private static async Task SeedRulesAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash",
            FileUrl = "https://example.com/core-rules.pdf",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedAgenticAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        var doc = new Document { SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash" };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "466.2",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
        });
        db.Cards.AddRange(
            new Card { RiftboundId = "test-viktor", Name = "Viktor" },
            new Card { RiftboundId = "test-yasuo", Name = "Yasuo", Mechanics = ["Deflect"] });
        await db.SaveChangesAsync();
    }
}
