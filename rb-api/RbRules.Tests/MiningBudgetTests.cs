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

/// <summary>Regressietests voor #200: een document met méér items dan het
/// per-run-budget strandde vóór deze fix voor altijd op dezelfde plek — elke
/// her-run verbrandde het volledige budget aan het opnieuw dedupen van
/// al-opgeslagen items (Seen/Updated) in plaats van de nog onverwerkte items
/// te bereiken. De fix: alleen uitkomsten die écht nieuw werk deden tellen
/// tegen het budget (<c>processed</c>); dedupe-treffers niet. Beide
/// mining-services delen dit patroon (ClaimMiningService.RunAsync,
/// ClarificationMiningService.RunAsync) en krijgen daarom elk hun eigen
/// meerdere-runs-scenario hieronder: run 1 strandt op de cap, run 2 (en
/// eventueel 3) maken aantoonbare voortgang voorbij de vorige strandingsplek,
/// en een latere run maakt het document af.</summary>
public class ClaimMiningBudgetTests
{
    private const string SourceId = "community-gids-budget";
    private const string AndereBronId = "andere-bron-budget";
    private const string Marker0 = "SEGMENT_MARKER_0";
    private const string Marker1 = "SEGMENT_MARKER_1";

    [Fact]
    public async Task RunAsync_DocumentGroterDanDeCap_MaaktAantoonbareVoortgang_TotHetDocumentAfIs()
    {
        // Opzet: 50 claims bestaan al via "andere-bron-budget" (dus élke
        // extractie hierbeneden hit de exacte-tekst-sneltoets — stap 1 van
        // ProcessClaimAsync — en heeft nooit een embedding nodig). Community-
        // gids mint diezelfde 50 statements verspreid over 2 segmenten
        // (25 elk, ClaimMiner.MaxClaims-cap per LLM-respons). Met een budget
        // van 20 per run kost het drie runs om alle 50 te corroboreren.
        using var db = NewDb();
        var doc = await SeedBudgetDocAsync(db);
        await SeedManyExistingClaimsAsync(db, count: 50);

        var extractionCalls = 0;
        var embeddingCalls = 0;
        var ai = MarkerAi(() => SegmentClaimsJson(0, 25), () => SegmentClaimsJson(25, 25),
            () => extractionCalls++);
        // Embeddings antwoordt met een 500: als ook maar één item de
        // embedding-weg zou inslaan (d.w.z. de exacte-tekst-sneltoets zou
        // missen) zou dat direct als Failed zichtbaar worden.
        var embeddings = new EmbeddingService(new HttpClient(new StubHandler(_ =>
        {
            embeddingCalls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }))
        { BaseAddress = new Uri("http://ollama.test") });
        var svc = new ClaimMiningService(db, ai, embeddings);

        var run1 = await svc.RunAsync(maxClaims: 20);
        Assert.Equal(20, run1.Corroborated);
        Assert.Equal(0, run1.NewClaims);
        Assert.Equal(0, run1.Failed);
        Assert.True(run1.CapHit);
        Assert.Null(doc.ClaimsMinedAt);
        Assert.Equal(20, await db.ClaimSources.CountAsync(cs => cs.SourceId == SourceId));

        var run2 = await svc.RunAsync(maxClaims: 20);
        // Kern van de regressie: run 2 verwerkt de VOLGENDE 20 items — vóór
        // de fix zou de oude telling deze run laten stranden op het opnieuw
        // dedupen van de eerste 20 (al bekende) items, met 0 extra
        // corroboraties tot gevolg.
        Assert.Equal(20, run2.Corroborated);
        Assert.True(run2.CapHit);
        Assert.Null(doc.ClaimsMinedAt);
        Assert.Equal(40, await db.ClaimSources.CountAsync(cs => cs.SourceId == SourceId));

        var run3 = await svc.RunAsync(maxClaims: 20);
        Assert.Equal(10, run3.Corroborated);
        Assert.False(run3.CapHit);
        Assert.NotNull(doc.ClaimsMinedAt); // het document is nu klaar
        Assert.Equal(50, await db.ClaimSources.CountAsync(cs => cs.SourceId == SourceId));

        // Seen/Corroborated-dedupe-treffers kosten geen embedding-call (#200,
        // richting 2 is hier al langer zo voor ClaimMiningService): alle 50
        // items dedupen via de exacte-tekst-sneltoets, dus embeddings.
        // EmbedOneAsync wordt in deze hele test nooit aangeroepen.
        Assert.Equal(0, embeddingCalls);
        // Extractie zelf kost wél elke run een LLM-call per bereikt segment
        // (dat is onvermijdelijk — de dedupe-besparing zit 'm in het niet
        // meer meetellen van het per-ITEM budget, niet in het overslaan van
        // hele segmenten): 1 (run 1, cap valt midden in segment 0) + 2 + 2.
        Assert.Equal(5, extractionCalls);
    }

