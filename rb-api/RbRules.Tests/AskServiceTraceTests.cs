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

/// <summary>Het volledige gesprek in de vraag-trace (#143): de trace draagt
/// naast de route-metadata nu ook het definitieve antwoord en een JSON-
/// snapshot van de doorvraag-beurten (#41) — op het gewone pad, op het
/// streamingpad (het slotframe is leidend, niet de deltas) en bij AI-uitval
/// (UnavailableAnswer is eerlijke data, Ok=false). Zelfde testopzet als
/// AskServiceDegradationTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen); zelfde collectie omdat de agentic-tests de
/// proces-brede ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServiceTraceTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Werkt Deflect ook tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook dan. [1]";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AskAsync_MetDoorvraagHistorie_TraceDraagtAntwoordEnGesprek()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai(Answer));

        // Vier beurten: de service capt op de laatste drie — het snapshot
        // moet exact de beurten zijn die als GESPREK-blok meegingen.
        var result = await svc.AskAsync(Question, history:
        [
            new AskTurn("beurt 1", "antwoord 1"),
            new AskTurn("beurt 2", "antwoord 2"),
            new AskTurn("beurt 3", "antwoord 3"),
            new AskTurn("beurt 4", "antwoord 4"),
        ]);

        Assert.Equal(Answer, result.Answer);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal(Answer, trace.Answer);
        Assert.NotNull(trace.History);
        var turns = JsonSerializer.Deserialize<List<AskTurn>>(trace.History!, Web)!;
        Assert.Equal(
            [new("beurt 2", "antwoord 2"), new("beurt 3", "antwoord 3"),
             new AskTurn("beurt 4", "antwoord 4")],
            turns);
        // Camel-case zoals de rest van de API-payloads.
        Assert.Contains("\"question\":", trace.History);
    }

    [Fact]
    public async Task AskAsync_EersteVraag_HistoryBlijftNull()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, Ai(Answer));

        await svc.AskAsync(Question);

        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal(Answer, trace.Answer);
        Assert.Null(trace.History);
    }

    [Fact]
    public async Task AskStreamingAsync_TraceBoektHetSlotframe_NietDeDeltas()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Het done-frame wijkt bewust af van de deltas: de trace moet het
        // definitieve antwoord uit het slotframe boeken, niet de losse deltas.
        const string final = "**Oordeel:** Ja — definitieve slotframe-tekst. [1]";
        var svc = Svc(db, new RbAiClient(
            new HttpClient(new StubHandler(req =>
                req.RequestUri!.AbsolutePath == "/ask/stream"
                    ? Ndjson(
                        JsonSerializer.Serialize(new { type = "delta", text = "Ja — " }),
                        JsonSerializer.Serialize(new { type = "delta", text = "deel twee" }),
                        JsonSerializer.Serialize(new { type = "done", answer = final }))
                    : Json(new { answer = "rewrite-antwoord zonder accolades" })))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance));

        var deltas = new StringBuilder();
        var result = await svc.AskStreamingAsync(
            Question, images: null,
            history: [new AskTurn("eerdere vraag", "eerder antwoord")],
            onMeta: _ => Task.CompletedTask,
            onDelta: d => { deltas.Append(d); return Task.CompletedTask; });

        Assert.True(result.Ok);
        Assert.Equal("Ja — deel twee", deltas.ToString());
        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal(final, trace.Answer);
        var turns = JsonSerializer.Deserialize<List<AskTurn>>(trace.History!, Web)!;
        Assert.Equal([new("eerdere vraag", "eerder antwoord")], turns);
    }

    [Fact]
    public async Task AskAsync_AiUitval_TraceDraagtUnavailableAnswer_OkFalse()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // rb-ai plat: elke call 500 → rewrite null én antwoord null.
        var svc = Svc(db, new RbAiClient(
            new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance));

        var result = await svc.AskAsync(Question, history:
            [new AskTurn("eerdere vraag", "eerder antwoord")]);

        // Eerlijke data (#143): ook het mislukte antwoord staat in de trace.
        Assert.False(result.Ok);
        var trace = await db.AskTraces.SingleAsync();
        Assert.False(trace.Ok);
        Assert.Equal(RbAiClient.UnavailableAnswer, trace.Answer);
        Assert.NotNull(trace.History);
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

    /// <summary>Failing Ollama (patroon #100): qv blijft null — de vector-
    /// kanalen doen niet mee, wat voor deze tests prima is.</summary>
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
