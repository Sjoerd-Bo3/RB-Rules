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

/// <summary>Servicetests voor de benchmark-job (#158): scoren over gemengd
/// gekeyde/ongekeyde vragen (issue-eis: alleen CorrectIndex != null telt mee),
/// en de idempotente seed-import (patroon Program.cs/SourceSeed). Zelfde
/// testopzet als de andere AskService-servicetests (EF InMemory, echte
/// RbAiClient op een gestubde handler).</summary>
[Collection("ask-service-env")]
public class BenchmarkServiceTests
{
    private const string SourceId = "riot-core-rules";
    private const string OverlapWord = "zzzoverlapzzz";

    [Fact]
    public async Task RunAsync_GemengdGekeydEnOngekeyd_ScoortAlleenDeGekeydeVragen()
    {
        using var db = NewDb();
        await SeedRuleChunkAsync(db);
        // q1: model kiest B (index 1) → correct. q2: model kiest B, sleutel is
        // A (index 0) → fout. q3/q4: geen sleutel → tellen niet mee, maar
        // krijgen wel een ChosenIndex (de vraag draait gewoon mee).
        db.BenchmarkQuestions.AddRange(
            Question("q1", correctIndex: 1),
            Question("q2", correctIndex: 0),
            Question("q3", correctIndex: null),
            Question("q4", correctIndex: null));
        await db.SaveChangesAsync();
        var svc = new BenchmarkService(db, Svc(db, "**Oordeel:** Antwoord. [1]\n\nGEKOZEN OPTIE: B"));

        var progress = new List<string>();
        var result = await svc.RunAsync(label: null, progress.Add);

        Assert.Equal(4, result.Questions);
        Assert.Equal(2, result.Keyed);
        Assert.Equal(1, result.Correct);
        Assert.NotEmpty(progress);

        var run = await db.BenchmarkRuns.SingleAsync();
        Assert.Equal(4, run.QuestionCount);
        Assert.Equal(2, run.KeyedCount);
        Assert.Equal(1, run.CorrectCount);
        Assert.Equal(50.0, run.ScorePercent);
        Assert.NotNull(run.CompletedAt);

        var results = await db.BenchmarkResults
            .Include(r => r.Question)
            .OrderBy(r => r.Question!.ExternalKey)
            .ToListAsync();
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(1, r.ChosenIndex)); // altijd "B" herkend
        Assert.True(results[0].Correct);   // q1: B == correctIndex 1
        Assert.False(results[1].Correct);  // q2: B != correctIndex 0
        Assert.Null(results[2].Correct);   // q3: geen sleutel
        Assert.Null(results[3].Correct);   // q4: geen sleutel

        // Isolatie blijft ook via de job-route intact.
        Assert.Empty(await db.AskTraces.ToListAsync());
        Assert.Empty(await db.AskMetrics.ToListAsync());

