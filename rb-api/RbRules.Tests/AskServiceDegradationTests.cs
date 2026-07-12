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

/// <summary>Regressietests voor #100: tijdens het Ollama-model-incident gaf
/// /api/ask een kale 500 zodra de embed-batch faalde, terwijl
/// RuleSearchService in dezelfde situatie netjes naar alleen-FTS degradeert.
/// Hier: EmbeddingService is de échte client op een gestubde Ollama die 500
/// geeft (patroon ClaimMiningServiceTests, EF InMemory) — het antwoordpad
/// moet blijven werken met FTS-citaties, zonder exception naar buiten. Het
/// FTS-kanaal zelf is in de test vervangen (tsvector vertaalt alleen naar
/// Postgres); de degradatie draait juist om de pipeline eromheen.
/// In dezelfde xUnit-collectie als AskServiceAgenticTests (#107): die tests
/// zetten de proces-brede ASK_AGENTIC-env en mogen dus niet parallel aan
/// andere AskService-tests draaien.</summary>
[Collection("ask-service-env")]
public class AskServiceDegradationTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";

    [Fact]
    public async Task AskAsync_EmbeddingUitval_DegradeertNaarFts_MetCitaties()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, answer: "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]");

        // Vóór de fix ontsnapte hier de HttpRequestException van de
        // embed-batch → onafgevangen 500 richting de gebruiker.
        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.Equal("**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]", result.Answer);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("101", citation.Section);
        Assert.Contains("Deflect", citation.Text);
        // Vector-gedreven kanalen zijn overgeslagen, niet gecrasht.
        Assert.NotNull(result.Claims);
        Assert.Empty(result.Claims!);

        // Kanttekening voor de beheerder: de trace meldt de degradatie …
        var trace = await db.AskTraces.SingleAsync();
        Assert.True(trace.Ok);
        Assert.Contains("embedding-uitval", trace.Sections);
        Assert.Contains("§101", trace.Sections);
        // … en de rulings-laag viel terug op recentste i.p.v. vector-match.
        Assert.Equal(1, trace.VerifiedRulings);
    }

    [Fact]
    public async Task AskStreamingAsync_EmbeddingUitval_LevertMetaEnDeltas_ZonderException()
    {
        // Het streamingpad (#31) deelt AskCoreAsync: ook daar moet de
        // degradatie werken — meta-frame met FTS-citaties vóór het antwoord.
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, answer: "Ja, dat mag.");

        AskStreamMeta? meta = null;
        var deltas = new StringBuilder();
        var result = await svc.AskStreamingAsync(
            Question, images: null, history: null,
            onMeta: m => { meta = m; return Task.CompletedTask; },
            onDelta: d => { deltas.Append(d); return Task.CompletedTask; });

        Assert.True(result.Ok);
        Assert.Equal("Ja, dat mag.", result.Answer);
        Assert.Equal("Ja, dat mag.", deltas.ToString());
        Assert.NotNull(meta);
        var citation = Assert.Single(meta!.Citations);
        Assert.Equal("101", citation.Section);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("embedding-uitval", trace.Sections);
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>AskService met alléén het FTS-kanaal vervangen door een
    /// simpele woord-match: tsvector-functies vertalen niet naar EF InMemory,
    /// en de degradatie (#100) gaat juist over de pipeline om dat kanaal heen.</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : AskService(db, embeddings, ai, new RequestUserContext(),
            NullLogger<AskService>.Instance)
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

    private static TestableAskService Svc(RbRulesDbContext db, string answer) =>
        new(db, FailingEmbeddings(), Ai(answer));

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

    /// <summary>Echte RbAiClient op een gestubde handler: /ask (rewrite én
    /// niet-streamend antwoord) geeft het antwoord als JSON; /ask/stream geeft
    /// het als NDJSON-frames (delta + done). Het antwoord bevat bewust geen
    /// accolades, zodat de rewrite-parse null oplevert (rauwe-vraag-pad).</summary>
    private static RbAiClient Ai(string answer) => new(
        new HttpClient(new StubHandler(req => req.RequestUri!.AbsolutePath == "/ask/stream"
            ? Ndjson(
                JsonSerializer.Serialize(new { type = "delta", text = answer }),
                JsonSerializer.Serialize(new { type = "done", answer }))
            : Json(new { answer })))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Echte EmbeddingService op een gestubde Ollama die altijd 500
    /// geeft — de productie-faalvorm van het model-incident (#100).</summary>
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
        db.RuleChunks.AddRange(
            new RuleChunk
            {
                DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101",
                ChunkIndex = 0, Page = 12,
                Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
            },
            new RuleChunk
            {
                DocumentId = doc.Id, SourceId = SourceId, SectionCode = "205",
                ChunkIndex = 1, Page = 20,
                Text = "Players draw two cards at the start of the game.",
            });
        // Geverifieerde ruling zónder embedding: de rulings-laag moet bij
        // embedding-uitval op recentste terugvallen i.p.v. crashen.
        db.Corrections.Add(new Correction
        {
            Scope = "answer", Ref = "up",
            Text = "Deflect werkt ook tijdens een showdown.",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