    [Fact]
    public async Task RunAsync_AlleItemsAlBekend_SeenTeltNietTegenBudget_DocumentWordtMeteenAfgerond()
    {
        // Geïsoleerde, minimale variant van de kern-bug: een her-scan van een
        // ongewijzigd document waarvan ALLE items al bekend zijn (Seen) mag
        // nooit op een cap stranden, hoe klein die cap ook is. Beide claims
        // bestaan al via "andere-bron" (exacte-tekst-sneltoets, geen
        // embedding nodig) — zo blijft deze test ook de EF InMemory-valkuil
        // uit de weg: twee ECHT nieuwe claims in dezelfde run zouden de
        // (alleen-Postgres) CosineDistance-vertaling raken zodra de tweede
        // het near-candidates-pad van de eerste tegenkomt (zie de grote
        // testcase hierboven, die daarom ook uitsluitend dedupe-treffers
        // gebruikt).
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        await SeedManyExistingClaimsAsync(db, count: 2);
        var svc = new ClaimMiningService(db, Ai(() => SegmentClaimsJson(0, 2)), Embeddings(ok: false));
        await svc.RunAsync(); // eerste keer: corroboreert beide (community-gids-budget erbij)
        Assert.NotNull(doc.ClaimsMinedAt);
        Assert.Equal(2, await db.Claims.CountAsync()); // geen nieuwe rijen — bestonden al

        var r = await svc.RunAsync(force: true, maxClaims: 1);

        Assert.Equal(0, r.NewClaims);
        Assert.Equal(0, r.Corroborated); // beide nu Seen (community-gids-budget al aangehecht)
        // #200: Seen kost geen budget, dus zelfs met een cap van 1 wordt het
        // hele (kleine) document in één keer afgerond.
        Assert.False(r.CapHit);
        Assert.NotNull(doc.ClaimsMinedAt);
    }

    // --- testinfra ---------------------------------------------------------

    private static string SegmentClaimsJson(int startIndex, int count) =>
        JsonSerializer.Serialize(new
        {
            claims = Enumerable.Range(startIndex, count).Select(i => new
            {
                topicType = "concept",
                topicRef = $"budget-item-{i}",
                statement = $"Budget item {i} describes a specific testable fact for issue 200.",
            }),
        });

    /// <summary>Content die exact in 2 segmenten knipt (ClaimMiningService.
    /// Segment, SegmentChars=12000): elke marker zit ruim binnen "zijn"
    /// segment, ongeacht de exacte cut op het laatste spatie-woordgrens.</summary>
    private static string TwoSegmentContent()
    {
        var filler = string.Concat(Enumerable.Repeat("riftbound filler word text. ", 500));
        var seg0Zone = Marker0 + " " + filler[..11700];
        var buffer = filler[..600];
        var seg1Zone = Marker1 + " " + filler[..2000];
        return seg0Zone + " " + buffer + " " + seg1Zone;
    }

