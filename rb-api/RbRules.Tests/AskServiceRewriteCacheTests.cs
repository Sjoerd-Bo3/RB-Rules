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

/// <summary>Servicetests voor de rewrite-cache (#152): een cache-hit slaat de
/// volledige rewrite-call (rb-ai) over — zelfde uitkomst, geen extra
/// wandkloktijd (rewriteMs = 0) — en een null-uitkomst (rewrite-uitval of
/// onzin-output) wordt nooit gecacht, zodat de volgende vraag het opnieuw
/// probeert i.p.v. voorgoed gedegradeerd te blijven. Zelfde testopzet als
/// AskServiceDegradationTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen); zelfde xUnit-collectie vanwege de proces-brede
/// ASK_AGENTIC-env van de agentic-tests.</summary>
[Collection("ask-service-env")]
public class AskServiceRewriteCacheTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";
    private const string RewriteJson =
        """{"normalized":"how does deflect work during a showdown","queries":["deflect showdown"],"terms":[]}""";

    [Fact]
    public async Task AskAsync_TweedeIdentiekeVraag_CacheHit_GeenTweedeRewriteCall()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var cache = new RewriteCache();
        var ai = new CountingAi(RewriteJson);
        var svc = Svc(db, ai.Client, cache);

        await svc.AskAsync(Question);
        await svc.AskAsync(Question);

        // Twee vragen, maar de rewrite-call hoort er maar één keer bij te
        // zitten: de tweede is een cache-hit.
        Assert.Equal(1, ai.RewriteCalls);
        Assert.Equal(2, ai.AnswerCalls);
    }

    [Fact]
    public async Task AskAsync_AndereSchrijfwijzeMaarGelijkeSleutel_CacheHit()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var cache = new RewriteCache();
        var ai = new CountingAi(RewriteJson);
        var svc = Svc(db, ai.Client, cache);

        await svc.AskAsync(Question);
        // Rand-witruimte en hoofdlettergebruik verschillen — NormalizeKey
        // (trim + lowercase) hoort dit toch als dezelfde sleutel te zien.
        await svc.AskAsync($"  {Question.ToUpperInvariant()}  ");

        Assert.Equal(1, ai.RewriteCalls);
    }

    [Fact]
    public async Task AskAsync_CacheHit_RewriteMsIsNulInDeFaseTimings()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var cache = new RewriteCache();
        var ai = new CountingAi(RewriteJson);
        var svc = Svc(db, ai.Client, cache);

        await svc.AskAsync(Question);
        await svc.AskAsync(Question);

        var traces = await db.AskTraces.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, traces.Count);
        var secondPhases = AskPhases.Parse(traces[1].PhaseTimings);
        Assert.NotNull(secondPhases);
        // Geen rb-ai-call gemaakt voor de rewrite van de tweede vraag —
        // de meting reflecteert dat (fase-doc: "0 bij een cache-hit").
        Assert.Equal(0, secondPhases!.RewriteMs);
    }

    [Fact]
    public async Task AskAsync_RewriteFaalt_NooitGecacht_VolgendeVraagProbeertOpnieuw()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var cache = new RewriteCache();
        // Rewrite-call faalt hard (uitval) — de antwoordcall werkt gewoon.
        var ai = new CountingAi(rewriteAnswer: null);
        var svc = Svc(db, ai.Client, cache);

        var first = await svc.AskAsync(Question);
        var second = await svc.AskAsync(Question);

        // Beide vragen slagen alsnog (degradatie naar de rauwe tekst), maar
        // een mislukte rewrite mag nooit blijven "hangen" in de cache — dus
        // twee pogingen, geen enkele cache-hit.
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(2, ai.RewriteCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task AskAsync_ZonderCache_ElkeVraagEenEigenRewriteCall()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Geen RewriteCache meegegeven (patroon dbFactory: optioneel,
        // default null) — het bestaande gedrag van vóór #152 blijft exact
        // hetzelfde: elke vraag een eigen rewrite-call.
        var ai = new CountingAi(RewriteJson);
        var svc = Svc(db, ai.Client, rewriteCache: null);

        await svc.AskAsync(Question);
        await svc.AskAsync(Question);

        Assert.Equal(2, ai.RewriteCalls);
    }

    // --- testinfra (patroon AskServiceDegradationTests) -------------------

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RewriteCache? rewriteCache)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance,
            dbFactory: null, rewriteCache: rewriteCache)
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
        RbRulesDbContext db, RbAiClient ai, RewriteCache? rewriteCache) =>
        new(db, FailingEmbeddings(), ai, rewriteCache);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    /// <summary>Echte RbAiClient die op prompt-inhoud routeert (rewrite vs.
    /// antwoordcall, zelfde patroon als AskServiceParallelRetrievalTests) en
    /// beide soorten calls telt.</summary>
    private sealed class CountingAi(string? rewriteAnswer)
    {
        public int RewriteCalls { get; private set; }
        public int AnswerCalls { get; private set; }

        public RbAiClient Client => new(
            new HttpClient(new StubHandler(async req =>
            {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
                if (prompt.Contains("Context-fragmenten:"))
                {
                    AnswerCalls++;
                    return Json(new { answer = Answer });
                }
                RewriteCalls++;
                return rewriteAnswer is null
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json(new { answer = rewriteAnswer });
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    /// <summary>Failing Ollama (#100-patroon): qv blijft null — deze tests
    /// draaien om de rewrite-cache, niet om de vector-retrieval.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError))))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
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
