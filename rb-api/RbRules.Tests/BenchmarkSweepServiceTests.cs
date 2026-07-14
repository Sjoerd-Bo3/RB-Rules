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

/// <summary>Servicetests voor de model-sweep (#174, uitbreiding op #158):
/// elk model uit de lijst <see cref="BenchmarkService.RunsPerModel"/>×, met de
/// juiste Model/RunIndex/SweepId-groepering op BenchmarkRun, én het bewijs
/// dat de model-override daadwerkelijk op de wire naar rb-ai gaat (niet
/// alleen door de C#-objectgraaf reist). Zelfde testopzet als
/// BenchmarkServiceTests (EF InMemory, echte RbAiClient op een gestubde
/// handler) en dezelfde xUnit-collectie als AskServiceBenchmarkIsolation-
/// Tests (deze tests muteren AI_BENCHMARK_MODELS, net als die de ASK_AGENTIC-
/// proces-env muteren).</summary>
[Collection("ask-service-env")]
public class BenchmarkSweepServiceTests
{
    private const string SourceId = "riot-core-rules";
    private const string OverlapWord = "zzzoverlapzzz";

    [Fact]
    public async Task RunSweepAsync_TweeModellenTweeRunsElk_JuisteAantalEnGroepering()
    {
        using var db = NewDb();
        await SeedRuleChunkAsync(db);
        db.BenchmarkQuestions.AddRange(
            Question("q1", correctIndex: 1),
            Question("q2", correctIndex: null));
        await db.SaveChangesAsync();

        var seenModels = new List<string?>();
        var svc = new BenchmarkService(
            db, Svc(db, RecordingAi(seenModels, "**Oordeel:** Antwoord. [1]\n\nGEKOZEN OPTIE: B")));

        var progress = new List<string>();
        var result = await svc.RunSweepAsync(models: ["model-a", "model-b"], progress: progress.Add);

        // 2 modellen × 2 runs (BenchmarkService.RunsPerModel) = 4 runs, elk
        // met de volledige (2-vragen) set.
        Assert.Equal(4, result.RunIds.Count);
        Assert.NotEmpty(progress);
        // Geschatte omvang (issue-eis) staat vóór de eerste ask-aanroep al in
        // het log: 2 modellen × 2 runs × 2 vragen = 8 ask-aanroepen.
        Assert.Contains(progress, p =>
            p.Contains("2 modellen") && p.Contains("× 2 runs") && p.Contains("8 ask-aanroepen"));

        var runs = await db.BenchmarkRuns.Where(r => r.SweepId == result.SweepId).ToListAsync();
        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.Equal(result.SweepId, r.SweepId));
        foreach (var model in new[] { "model-a", "model-b" })
        {
            var modelRuns = runs.Where(r => r.Model == model).OrderBy(r => r.RunIndex).ToList();
            Assert.Equal(2, modelRuns.Count);
            Assert.Equal([1, 2], modelRuns.Select(r => r.RunIndex));
            Assert.All(modelRuns, r => Assert.Equal(2, r.QuestionCount));
            // Score is vastgelegd (niet null) — q1 is gekeyed op index 1, het
            // gestubde antwoord kiest altijd "B" (index 1) ⇒ 100%.
            Assert.All(modelRuns, r => Assert.Equal(100.0, r.ScorePercent));
        }

        // Elke run boekte zijn eigen benchmark_result-rijen (2 vragen × 4 runs).
        Assert.Equal(8, await db.BenchmarkResults.CountAsync());

        // Model-override reisde daadwerkelijk over de wire naar rb-ai (#174):
        // per model precies 4 finale-antwoord-aanroepen (2 runs × 2 vragen).
        // De query-rewrite-aanroep (RunRewriteAsync) draagt bewust geen model
        // mee — "zelfde retrieval, ander model" — dus die voegt alleen
        // null-entries toe, geen ruis voor deze telling.
        Assert.Equal(4, seenModels.Count(m => m == "model-a"));
        Assert.Equal(4, seenModels.Count(m => m == "model-b"));