    /// <summary>RbAiClient-stub die op basis van de marker in de prompt
    /// (welk segment wordt geëxtraheerd) het juiste kant-en-klare antwoord
    /// teruggeeft — zo kunnen beide segmenten van hetzelfde document elk hun
    /// eigen batch claims opleveren, stabiel over meerdere RunAsync-aanroepen.</summary>
    private static RbAiClient MarkerAi(
        Func<string> segment0Answer, Func<string> segment1Answer, Action onCall)
    {
        var handler = new StubHandler(req =>
        {
            onCall();
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
            var answer = prompt.Contains(Marker0) ? segment0Answer()
                : prompt.Contains(Marker1) ? segment1Answer()
                : """{"claims": []}""";
            return Json(new { answer });
        });
        return new RbAiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static async Task<Document> SeedBudgetDocAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Community-gids budget", Url = "https://example.com/budget-gids",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = TwoSegmentContent(), ContentHash = "hash-budget",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Claims die al bestaan via een ANDERE bron — elke extractie
    /// hierboven met hetzelfde (topicRef, statement)-paar dedupet dus via de
    /// exacte-tekst-sneltoets (stap 1) naar Corroborated (eerste keer voor
    /// community-gids) of Seen (tweede keer), nooit via de embedding-weg.</summary>
    private static async Task SeedManyExistingClaimsAsync(RbRulesDbContext db, int count)
    {
        db.Sources.Add(new Source
        {
            Id = AndereBronId, Name = "Andere bron (budget)", Url = "https://example.com/andere-bron-budget",
            Type = "community", TrustTier = 3, Rank = 5, Parser = "html", Cadence = "weekly",
        });
        for (var i = 0; i < count; i++)
        {
            var claim = new Claim
            {
                TopicType = "concept", TopicRef = $"budget-item-{i}",
                Statement = $"Budget item {i} describes a specific testable fact for issue 200.",
                TrustScore = 0.5, OfficialStatus = "unclear",
            };
            db.Claims.Add(claim);
            await db.SaveChangesAsync();
            db.ClaimSources.Add(new ClaimSource
            {
                ClaimId = claim.Id, SourceId = AndereBronId, Url = "https://example.com/andere-bron-budget",
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Document> SeedCommunityDocAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Community-gids budget", Url = "https://example.com/budget-gids",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "Uitleg over mulligans en scoren.", ContentHash = "hash",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

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

    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static EmbeddingService Embeddings(bool ok) => new(
        new HttpClient(new StubHandler(_ => ok
            ? Json(new { embeddings = new[] { Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray() } })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Zelfde regressie (#200) voor de clarify-pijplijn. Anders dan
/// ClaimMiningService heeft ClarificationMiningService geen cross-bron-
/// corroboratie — een dedupe-treffer (Updated) is hier altijd "dit item kwam
/// al eens langs uit DEZE bron" (Provenance is per-bron). De items hieronder
/// gebruiken bewust een onherkend topicRef/geen citaat (blijven dus altijd
/// "pending"/unverified) — de hybride grounded/anchored-poort is hier niet
/// het punt, alleen de budget-/kosten-boekhouding.</summary>
public class ClarificationMiningBudgetTests
{
    private const string SourceId = "budget-faq-source";
    private const string SourceUrl = "https://example.com/budget-faq";
    private const string Marker0 = "SEGMENT_MARKER_0";
    private const string Marker1 = "SEGMENT_MARKER_1";

    [Fact]
    public async Task RunAsync_DocumentGroterDanDeCap_MaaktAantoonbareVoortgang_EnBespaartEmbeddingCalls()
    {
        // 2 segmenten × 25 concepten (ClarificationMiner.MaxItems-cap per
        // LLM-respons) = 50 items, allemaal ongegrond/onanchored dus altijd
        // "pending" — bewust: dit test de budget-/kostenboekhouding, niet de
        // hybride poort. Met een run-budget van 20 kost het drie runs om
        // alle 50 concepten op te slaan.
        using var db = NewDb();
        var doc = await SeedBudgetFaqDocAsync(db);

        var extractionCalls = 0;
        var embeddingCalls = 0;
        var ai = MarkerAi(() => SegmentClarificationsJson(0, 25), () => SegmentClarificationsJson(25, 25),
            () => extractionCalls++);
        var embeddings = new EmbeddingService(new HttpClient(new StubHandler(req =>
        {
            embeddingCalls++;
            return Json(new { embeddings = new[] { Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray() } });
        }))
        { BaseAddress = new Uri("http://ollama.test") });
        var svc = new ClarificationMiningService(db, ai, embeddings);

        var run1 = await svc.RunAsync(maxItems: 20);
        Assert.Equal(20, run1.Pending);
        Assert.Equal(0, run1.Verified);
        Assert.Equal(0, run1.Updated);
        Assert.True(run1.CapHit);
        Assert.Null(doc.ClarifiedAt);
        Assert.Equal(20, await db.Corrections.CountAsync());
        Assert.Equal(20, embeddingCalls); // 20 gloednieuwe items ⇒ 20 embeddings

        var run2 = await svc.RunAsync(maxItems: 20);
        // Kern van de regressie: run 2 werkt de eerste 20 bij (Updated, geen
        // budget, geen embedding-call — #200 richting 2) en bereikt daardoor
        // de VOLGENDE 20, in plaats van te stranden op het herhalen van de
        // eerste 20.
        Assert.Equal(20, run2.Updated);
        Assert.Equal(20, run2.Pending);
        Assert.True(run2.CapHit);
        Assert.Null(doc.ClarifiedAt);
        Assert.Equal(40, await db.Corrections.CountAsync());
        // Geen embedding-call voor de 20 Updated-items: alleen de 20 nieuwe.
        Assert.Equal(40, embeddingCalls);

        var run3 = await svc.RunAsync(maxItems: 20);
        Assert.Equal(40, run3.Updated); // alle 40 al bekende items — geen budget
        Assert.Equal(10, run3.Pending); // de laatste 10, echt nieuw
        Assert.False(run3.CapHit);
        Assert.NotNull(doc.ClarifiedAt); // het document is nu klaar
        Assert.Equal(50, await db.Corrections.CountAsync());
        // #200 richting 2: de 40 Updated-treffers in run 3 kostten geen
        // embedding-call — alleen de 10 écht nieuwe concepten deden dat.
        Assert.Equal(50, embeddingCalls);

        Assert.Equal(5, extractionCalls); // 1 (run 1, cap in segment 0) + 2 + 2
    }

    // --- testinfra ---------------------------------------------------------

    private static string SegmentClarificationsJson(int startIndex, int count) =>
        JsonSerializer.Serialize(new
        {
            clarifications = Enumerable.Range(startIndex, count).Select(i => new
            {
                topicType = "concept",
                topicRef = $"budget-concept-{i}",
                clarification = $"Budget concept {i} clarifies a specific testable interaction for issue 200.",
            }),
        });

    private static string TwoSegmentContent()
    {
        var filler = string.Concat(Enumerable.Repeat("riftbound filler word text. ", 500));
        var seg0Zone = Marker0 + " " + filler[..11700];
        var buffer = filler[..600];
        var seg1Zone = Marker1 + " " + filler[..2000];
        return seg0Zone + " " + buffer + " " + seg1Zone;
    }

    private static RbAiClient MarkerAi(
        Func<string> segment0Answer, Func<string> segment1Answer, Action onCall)
    {
        var handler = new StubHandler(req =>
        {
            onCall();
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
            var answer = prompt.Contains(Marker0) ? segment0Answer()
                : prompt.Contains(Marker1) ? segment1Answer()
                : """{"clarifications": []}""";
            return Json(new { answer });
        });
        return new RbAiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static async Task<Document> SeedBudgetFaqDocAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Budget FAQ", Url = SourceUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.Faq, ContentKindSource = SourceContentKind.LlmOrigin,
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = TwoSegmentContent(), ContentHash = "hash-budget-faq",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

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

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}
