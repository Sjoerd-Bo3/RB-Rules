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

/// <summary>Servicetests voor de per-fase-instrumentatie (#152): elke trace
/// draagt een parsebare PhaseTimings-JSON — op het gewone pad, op het
/// streamingpad en bij AI-uitval (Ok=false). De meting is diagnostiek en mag
/// een vraag nooit laten falen. Zelfde testopzet als
/// AskServiceDegradationTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen); zelfde xUnit-collectie omdat de agentic-tests de
/// proces-brede ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServicePhaseTimingTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";

    [Fact]
    public async Task AskAsync_TraceDraagtParsebareFaseTimings()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai(Answer));

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var trace = await db.AskTraces.SingleAsync();
        var phases = AskPhases.Parse(trace.PhaseTimings);
        Assert.NotNull(phases);
        AssertPlausibel(phases!, trace.DurationMs);
    }

    [Fact]
    public async Task AskStreamingAsync_TraceDraagtParsebareFaseTimings()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai(Answer));

        var result = await svc.AskStreamingAsync(
            Question, images: null, history: null,
            onMeta: _ => Task.CompletedTask,
            onDelta: _ => Task.CompletedTask);

        Assert.True(result.Ok);
        var trace = await db.AskTraces.SingleAsync();
        var phases = AskPhases.Parse(trace.PhaseTimings);
        Assert.NotNull(phases);
        AssertPlausibel(phases!, trace.DurationMs);
    }

    [Fact]
    public async Task AskAsync_AiUitval_OkFalse_FaseTimingsBlijvenAanwezig()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // rb-ai plat: rewrite én antwoordcall geven 500 — juist de mislukte
        // vraag moet meetbaar blijven (waar viel hij om, hoe lang duurde het).
        var svc = Svc(db, new RbAiClient(
            new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance));

        var result = await svc.AskAsync(Question);

        Assert.False(result.Ok);
        var trace = await db.AskTraces.SingleAsync();
        Assert.False(trace.Ok);
        var phases = AskPhases.Parse(trace.PhaseTimings);
        Assert.NotNull(phases);
        AssertPlausibel(phases!, trace.DurationMs);
    }

    /// <summary>Sanity op de meting zelf: geen negatieve fasen en het totaal
    /// spoort met DurationMs. De fasen overlappen (parallelle pipeline), dus
    /// een som-controle is hier bewust niet op zijn plaats.</summary>
    private static void AssertPlausibel(AskPhases phases, int durationMs)
    {
        Assert.True(phases.RewriteMs >= 0);
        Assert.True(phases.EmbedMs >= 0);
        Assert.True(phases.RetrievalMs >= 0);
        Assert.True(phases.AiMs >= 0);
        Assert.True(phases.TotalMs >= 0);
        Assert.True(phases.TotalMs >= durationMs,
            $"TotalMs ({phases.TotalMs}) hoort minstens DurationMs ({durationMs}) te zijn " +
            "(zelfde stopwatch, iets later gelezen)");
    }

    // --- testinfra (patroon AskServiceDegradationTests) -------------------

    /// <summary>Alleen het FTS-kanaal vervangen door een woord-match:
    /// tsvector vertaalt niet naar EF InMemory.</summary>
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

    private static TestableAskService Svc(RbRulesDbContext db, RbAiClient ai) =>
        new(db, FailingEmbeddings(), ai);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Echte RbAiClient op een gestubde handler; het antwoord bevat
    /// geen accolades, dus de rewrite-parse levert null (rauwe-vraag-pad).</summary>
    private static RbAiClient Ai(string answer) => new(
        new HttpClient(new StubHandler(req => req.RequestUri!.AbsolutePath == "/ask/stream"
            ? Ndjson(
                JsonSerializer.Serialize(new { type = "delta", text = answer }),
                JsonSerializer.Serialize(new { type = "done", answer }))
            : Json(new { answer })))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Failing Ollama (#100-patroon): qv blijft null — de vector-
    /// kanalen vervallen; deze tests gaan over de meting, niet de retrieval.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Ndjson(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson"),
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AskService-tests).</summary>
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
}