        // Isolatie (#158) blijft ook via de sweep-route intact.
        Assert.Empty(await db.AskTraces.ToListAsync());
        Assert.Empty(await db.AskMetrics.ToListAsync());
    }

    [Fact]
    public async Task RunSweepAsync_ZonderModellenParameter_ValtTerugOpResolveModels()
    {
        using var db = NewDb();
        await SeedRuleChunkAsync(db);
        db.BenchmarkQuestions.Add(Question("q1", correctIndex: 0));
        await db.SaveChangesAsync();

        var previous = Environment.GetEnvironmentVariable("AI_BENCHMARK_MODELS");
        Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", "custom-model-x,custom-model-y");
        try
        {
            var svc = new BenchmarkService(db, Svc(db, "**Oordeel:** Antwoord. [1]\n\nGEKOZEN OPTIE: A"));
            var result = await svc.RunSweepAsync(models: null, progress: _ => { });

            Assert.Equal(4, result.RunIds.Count); // 2 modellen × 2 runs
            var models = (await db.BenchmarkRuns.Where(r => r.SweepId == result.SweepId).ToListAsync())
                .Select(r => r.Model).Distinct().OrderBy(m => m).ToList();
            Assert.Equal(["custom-model-x", "custom-model-y"], models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", previous);
        }
    }

    [Fact]
    public void ResolveModels_ZonderEnv_GeeftVerstandigeDefaultMetBestaandeAiTsModellen()
    {
        var previous = Environment.GetEnvironmentVariable("AI_BENCHMARK_MODELS");
        Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", null);
        try
        {
            var models = BenchmarkService.ResolveModels();
            Assert.NotEmpty(models);
            // Puur additief: de modellen die rb-ai/src/ai.ts's MODEL-record al
            // voor cheap/hard gebruikt horen in de default te zitten, zodat de
            // sweep in elk geval het bestaande gedrag dekt.
            Assert.Contains("claude-sonnet-4-6", models);
            Assert.Contains("claude-opus-4-8", models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", previous);
        }
    }

    [Fact]
    public void ResolveModels_MetEnv_ParstCommaGescheidenEnTrimt()
    {
        var previous = Environment.GetEnvironmentVariable("AI_BENCHMARK_MODELS");
        Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", " model-a ,model-b, ,model-a");
        try
        {
            var models = BenchmarkService.ResolveModels();
            Assert.Equal(["model-a", "model-b"], models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_BENCHMARK_MODELS", previous);
        }
    }

    [Fact]
    public async Task RunSweepAsync_ZonderVragen_GeeftNetteMeldingEnRunLog_ZonderRunRijen()
    {
        using var db = NewDb();
        var svc = new BenchmarkService(db, Svc(db, "irrelevant"));

        var result = await svc.RunSweepAsync(models: ["model-a"], progress: _ => { });

        Assert.Empty(result.RunIds);
        Assert.Contains("seed ontbreekt", result.Message);
        Assert.Empty(await db.BenchmarkRuns.ToListAsync());
        var log = await db.RunLogs.SingleAsync();
        Assert.Equal("benchmarksweep", log.Kind);
        Assert.Equal("error", log.Status);
    }

    // --- testinfra (patroon BenchmarkServiceTests) ---------------------------------

    private static BenchmarkQuestion Question(string key, int? correctIndex) => new()
    {
        ExternalKey = key, Category = "test",
        Question = $"Test vraag ({key}) met woord {OverlapWord}.",
        Options = ["Opt A", "Opt B"],
        CorrectIndex = correctIndex,
    };

    private static async Task SeedRuleChunkAsync(RbRulesDbContext db)
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
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "1",
            ChunkIndex = 0, Text = $"Regeltekst met het woord {OverlapWord} voor de testmatch.",
        });
        await db.SaveChangesAsync();
    }

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

    private static AskService Svc(RbRulesDbContext db, string fixedAnswer) =>
        new TestableAskService(db, FailingEmbeddings(), Ai(fixedAnswer));

    private static AskService Svc(RbRulesDbContext db, RbAiClient ai) =>
        new TestableAskService(db, FailingEmbeddings(), ai);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Elke /ask-call (rewrite én single-pass) geeft hetzelfde vaste
    /// antwoord terug.</summary>
    private static RbAiClient Ai(string answer) => new(
        new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { answer }), Encoding.UTF8, "application/json"),
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Zelfde vaste antwoord als <see cref="Ai"/>, maar legt bovendien
    /// het "model"-veld van élke /ask-payload vast in <paramref name="seenModels"/>
    /// (#174-bewijs: de override reist echt over de wire naar rb-ai, niet
    /// alleen door de C#-objectgraaf). De JSON-body wordt synchroon gelezen —
    /// voor deze in-memory stub is dat veilig (geen echte netwerk-I/O).</summary>
    private static RbAiClient RecordingAi(List<string?> seenModels, string answer) => new(
        new HttpClient(new StubHandler(req =>
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            seenModels.Add(
                doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { answer }), Encoding.UTF8, "application/json"),
            };
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

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
}
