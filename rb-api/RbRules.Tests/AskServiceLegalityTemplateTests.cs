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

/// <summary>Het legaliteitssjabloon door de echte /ask-flow (#383): een zuivere
/// banvraag met een herkende kaart krijgt een deterministisch antwoord uit de
/// banlijst — de rewrite-call blijft, de antwoord-call verdwijnt.</summary>
public class AskServiceLegalityTemplateTests
{
    private const string SourceId = "riot-core-rules";

    [Fact]
    public async Task Banvraag_WordtZonderAntwoordCallBeantwoord()
    {
        using var db = NewDb();
        await SeedAsync(db, viktorBanned: true);
        var calls = 0;
        var ai = CountingAi(() => calls++, new
        {
            // Alleen de rewrite (light) hoort dit te zien.
            answer = """{"normalized":"is Viktor banned","queries":[],"terms":[]}""",
            usage = new { inputTokens = 50, outputTokens = 5 },
        });
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext());

        var r = await svc.AskAsync("Is Viktor banned in constructed?");

        Assert.True(r.Ok);
        Assert.Equal("Legaliteit", r.QuestionType);
        Assert.StartsWith("**Oordeel:** Viktor is niet toegestaan: de kaart staat op de banlijst.", r.Answer);
        Assert.Contains("**Zekerheid:** Bevestigd", r.Answer);
        // Precies één citatie: de officiële banlijstbron.
        var cit = Assert.Single(r.Citations);
        Assert.Equal("https://playriftbound.com/hub", cit.Url);
        Assert.Equal(1, cit.Trust);
        // Eén rb-ai-call: de rewrite. Géén antwoord-call.
        Assert.Equal(1, calls);

        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal("template", metric.Model);
        Assert.True(metric.Ok);
        // Kostengrootboek: alleen de rewrite-rij; geen antwoord-rij tegen een LLM-tarief.
        var evt = Assert.Single(await db.AiUsageEvents.ToListAsync());
        Assert.Equal("ask-rewrite", evt.Kind);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal("template", trace.Model);
        Assert.Contains("sjabloon", trace.BrainSteps);
    }

    [Fact]
    public async Task NietGebandeKaart_KrijgtToegestaanMetHubAlsBron()
    {
        using var db = NewDb();
        await SeedAsync(db, viktorBanned: false);
        var calls = 0;
        var ai = CountingAi(() => calls++, new { answer = """{"normalized":"Viktor deck legal","queries":[],"terms":[]}""" });
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext());

        // Let op: "mag ik Viktor spelen?" routeert de QuestionRouter NIET als
        // Legaliteit (geen van zijn sleutelwoorden) — dat is een router-kwestie,
        // los van dit sjabloon. "toegestaan" routeert wel.
        var r = await svc.AskAsync("is Viktor toegestaan?");

        Assert.StartsWith("**Oordeel:** Viktor is toegestaan.", r.Answer);
        Assert.Contains("staat niet op de actuele banlijst [1]", r.Answer);
        Assert.Contains("legaal sinds 1 oktober 2025", r.Answer);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Deckbouwvraag_GaatGewoonNaarHetLlm()
    {
        // "hoeveel kopieën" vraagt de Core Rules, niet de banlijst — het sjabloon
        // moet zich hier terugtrekken en het bestaande pad ongemoeid laten.
        using var db = NewDb();
        await SeedAsync(db, viktorBanned: false);
        var calls = 0;
        var ai = CountingAi(() => calls++,
            new { answer = """{"normalized":"Viktor deck legal","queries":[],"terms":[]}""" },
            new { answer = "**Oordeel:** Maximaal drie exemplaren. [1]" });
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext());

        var r = await svc.AskAsync("mag ik 4x Viktor in mijn deck spelen?");

        Assert.Equal("Legaliteit", r.QuestionType);
        Assert.StartsWith("**Oordeel:** Maximaal drie exemplaren.", r.Answer);
        Assert.Equal(2, calls);
        Assert.Equal("cheap", (await db.AskMetrics.SingleAsync()).Model);
    }

    [Fact]
    public async Task ZonderOfficieleBron_GeenSjabloon()
    {
        // Geen Rules Hub-bron en geen ban-rij ⇒ niets om te citeren ⇒ LLM-pad.
        using var db = NewDb();
        await SeedAsync(db, viktorBanned: false, withHub: false);
        var calls = 0;
        var ai = CountingAi(() => calls++,
            new { answer = """{"normalized":"Viktor deck legal","queries":[],"terms":[]}""" },
            new { answer = "**Oordeel:** Toegestaan. [1]" });
        var svc = new TestableAskService(db, FailingEmbeddings(), ai, new RequestUserContext());

        // Het LLM-pad komt alleen tot een antwoord-call als de retrieval iets
        // vindt; zonder hit neemt de lege-retrieval-uitgang het over (1 call).
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = db.Documents.Single().Id, SourceId = SourceId, SectionCode = "102",
            ChunkIndex = 1, Page = 13, Text = "Viktor is legal in Constructed unless banned.",
        });
        await db.SaveChangesAsync();

        await svc.AskAsync("is Viktor legaal?");

        Assert.Equal(2, calls);
    }

    // ── seeding ─────────────────────────────────────────────────────────────

    private static async Task SeedAsync(RbRulesDbContext db, bool viktorBanned, bool withHub = true)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        if (withHub)
            db.Sources.Add(new Source
            {
                Id = SourceSeed.RulesHubId, Name = "Rules Hub", Url = "https://playriftbound.com/hub",
                Type = "official", TrustTier = 1, Rank = 2, Parser = "html", Cadence = "weekly",
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
            Text = "A deck may contain at most three copies of a card with the same name.",
        });
        db.CardSets.Add(new CardSet { SetId = "OGN", Name = "Origins", PublishedOn = new DateOnly(2025, 10, 1) });
        db.Cards.Add(new Card { RiftboundId = "ogn-001", Name = "Viktor", SetId = "OGN", SetLabel = "Origins" });
        if (viktorBanned)
            db.BanEntries.Add(new BanEntry
            {
                Name = "Viktor", CardRiftboundId = "ogn-001", Kind = "card", Format = "constructed",
                EffectiveFrom = new DateOnly(2026, 7, 16), SourceUrl = "https://playriftbound.com/hub",
            });
        await db.SaveChangesAsync();
    }

    // ── helpers (zelfde patroon als AiUsageMeteringTests) ────────────────────

    /// <summary>InMemory kent geen Postgres-full-text; de FTS-stap wordt hier
    /// een eenvoudige woordmatch, zoals in de andere AskService-tests.</summary>
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

    private static RbAiClient CountingAi(Action onCall, params object[] payloads)
    {
        var call = 0;
        return new RbAiClient(
            new HttpClient(new StubHandler(_ =>
            {
                onCall();
                return JsonMessage(payloads[Math.Min(call++, payloads.Length - 1)]);
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
