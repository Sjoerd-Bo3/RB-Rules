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

/// <summary>Eval-set uit echt verkeer (#387): promoveren vanuit een trace of
/// geheugenrij, de weigeringen (geen citaties, dubbel, verkeerde trust), en de
/// eval-run door de echte /ask-flow met gate + baseline.</summary>
public class EvalCaseServiceTests
{
    private const string SourceId = "riot-core-rules";

    [Fact]
    public async Task PromoteFromTrace_MaaktShadowGevalMetSectiesUitDeTrace()
    {
        using var db = NewDb();
        db.AskTraces.Add(new AskTrace
        {
            Question = "Mag een exhausted unit blokkeren?", QuestionType = "Ruling", Ok = true,
            Sections = "[§-bijgeladen: 1] §466.2.c, §466",
        });
        await db.SaveChangesAsync();
        var svc = new EvalCaseService(db);

        var p = await svc.PromoteFromTraceAsync(1, CancellationToken.None);

        Assert.True(p.Ok, p.Error);
        Assert.Equal("shadow", p.Case!.Status);
        Assert.Equal("Inference", p.Case.QueryType);
        Assert.Equal(["section:466.2.c", "section:466"], p.Case.ExpectedCitations);
        Assert.Equal("trace", p.Case.Origin);
        var row = await db.EvalCases.SingleAsync();
        Assert.StartsWith("eval-mag-een-exhausted-unit-blokkeren-", row.Id);
        // Dezelfde vraag nogmaals: geweigerd met uitleg, geen tweede rij.
        var again = await svc.PromoteFromTraceAsync(1, CancellationToken.None);
        Assert.False(again.Ok);
        Assert.Contains("al in de eval-set", again.Error);
        Assert.Equal(1, await db.EvalCases.CountAsync());
    }

