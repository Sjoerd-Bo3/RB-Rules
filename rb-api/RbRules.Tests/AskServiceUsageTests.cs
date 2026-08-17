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

/// <summary>Servicetests voor de token-boekhouding per vraag (#121): rb-ai
/// stuurt usage mee in /ask en in het done-frame van /ask/stream; AskService
/// telt de calls van één vraag op (rewrite + antwoord) en boekt het totaal op
/// ask_metric. Een oude rb-ai zonder usage-veld mag niets breken: dan blijft
/// het totaal null (onbekend ≠ 0). Zelfde testopzet als
/// AskServiceDegradationTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen) en dezelfde xUnit-collectie: de agentic-tests in
/// die collectie zetten de proces-brede ASK_AGENTIC-env.</summary>
[Collection("ask-service-env")]
public class AskServiceUsageTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";

    [Fact]
    public async Task AskAsync_UsagePerCall_OpgeteldInMetric()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Eerste /ask-call is de rewrite, de tweede het antwoord — de metric
        // hoort de som van beide te boeken (de vraag kostte beide calls).
        var svc = Svc(db, SequenceAi(
            new { answer = Answer, usage = new { inputTokens = 100, outputTokens = 10 } },
            new { answer = Answer, usage = new { inputTokens = 2_000, outputTokens = 300 } }));

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal(2_100, metric.InputTokens);
        Assert.Equal(310, metric.OutputTokens);
    }

    [Fact]
    public async Task AskStreamingAsync_UsageUitSlotframe_OpgeteldInMetric()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Rewrite (gewone /ask) mét usage; het antwoord komt via /ask/stream,
        // waar de usage in het done-frame zit (#121).
        var svc = Svc(db, new RbAiClient(
            new HttpClient(new StubHandler(req =>
                req.RequestUri!.AbsolutePath == "/ask/stream"
                    ? Ndjson(
                        JsonSerializer.Serialize(new { type = "delta", text = Answer }),
                        JsonSerializer.Serialize(new
                        {
                            type = "done", answer = Answer,
                            usage = new { inputTokens = 1_500, outputTokens = 250 },
                        }))
                    : JsonMessage(new
                    {
                        answer = Answer,
                        usage = new { inputTokens = 100, outputTokens = 10 },
                    })))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance));

        var result = await svc.AskStreamingAsync(
            Question, images: null, history: null,
            onMeta: _ => Task.CompletedTask,
            onDelta: _ => Task.CompletedTask);

        Assert.True(result.Ok);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal(1_600, metric.InputTokens);
        Assert.Equal(260, metric.OutputTokens);
    }

    [Fact]
    public async Task AskAsync_OudeRbAiZonderUsage_MetricTokensBlijvenNull()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Respons-vorm van vóór #121: alleen {answer}. Niets mag breken en de
        // metric boekt "onbekend" (null), niet 0.
        var svc = Svc(db, SequenceAi(new { answer = Answer }));

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Null(metric.InputTokens);
        Assert.Null(metric.OutputTokens);
    }

    [Fact]
    public async Task AskAsync_AlleenRewriteGafUsage_DeelsomBlijftGeboekt()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Gemengd (rewrite mét, antwoordcall zónder usage): het wél gemeten
        // deel telt — null + x = x, geen weggegooide meting.
        var svc = Svc(db, SequenceAi(
            new { answer = Answer, usage = new { inputTokens = 100, outputTokens = 10 } },
            new { answer = Answer }));

        await svc.AskAsync(Question);

        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal(100, metric.InputTokens);
        Assert.Equal(10, metric.OutputTokens);
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

    /// <summary>RbAiClient waarvan /ask de payloads in volgorde teruggeeft
    /// (de laatste herhaalt): call 1 = rewrite, call 2 = antwoord. De
    /// antwoorden bevatten geen accolades, dus de rewrite-parse levert null
    /// op (rauwe-vraag-pad).</summary>
    private static RbAiClient SequenceAi(params object[] payloads)
    {
        var call = 0;
        return new RbAiClient(
            new HttpClient(new StubHandler(_ =>
                JsonMessage(payloads[Math.Min(call++, payloads.Length - 1)])))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static HttpResponseMessage JsonMessage(object payload) => new(HttpStatusCode.OK)
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
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
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

    /// <summary>Failing Ollama (patroon #100): de vector-kanalen vervallen —
    /// deze tests draaien om de token-boekhouding, niet om retrieval.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

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
}
