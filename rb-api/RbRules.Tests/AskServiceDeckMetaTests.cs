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

/// <summary>Regressietests voor het deck-meta-kanaal in /ask (kennislaag 3,
/// #267). Twee gedragingen zijn hard: (a) het deck-signaal komt uitsluitend
/// als expliciet gelabeld laag-3-blok in de prompt — ongelabelde deck-cijfers
/// zouden als officiële kennis meeliften; (b) /ask is de hotpath, dus een
/// niet-relevante vraag (regelvraag zonder kaarten, of een normatieve vraag
/// zoals legaliteit) mag het kanaal — en dus de deck-query's — nooit raken.
/// De invocatie wordt gemeten via de kanaal-seam (zelfde patroon als de
/// misvattingen-seam): de override telt en delegeert naar de échte
/// productie-implementatie, dus de deck-query's zelf draaien gewoon mee (EF
/// InMemory vertaalt ze, zie CardDetailServiceDeckPopularityTests). Zelfde
/// testopzet als AskServiceMisconceptionTests; in dezelfde xUnit-collectie
/// als de agentic-tests omdat die de proces-brede ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServiceDeckMetaTests
{
    private const string SourceId = "riot-core-rules";
    private const string CardId = "ogn-011-298";
    private const string PartnerId = "ogn-022-298";

    [Fact]
    public async Task AskAsync_Kaartvraag_DeckMetaAlsGelabeldLaag3Blok()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        SeedDecks(db);
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Antwoord:** Test Kaart valt aan. [1]");
        var svc = Svc(db, ai);

        // "Wat doet …" + herkende kaartnaam ⇒ router-type Kaart ⇒ poort open.
        var result = await svc.AskAsync("Wat doet Test Kaart in een deck?");

        Assert.True(result.Ok);
        Assert.Equal(1, svc.DeckMetaCalls);

        var prompt = ai.AnswerPrompt();
        // Expliciete laag-3-labeling (docs/KNOWLEDGE.md): het blok draagt de
        // piramide-laag én de "zwakste laag"-framing — deck-cijfers zonder
        // dit label zouden als officiële kennis meeliften.
        Assert.Contains("DECK-META (kennislaag 3", prompt);
        Assert.Contains("géén officiële regel, ruling of kaarttekst", prompt);
        // De echte cijfers uit de gezeede bank: 2 van de 5 recente decks
        // (dun sample: 5 < drempel), gemiddeld (2+1)/2 exemplaren, en de
        // top-co-occurrence met naam + aantal decks.
        Assert.Contains(
            "- [deck-meta] Test Kaart: gespeeld in 2 van de 5 recentste decks", prompt);
        Assert.Contains("gemiddeld 1.5 exemplaren", prompt);
        Assert.Contains("vaak samen met: Partner Kaart (2 decks)", prompt);
        // Piramide-volgorde: officiële kaartgegevens vóór het meta-blok.
        Assert.True(
            prompt.IndexOf("Kaartgegevens", StringComparison.Ordinal)
                < prompt.IndexOf("DECK-META", StringComparison.Ordinal),
            "deck-meta hoort ná de officiële kaartgegevens in de prompt");

        // Trace: kennislagen-regel met het "deckmeta:"-prefix in het
        // bestaande CommunityClaims-veld (bewust geen migratie).
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("deckmeta:card:Test Kaart", trace.CommunityClaims);
    }

    [Fact]
    public async Task AskAsync_LegendVraag_SubstringMatchVerbruiktGeenTweedeSlot()
    {
        // #318-review B1: vrijwel elke legend "X, Epithet" heeft een
        // champion-unit "X" naast zich. Een vraag over uitsluitend de legend
        // raakt via de substring-match óók de basiskaart — die mag geen eigen
        // deck-meta-regel, trace-ref of deck-query's opleveren (dedup op
        // langste naam, zoals AgenticGate.CountDistinctMentions).
        using var db = NewDb();
        await SeedRulesAsync(db);
        const string legendId = "ogn-100-298";
        const string baseId = "ogn-101-298";
        db.Cards.AddRange(
            new Card { RiftboundId = legendId, Name = "Jinx, Loose Cannon", SetId = "OGN" },
            new Card { RiftboundId = baseId, Name = "Jinx", SetId = "OGN" });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = db.Documents.Single().Id, SourceId = SourceId,
            SectionCode = "302.1", ChunkIndex = 1, Page = 4,
            Text = "A legend such as Jinx, Loose Cannon may attack during a showdown.",
        });
        await db.SaveChangesAsync();
        // Beide kaarten hebben een eigen deck-signaal: zonder dedup zou de
        // basiskaart dus wél een tweede regel opleveren (mutatie-bewijs).
        var decks = Enumerable.Range(0, 5).Select(i => new Deck
        {
            PaId = $"jinx-{i}",
            SourceUrl = $"https://piltoverarchive.com/decks/view/jinx-{i}",
            PaUpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
        }).ToList();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = decks[0].Id, Section = "champions", CardCode = "OGN-100", CanonicalRiftboundId = legendId, Quantity = 1 },
            new DeckCard { DeckId = decks[1].Id, Section = "champions", CardCode = "OGN-100", CanonicalRiftboundId = legendId, Quantity = 1 },
            new DeckCard { DeckId = decks[2].Id, Section = "maindeck", CardCode = "OGN-101", CanonicalRiftboundId = baseId, Quantity = 3 },
            new DeckCard { DeckId = decks[3].Id, Section = "maindeck", CardCode = "OGN-101", CanonicalRiftboundId = baseId, Quantity = 3 });
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Antwoord:** De legend valt aan. [1]");
        var svc = Svc(db, ai);

        var result = await svc.AskAsync("Wat doet Jinx, Loose Cannon in een deck?");

        Assert.True(result.Ok);
        Assert.Equal(1, svc.DeckMetaCalls);
        var prompt = ai.AnswerPrompt();
        // Precies één deck-meta-regel: de legend zelf.
        Assert.Contains(
            "- [deck-meta] Jinx, Loose Cannon: gespeeld in 2 van de 5 recentste decks",
            prompt);
        Assert.DoesNotContain("- [deck-meta] Jinx:", prompt);
        Assert.Equal(
            prompt.IndexOf("[deck-meta]", StringComparison.Ordinal),
            prompt.LastIndexOf("[deck-meta]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskAsync_RegelvraagZonderKaart_GeenEnkeleDeckQuery()
    {
        // De hotpath-eis van #267: een regelvraag zonder kaartnamen mag het
        // deck-meta-kanaal nooit raken — ook niet "voor niets".
        using var db = NewDb();
        await SeedRulesAsync(db);
        SeedDecks(db);
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Oordeel:** Ja. [1]");
        var svc = Svc(db, ai);

        var result = await svc.AskAsync("Hoe werkt een showdown precies?");

        Assert.True(result.Ok);
        Assert.Equal(0, svc.DeckMetaCalls);
        Assert.DoesNotContain("DECK-META", ai.AnswerPrompt());
    }

    [Fact]
    public async Task AskAsync_LegaliteitsvraagMetKaart_GeenDeckQuery()
    {
        // Ook mét herkende kaart blijft een normatieve vraag buiten het
        // meta-kanaal: de banlijst is daar gezaghebbend, community-meta zou
        // het oordeel vervuilen (poort op vraagtype én kaart, niet alleen kaart).
        using var db = NewDb();
        await SeedRulesAsync(db);
        SeedDecks(db);
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Oordeel:** Niet banned. [1]");
        var svc = Svc(db, ai);

        var result = await svc.AskAsync("Is Test Kaart banned?");

        Assert.True(result.Ok);
        Assert.Equal(0, svc.DeckMetaCalls);
        Assert.DoesNotContain("DECK-META", ai.AnswerPrompt());
    }

    [Fact]
    public async Task AskAsync_KaartvraagZonderDeckSignaal_GeenLeegBlok()
    {
        // Lege bank: het kanaal draait (poort open), maar zonder signaal komt
        // er géén (leeg) gelabeld blok — een kop zonder inhoud is ruis.
        using var db = NewDb();
        await SeedRulesAsync(db);
        // bewust géén decks
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Antwoord:** Test Kaart valt aan. [1]");
        var svc = Svc(db, ai);

        var result = await svc.AskAsync("Wat doet Test Kaart in een deck?");

        Assert.True(result.Ok);
        Assert.Equal(1, svc.DeckMetaCalls);
        Assert.DoesNotContain("DECK-META", ai.AnswerPrompt());
        var trace = await db.AskTraces.SingleAsync();
        Assert.DoesNotContain("deckmeta:", trace.CommunityClaims);
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>FTS-seam (zelfde reden als AskServiceDegradationTests: de
    /// tsvector-functies vertalen niet naar EF InMemory). De deck-meta-seam
    /// hieronder telt alleen en delegeert naar de productie-implementatie —
    /// de deck-query's zelf zijn InMemory-vertaalbaar en draaien dus echt.</summary>
    private class ProductionPathAskService(
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

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : ProductionPathAskService(db, embeddings, ai)
    {
        /// <summary>Aantal keer dat het deck-meta-kanaal daadwerkelijk is
        /// aangeroepen — de gedragsmeting waarmee de hotpath-poort getest
        /// wordt (0 = geen enkele deck-query afgevuurd).</summary>
        public int DeckMetaCalls { get; private set; }

        protected override Task<(string Block, List<string> Refs)> DeckMetaChannelAsync(
            RbRulesDbContext ctx, string qLower, CancellationToken ct)
        {
            DeckMetaCalls++;
            return base.DeckMetaChannelAsync(ctx, qLower, ct);
        }
    }

    private static TestableAskService Svc(RbRulesDbContext db, RecordingAi ai) =>
        new(db, FailingEmbeddings(), ai.Client);

    /// <summary>Echte RbAiClient op een handler die alle request-bodies
    /// vastlegt, zodat de test de daadwerkelijk verstuurde prompt kan
    /// inspecteren. Antwoord bevat bewust geen accolades: de rewrite-parse
    /// levert dan null (rauwe-vraag-pad).</summary>
    private sealed class RecordingAi(string answer)
    {
        private readonly List<string> _bodies = [];

        public RbAiClient Client => new(
            new HttpClient(new Handler(this, answer)) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        public string AnswerPrompt()
        {
            foreach (var body in _bodies)
            {
                using var doc = JsonDocument.Parse(body);
                var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
                if (prompt.Contains("Context-fragmenten:")) return prompt;
            }
            throw new InvalidOperationException("geen antwoord-call gezien");
        }

        private sealed class Handler(RecordingAi owner, string answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Content is not null)
                    owner._bodies.Add(await request.Content.ReadAsStringAsync(ct));
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { answer }),
                        Encoding.UTF8, "application/json"),
                };
            }
        }
    }

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => throw exception();
    }

    /// <summary>Failing Ollama (#100-patroon): qv blijft null — de
    /// vector-kanalen vervallen, precies zoals in de degradatietests. Het
    /// deck-meta-kanaal is naam-gedreven en heeft géén vector nodig.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new ThrowingHandler(() => new HttpRequestException("ollama plat")))
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

    /// <summary>Minimale regelindex + de twee kaarten. De chunk-tekst raakt
    /// zowel "test" als "showdown", zodat de FTS-seam in álle testvragen een
    /// treffer heeft en de retrieval nooit op een lege index eindigt.</summary>
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
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "301.1",
            ChunkIndex = 0, Page = 3,
            Text = "During a showdown a unit such as Test Kaart may attack the battlefield.",
        });
        db.Cards.AddRange(
            new Card { RiftboundId = CardId, Name = "Test Kaart", SetId = "OGN" },
            new Card { RiftboundId = PartnerId, Name = "Partner Kaart", SetId = "OGN" });
        await db.SaveChangesAsync();
    }

    /// <summary>Vijf recente decks; Test Kaart in deck 0 (2 exemplaren) en
    /// deck 1 (1 exemplaar) — gemiddeld 1.5 — met Partner Kaart in dezelfde
    /// twee decks als top-co-occurrence.</summary>
    private static void SeedDecks(RbRulesDbContext db)
    {
        var decks = Enumerable.Range(0, 5).Select(i => new Deck
        {
            PaId = $"deck-{i}",
            SourceUrl = $"https://piltoverarchive.com/decks/view/deck-{i}",
            PaUpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
        }).ToList();
        db.Decks.AddRange(decks);
        db.SaveChanges();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 2 },
            new DeckCard { DeckId = decks[1].Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 },
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "OGN-022", CanonicalRiftboundId = PartnerId, Quantity = 3 },
            new DeckCard { DeckId = decks[1].Id, Section = "maindeck", CardCode = "OGN-022", CanonicalRiftboundId = PartnerId, Quantity = 3 });
    }
}