    [Fact]
    public async Task PromoteFromTrace_ZonderCitatiesOfMislukt_Weigert()
    {
        using var db = NewDb();
        db.AskTraces.Add(new AskTrace { Question = "A?", Ok = true, Sections = "[kanaal-uitval: fts]" });
        db.AskTraces.Add(new AskTrace { Question = "B?", Ok = false, Sections = "§101" });
        await db.SaveChangesAsync();
        var svc = new EvalCaseService(db);

        Assert.Contains("Geen geciteerde", (await svc.PromoteFromTraceAsync(1, CancellationToken.None)).Error);
        Assert.Contains("mislukt", (await svc.PromoteFromTraceAsync(2, CancellationToken.None)).Error);
        Assert.Contains("niet gevonden", (await svc.PromoteFromTraceAsync(99, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task PromoteFromMemory_AlleenBevestigd_MetCitatiesUitDeRij()
    {
        using var db = NewDb();
        var cits = JsonSerializer.Serialize(new List<Citation>
        {
            new(1, "Core Rules", "https://x", "101", 1), new(2, "Core Rules", "https://x", null, 1),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.AnswerMemories.Add(new AnswerMemory
        {
            Question = "Is Viktor legaal?", QuestionNormalized = "x", QuestionType = "Legaliteit",
            Answer = "a", CitationsJson = cits, SourceSnapshot = ",", Trust = "candidate",
        });
        db.AnswerMemories.Add(new AnswerMemory
        {
            Question = "Is Viktor toegestaan?", QuestionNormalized = "x", QuestionType = "Legaliteit",
            Answer = "a", CitationsJson = cits, SourceSnapshot = ",", Trust = "confirmed",
        });
        await db.SaveChangesAsync();
        var svc = new EvalCaseService(db);

        var candidate = await svc.PromoteFromMemoryAsync(1, CancellationToken.None);
        Assert.Contains("bevestigde", candidate.Error);
        var confirmed = await svc.PromoteFromMemoryAsync(2, CancellationToken.None);
        Assert.True(confirmed.Ok, confirmed.Error);
        Assert.Equal(["section:101"], confirmed.Case!.ExpectedCitations); // citatie zonder § valt weg
        Assert.Equal("memory", confirmed.Case.Origin);
        Assert.Equal(2, confirmed.Case.OriginRef);
    }

    [Fact]
    public async Task Status_EnVerwijderen_EnTellers()
    {
        using var db = NewDb();
        db.AskTraces.Add(new AskTrace { Question = "A?", Ok = true, Sections = "§101" });
        await db.SaveChangesAsync();
        var svc = new EvalCaseService(db);
        var p = await svc.PromoteFromTraceAsync(1, CancellationToken.None);

        Assert.True(await svc.SetStatusAsync(p.Case!.Id, "Active", CancellationToken.None));
        Assert.Equal(new EvalCaseCounts(1, 0, 0), await svc.CountsAsync(CancellationToken.None));
        Assert.False(await svc.SetStatusAsync(p.Case.Id, "onzin", CancellationToken.None));
        Assert.Equal(EvalStatus.Active, (await svc.DomainCasesAsync(CancellationToken.None)).Single().Status);
        Assert.True(await svc.DeleteAsync(p.Case.Id, CancellationToken.None));
        Assert.False(await svc.DeleteAsync(p.Case.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EvalRun_ScoortDoorDeEchteAskFlow_LegtBaselineVast_EnGateOpVerzonnenCitatie()
    {
        using var db = NewDb();
        await SeedAsync(db);
        // Eén actief geval dat §101 verwacht.
        db.EvalCases.Add(new EvalCaseRecord
        {
            Id = "eval-viktor-1", Question = "Is Viktor legaal in constructed?", QueryType = "Factoid",
            Status = "active", ValidFrom = new DateOnly(2026, 1, 1),
            GoldSupportJson = """["section:101"]""", ExpectedCitationsJson = """["section:101"]""",
        });
        await db.SaveChangesAsync();
        var ai = CountingAi(RewriteJson, "**Oordeel:** Toegestaan. [1]");
        var runner = new EvalRunService(db,
            new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext()),
            new EvalCaseService(db));

        var first = await runner.RunAsync();

        Assert.True(first.Passed, first.Message);
        Assert.Equal(1, first.Cases);
        Assert.Contains("vastgelegd als baseline", first.Message);
        // Benchmark-isolatie: geen metric/trace/geheugen door de eval-run.
        Assert.Empty(await db.AskMetrics.ToListAsync());
        Assert.Empty(await db.AskTraces.ToListAsync());
        Assert.Empty(await db.AnswerMemories.ToListAsync());
        var run = await db.EvalRuns.SingleAsync();
        Assert.True(run.Passed);
        Assert.Contains("\"recall\":1", run.ResultsJson);
        Assert.NotEmpty(await db.EvalBaselines.ToListAsync());
        Assert.Equal("ok", (await db.RunLogs.SingleAsync(l => l.Kind == "eval")).Status);

        // Tweede run: het antwoord citeert nu óók §102, dat niet verwacht is —
        // citation-validity is een harde gate, dus de run faalt.
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = db.Documents.Single().Id, SourceId = SourceId, SectionCode = "102",
            ChunkIndex = 1, Page = 13, Text = "Viktor constructed legal unless banned.",
        });
        await db.SaveChangesAsync();
        var second = await runner.RunAsync();

        Assert.False(second.Passed, second.Message);
        Assert.Equal(1, second.Failures);
        Assert.Contains("harde overtredingen", second.Message);
        Assert.Equal(2, await db.EvalRuns.CountAsync());
        Assert.Equal("error", (await db.RunLogs.Where(l => l.Kind == "eval").OrderByDescending(l => l.Id).FirstAsync()).Status);
    }

    [Fact]
    public async Task EvalRun_ZonderGevallen_MeldtDatEerlijk()
    {
        using var db = NewDb();
        var ai = CountingAi(RewriteJson, "x");
        var runner = new EvalRunService(db,
            new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext()),
            new EvalCaseService(db));

        var r = await runner.RunAsync();

        Assert.Equal(0, r.Cases);
        Assert.Contains("geen eval-gevallen", r.Message);
        Assert.Empty(await db.EvalRuns.ToListAsync());
    }

    // ── seeding & stubs (zelfde patroon als AskServiceLegalityTemplateTests) ──

    private const string RewriteJson = """{"normalized":"Viktor legal","queries":[],"terms":[]}""";

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
            FileUrl = "https://example.com/core-rules.pdf",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101", ChunkIndex = 0, Page = 12,
            Text = "Viktor is legal in Constructed unless the card is banned.",
        });
        db.CardSets.Add(new CardSet { SetId = "OGN", Name = "Origins", PublishedOn = new DateOnly(2025, 10, 1) });
        db.Cards.Add(new Card { RiftboundId = "ogn-001", Name = "Viktor", SetId = "OGN", SetLabel = "Origins" });
        await db.SaveChangesAsync();
    }

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RequestUserContext userContext)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            userContext, NullLogger<AskService>.Instance,
            memory: new AnswerMemoryService(db, NullLogger<AnswerMemoryService>.Instance))
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

    private static RbAiClient CountingAi(string rewriteJson, string answer)
    {
        var call = 0;
        return new RbAiClient(
            new HttpClient(new StubHandler(_ =>
            {
                var payload = call++ % 2 == 0 ? new { answer = rewriteJson } : new { answer };
                return JsonMessage(payload);
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static HttpResponseMessage JsonMessage(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
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
}