        var log = await db.RunLogs.SingleAsync(l => l.Status == "ok");
        Assert.Equal("benchmark", log.Kind);
        Assert.Contains("50", log.Detail);
    }

    [Fact]
    public async Task RunAsync_GeenSleutelOpEnkeleVraag_ScorePercentIsNull()
    {
        using var db = NewDb();
        await SeedRuleChunkAsync(db);
        db.BenchmarkQuestions.Add(Question("q1", correctIndex: null));
        await db.SaveChangesAsync();
        var svc = new BenchmarkService(db, Svc(db, "**Oordeel:** Antwoord. [1]\n\nGEKOZEN OPTIE: A"));

        var result = await svc.RunAsync(label: null, _ => { });

        Assert.Equal(0, result.Keyed);
        var run = await db.BenchmarkRuns.SingleAsync();
        Assert.Null(run.ScorePercent);
        var single = await db.BenchmarkResults.SingleAsync();
        Assert.Null(single.Correct);
        Assert.Equal(0, single.ChosenIndex); // "A" wél herkend, alleen onscoorbaar
    }

    [Fact]
    public async Task RunAsync_GeenAntwoordMetLetter_ChosenIndexBlijftNull_GeenFout()
    {
        using var db = NewDb();
        await SeedRuleChunkAsync(db);
        db.BenchmarkQuestions.Add(Question("q1", correctIndex: 0));
        await db.SaveChangesAsync();
        // Antwoord zonder herkenbare "GEKOZEN OPTIE"-regel.
        var svc = new BenchmarkService(db, Svc(db, "**Oordeel:** Onduidelijk antwoord zonder keuze."));

        var result = await svc.RunAsync(label: null, _ => { });

        Assert.Equal(1, result.Keyed);
        Assert.Equal(0, result.Correct); // geen match ⇒ null-index ≠ correctIndex 0 ⇒ fout, geen crash
        var single = await db.BenchmarkResults.SingleAsync();
        Assert.Null(single.ChosenIndex);
        Assert.False(single.Correct);
    }

    [Fact]
    public async Task RunAsync_ZonderVragen_GeeftNetteMeldingEnRunLog_ZonderRunRij()
    {
        using var db = NewDb();
        var svc = new BenchmarkService(db, Svc(db, "irrelevant"));

        var result = await svc.RunAsync(label: null, _ => { });

        Assert.Equal(0, result.Questions);
        Assert.Contains("seed ontbreekt", result.Message);
        Assert.Empty(await db.BenchmarkRuns.ToListAsync());
        var log = await db.RunLogs.SingleAsync();
        Assert.Equal("benchmark", log.Kind);
        Assert.Equal("error", log.Status);
    }

    // ── Seed-import (patroon Program.cs/SourceSeed): idempotent op
    //    ExternalKey — ontbrekende vragen komen erbij, bestaande blijven
    //    (ook hun CorrectIndex) ongemoeid ─────────────────────────────────

    [Fact]
    public async Task SeedImport_LegeSet_ImporteertAlle12Vragen()
    {
        using var db = NewDb();

        await ImportMissingAsync(db);

        Assert.Equal(12, await db.BenchmarkQuestions.CountAsync());
    }

    [Fact]
    public async Task SeedImport_IsIdempotent_EnLaatBestaandeCorrectIndexOngemoeid()
    {
        using var db = NewDb();
        // Alsof judge-1 al eerder geïmporteerd én gekeyed is door Sjoerd.
        db.BenchmarkQuestions.Add(new BenchmarkQuestion
        {
            ExternalKey = "judge-1", Category = "judge", Question = "bestaande vraag",
            Options = ["a", "b"], CorrectIndex = 1,
        });
        await db.SaveChangesAsync();

        await ImportMissingAsync(db);
        await ImportMissingAsync(db); // nogmaals draaien = geen effect

        Assert.Equal(12, await db.BenchmarkQuestions.CountAsync());
        var judge1 = await db.BenchmarkQuestions.SingleAsync(q => q.ExternalKey == "judge-1");
        Assert.Equal("bestaande vraag", judge1.Question); // niet overschreven
        Assert.Equal(1, judge1.CorrectIndex);              // sleutel blijft staan
    }

    /// <summary>Zelfde idempotente import als Program.cs (SourceSeed-patroon):
    /// hier los getest omdat top-level statements in Program.cs niet
    /// rechtstreeks aanroepbaar zijn.</summary>
    private static async Task ImportMissingAsync(RbRulesDbContext db)
    {
        var existing = await db.BenchmarkQuestions.Select(q => q.ExternalKey).ToHashSetAsync();
        foreach (var q in BenchmarkSeed.Defaults.Where(q => !existing.Contains(q.ExternalKey)))
            db.BenchmarkQuestions.Add(q);
        await db.SaveChangesAsync();
    }

    // --- testinfra ---------------------------------------------------------

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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Elke /ask-call (rewrite én single-pass) geeft hetzelfde vaste
    /// antwoord terug — de rewrite-parse levert null op (geen JSON), dus de
    /// vraag zoekt met de rauwe (samengestelde) tekst.</summary>
    private static RbAiClient Ai(string answer) => new(
        new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { answer }), Encoding.UTF8, "application/json"),
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
