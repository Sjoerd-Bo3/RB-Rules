using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Infrastructure;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase ask-retrieval (#228): de wiring van de brein-GraphRAG-retrieval IN
/// AskService, achter de default-uit flag. Bewijst de kritieke invariant: flag UIT ⇒
/// /ask draait EXACT zoals nu (geen brein-call, geen BREIN-CONTEXT in de prompt, geen
/// AnswerTrace); flag AAN ⇒ de prompt wordt verrijkt met de trust-gelabelde
/// brein-context én er wordt een AnswerTrace weggeschreven; een retrieval-fout valt
/// netjes terug op de bestaande flow (geen 500, geen AnswerTrace).</summary>
[Collection("ask-service-env")]
public class AskServiceBreinRetrievalTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";

    // ── 1) Flag UIT ⇒ geen brein-call, geen verrijking, geen AnswerTrace ──

    [Fact]
    public async Task FlagUit_GeenBreinCall_PromptOngewijzigd_GeenAnswerTrace()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Ja. [1]");
        var spy = new SpyRetriever();
        var brein = new BreinRetrievalService(
            Orchestrator(spy), BreinRetrievalSettings.Disabled,
            NullLogger<BreinRetrievalService>.Instance);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        // Geen enkele poort geraakt: de flag-uit-poort schakelt de hele laag uit.
        Assert.False(spy.Called);
        // De prompt draagt geen brein-blok — byte-identiek aan het bestaande gedrag.
        Assert.DoesNotContain("BREIN-CONTEXT", ai.LastPrompt);
        // En er is geen AnswerTrace weggeschreven.
        Assert.Empty(await db.AnswerTraces.ToListAsync());
    }

    [Fact]
    public async Task GeenBreinService_PromptOngewijzigd()
    {
        // De meeste constructors geven geen brein-service mee (null): identiek gedrag.
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Ja. [1]");
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein: null);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.DoesNotContain("BREIN-CONTEXT", ai.LastPrompt);
        Assert.Empty(await db.AnswerTraces.ToListAsync());
    }

    // ── 2) Flag AAN ⇒ prompt verrijkt + AnswerTrace opgebouwd/gepersisteerd ──

    [Fact]
    public async Task FlagAan_VerrijktPrompt_EnPersisteertAnswerTrace()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]");
        var retriever = new AllModesRetriever(
            OfficialChunk(BrainRef.Section("core", "7.3"), "showdown timing regel uit het brein"));
        var brein = new BreinRetrievalService(
            Orchestrator(retriever), new BreinRetrievalSettings(Enabled: true),
            NullLogger<BreinRetrievalService>.Instance);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        // De prompt is verrijkt met het trust-gelabelde brein-blok.
        Assert.Contains("BREIN-CONTEXT", ai.LastPrompt);
        Assert.Contains("[OFFICIEEL]", ai.LastPrompt);
        Assert.Contains("showdown timing regel uit het brein", ai.LastPrompt);
        // De bestaande citaties/antwoord blijven werken naast de verrijking.
        Assert.NotEmpty(result.Citations);
        // Er is precies één AnswerTrace weggeschreven, met dragende feiten (§6/#236).
        var trace = Assert.Single(await db.AnswerTraces.Include(t => t.Supports).ToListAsync());
        Assert.NotEmpty(trace.Supports);
        Assert.Equal(PrimaryChannel.Official.ToString(), trace.PrimaryChannel);
        Assert.Equal(Question, trace.Question);
    }

    // ── 3) Retrieval-fout ⇒ nette terugval: geen 500, geen AnswerTrace ──

    [Fact]
    public async Task FlagAan_RetrievalFout_ValtTerug_GeenException_GeenAnswerTrace()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Ja. [1]");
        var brein = new BreinRetrievalService(
            Orchestrator(new ThrowingRetriever()), new BreinRetrievalSettings(Enabled: true),
            NullLogger<BreinRetrievalService>.Instance);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein);

        // Geen exception naar buiten — /ask valt terug op de bestaande flow.
        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.DoesNotContain("BREIN-CONTEXT", ai.LastPrompt);
        Assert.Empty(await db.AnswerTraces.ToListAsync());
    }

    // ── 4) NoPath ⇒ blok kleurt de prompt zónder Supports ⇒ trace TÓCH weg ──

    [Fact]
    public async Task FlagAan_NoPath_LeegBundle_PersisteertTochAnswerTrace()
    {
        // Twee ankers (Deflect + showdown) + interactie-cue → het pad-kanaal staat aan
        // (GdsWarm zodat de RetrievalGuard het Path-kanaal niet als koud-GDS strípt),
        // maar de retriever levert niets → geen pad, geen chunks → lege bundle, lege
        // Supports. Het NoPath-oordeel ("geen bekende interactie") kleurt tóch de prompt
        // (BreinContextFormatter, hasNoPath-tak) en die beslissing mag geen onzichtbare
        // state achterlaten (#236): de guard mag niet op Supports>0 blijven hangen.
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Geen bekende interactie. [1]");
        var spy = new SpyRetriever();
        var brein = new BreinRetrievalService(
            Orchestrator(spy), new BreinRetrievalSettings(Enabled: true, GdsWarm: true),
            NullLogger<BreinRetrievalService>.Instance);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein);

        var result = await svc.AskAsync("Werkt Deflect samen met een showdown?");

        Assert.True(result.Ok);
        Assert.True(spy.Called); // de retrieval liep écht (geen flag-uit-degradatie)
        // NoPath kleurde de prompt: eerlijk "geen bekende interactie" i.p.v. een gok.
        Assert.Contains("BREIN-CONTEXT", ai.LastPrompt);
        Assert.Contains("GEEN PAD", ai.LastPrompt);
        // ...en juist die gating/NoPath-beslissing laat nu provenance achter — óók
        // zonder dragende Supports (dit is de kern van #228-review-defect 2).
        var trace = Assert.Single(await db.AnswerTraces.Include(t => t.Supports).ToListAsync());
        Assert.Empty(trace.Supports);
        Assert.Equal(PrimaryChannel.None.ToString(), trace.PrimaryChannel);
    }

    // ── 5) Flag AAN + geslaagde-maar-lege retrieval ⇒ leeg blok ⇒ GEEN trace ──

    [Fact]
    public async Task FlagAan_GeslaagdeMaarLegeRetrieval_LeegBlok_GeenAnswerTrace()
    {
        // Geslaagde retrieval die NIETS oplevert: GdsWarm laat de RetrievalGuard het
        // dure Path-kanaal koud-strippen (gds-cold), de retriever geeft lege resultaten
        // → geen chunks, geen paden, geen NoPath → lege bundle → leeg brein-blok →
        // prompt byte-identiek. Dan mag er GÉÉN (lege) AnswerTrace-rij per /ask
        // wegvloeien (#228-review-defect 4: de weiger-tak van de write-guard). Zeer
        // gangbaar vroeg in de brein-levensduur (graaf nog dun, GDS koud).
        using var db = NewDb();
        await SeedRulesAsync(db);
        var ai = CapturingAi("**Oordeel:** Ja. [1]");
        var spy = new SpyRetriever();
        var brein = new BreinRetrievalService(
            Orchestrator(spy), new BreinRetrievalSettings(Enabled: true, GdsWarm: false),
            NullLogger<BreinRetrievalService>.Instance);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, brein);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.True(spy.Called); // de retrieval liep écht, maar leverde niets op
        // Leeg blok ⇒ prompt draagt geen brein-context ⇒ geen provenance te loggen.
        Assert.DoesNotContain("BREIN-CONTEXT", ai.LastPrompt);
        Assert.Empty(await db.AnswerTraces.ToListAsync());
    }

    // ── testinfra ────────────────────────────────────────────────────────

    private static RetrievalOrchestrator Orchestrator(IGraphRetriever retriever) =>
        new(new FakeGazetteer(), new FakeSimilarity(), new FakeAdjacency(), retriever);

    private static RetrievalResult OfficialChunk(BrainRef r, string text) =>
        new([], [], [new RetrievedChunk(r, KnowledgeTier.Official, text, 0.9, TrustVector.OfficialDefault)], [], []);

    private sealed class FakeGazetteer : IGazetteerSource
    {
        public Task<Gazetteer> BuildAsync(CancellationToken ct = default) =>
            Task.FromResult(Gazetteer.Build(
            [
                new(BrainRef.Mechanic("Deflect"), "Deflect", []),
                new(BrainRef.Concept("showdown"), "Showdown", []),
            ]));
    }

    private sealed class FakeSimilarity : INodeContextSimilarity
    {
        public Task<Func<BrainRef, double>> ForQuestionAsync(string q, CancellationToken ct = default) =>
            Task.FromResult<Func<BrainRef, double>>(_ => 0.0);
    }

    private sealed class FakeAdjacency : INodeAdjacency
    {
        public Task<Func<BrainRef, BrainRef, bool>> ForCandidatesAsync(
            IReadOnlyList<BrainRef> c, CancellationToken ct = default) =>
            Task.FromResult<Func<BrainRef, BrainRef, bool>>((_, _) => true);
    }

    /// <summary>Levert de official-chunk uit ongeacht welke modus de router kiest.</summary>
    private sealed class AllModesRetriever(RetrievalResult result) : IGraphRetriever
    {
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(result);
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default) => Task.FromResult(result);
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(RetrievalResult.Empty);
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class SpyRetriever : IGraphRetriever
    {
        public bool Called;
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) { Called = true; return Task.FromResult(RetrievalResult.Empty); }
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default) { Called = true; return Task.FromResult(RetrievalResult.Empty); }
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) { Called = true; return Task.FromResult(RetrievalResult.Empty); }
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) { Called = true; return Task.FromResult(RetrievalResult.Empty); }
    }

    private sealed class ThrowingRetriever : IGraphRetriever
    {
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default) => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => throw new InvalidOperationException("Neo4j weg");
    }

    /// <summary>Zelfde FTS-vervanging als AskServiceDegradationTests: tsvector
    /// vertaalt niet naar EF InMemory; de brein-wiring gaat om dat kanaal heen.</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai, BreinRetrievalService? brein)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance, brein: brein)
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

    /// <summary>Stub-handler die de /ask-prompt onthoudt zodat de verrijking
    /// controleerbaar is; /ask/stream ongebruikt (deze tests zijn niet-streamend).</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _answer;
        public string LastPrompt = "";
        public CapturingHandler(string answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/ask" && request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("prompt", out var p))
                    LastPrompt = p.GetString() ?? "";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { answer = _answer }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturingRbAi(CapturingHandler handler) : RbAiClient(
        new HttpClient(handler) { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance)
    {
        public string LastPrompt => handler.LastPrompt;
    }

    private static CapturingRbAi CapturingAi(string answer) => new(new CapturingHandler(answer));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)))
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
