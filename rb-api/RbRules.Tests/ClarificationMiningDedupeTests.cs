using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Concept-niveau-dedupe van de FAQ-mining (#177, HIGH-review-fix).
/// De eerdere exacte-tekst-toets stapelde dubbele geverifieerde rulings op
/// zodra een her-mine (na een gedeeltelijk mislukte/gecapte run, of na een
/// cosmetische bronwijziging met een nieuwe Document-rij) de LLM een
/// verduidelijking nét anders liet verwoorden. Deze tests dekken precies dat
/// gat: de LLM-stub geeft een PARAFRASE terug (andere woorden, zelfde
/// onderwerp), en de embedding-stub is een deterministische bag-of-words zodat
/// een parafrase dicht bij het origineel ligt en een écht ander concept ver —
/// het gat dat de bestaande tests (identieke-string-stub) niet konden raken.</summary>
public class ClarificationMiningDedupeTests
{
    private const string SourceId = "playriftbound-com-unleashed-rules-faq-and-clarifications";
    private const string SourceUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    // Origineel en parafrase delen bijna alle woorden (alleen het laatste
    // werkwoord verschilt) ⇒ lage cosine-afstand ⇒ dezelfde verduidelijking.
    private static string Original(string clar) =>
        $$"""{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "{{clar}}"}]}""";

    private const string LegionA = "Legion betekent dat je een item op de chain finalizet";
    private const string LegionParaphrase = "Legion betekent dat je een item op de chain afrondt";
    // Heel andere verduidelijking over hetzelfde onderwerp (Legion) ⇒ deelt
    // vrijwel geen woorden ⇒ hoge cosine-afstand ⇒ apart concept.
    private const string LegionDifferent =
        "Legion units kunnen niet worden gekozen als doel door removal effecten";

    [Fact]
    public async Task ReRun_ParaphraseOfSameConcept_UpdatesExisting_NoNewRow()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var answer = Original(LegionA);
        var svc = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());

        var first = await svc.RunAsync();
        Assert.Equal(1, first.NewItems);

        // Tweede run (force, want doc is al ClarifiedAt): de LLM herformuleert
        // exact hetzelfde concept — geen nieuwe rij, de bestaande wordt bijgewerkt.
        answer = Original(LegionParaphrase);
        var second = await svc.RunAsync(force: true);

        Assert.Equal(0, second.NewItems);
        var ruling = Assert.Single(await db.Corrections.ToListAsync());
        Assert.Contains("afrondt", ruling.Text); // bijgewerkt naar de nieuwste formulering
        Assert.DoesNotContain("finalizet", ruling.Text);
        Assert.Equal("verified", ruling.Status);
        Assert.NotNull(ruling.Embedding);
    }

    [Fact]
    public async Task ReMine_NewDocumentSameConcept_NoDuplicate()
    {
        // Bevinding (b): een cosmetische bronwijziging maakt een nieuwe
        // Document-rij (ClarifiedAt=null) ⇒ her-mine van het HELE document.
        using var db = NewDb();
        var doc1 = await SeedFaqDocAsync(db, retrievedAt: DateTimeOffset.UtcNow.AddDays(-1));
        var answer = Original(LegionA);
        var svc = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());

        var first = await svc.RunAsync();
        Assert.Equal(1, first.NewItems);
        Assert.NotNull(doc1.ClarifiedAt);

        // Nieuwe Document-versie van dezelfde bron (nieuwste RetrievedAt,
        // ClarifiedAt=null) — de service pakt de laatste, dus geen force nodig.
        db.Documents.Add(new Document
        {
            SourceId = SourceId, Content = "Herziene tekst met dezelfde Legion-uitleg.",
            ContentHash = "hash2", RetrievedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        answer = Original(LegionParaphrase);

        var second = await svc.RunAsync();

        Assert.Equal(1, second.Documents);
        Assert.Equal(0, second.NewItems);
        Assert.Single(await db.Corrections.ToListAsync()); // geen duplicaat
    }

    [Fact]
    public async Task ReRun_DifferentConcept_DifferentRef_CreatesNewRow()
    {
        // Geen over-dedup: een verduidelijking over een ánder onderwerp krijgt
        // wél een eigen rij.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var answer = Original(LegionA);
        var svc = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());

        await svc.RunAsync();
        answer = """{"clarifications": [{"topicType": "concept", "topicRef": "Reflection tokens", "clarification": "Reflection tokens tellen niet mee voor het handlimiet."}]}""";
        var second = await svc.RunAsync(force: true);

        Assert.Equal(1, second.NewItems);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    [Fact]
    public async Task ReRun_DifferentConcept_SameRef_FarEmbedding_CreatesNewRow()
    {
        // Discriminatie op embedding-niveau: twee inhoudelijk verschillende
        // verduidelijkingen over hetzelfde onderwerp (Ref "Legion") die ver uit
        // elkaar liggen mogen NIET samengevouwen worden.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var answer = Original(LegionA);
        var svc = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());

        await svc.RunAsync();
        answer = Original(LegionDifferent);
        var second = await svc.RunAsync(force: true);

        Assert.Equal(1, second.NewItems);
        var rulings = await db.Corrections.Where(c => c.Ref == "Legion").ToListAsync();
        Assert.Equal(2, rulings.Count); // twee losse Legion-verduidelijkingen
    }

    [Fact]
    public async Task ReRun_SameClarification_DifferentQuote_QuoteNotInDedupeKey()
    {
        // Het citaat telt niet mee in de dedupe-sleutel: zelfde verduidelijking
        // met een ander citaat werkt de bestaande rij bij, geen duplicaat.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var answer = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen op de chain.", "quote": "you finalize on the chain"}]}""";
        var svc = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());

        await svc.RunAsync();
        answer = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen op de chain.", "quote": "een compleet ander citaat uit de bron"}]}""";
        var second = await svc.RunAsync(force: true);

        Assert.Equal(0, second.NewItems);
        var ruling = Assert.Single(await db.Corrections.ToListAsync());
        Assert.Contains("een compleet ander citaat", ruling.Text); // citaat bijgewerkt
    }

    [Fact]
    public async Task ReRun_EmbeddingDown_NormalizedMatch_NoDuplicate_ExistingEmbeddingKept()
    {
        // Degradatie: valt Ollama weg bij een her-run, dan voorkomt de
        // genormaliseerde exacte-tekst-toets alsnog een duplicaat — en de
        // bestaande (goede) embedding blijft staan (nooit overschreven met null).
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var answer = Original(LegionA);
        var svc1 = new ClarificationMiningService(db, Ai(() => answer), BagOfWordsEmbeddings());
        await svc1.RunAsync();
        var before = (await db.Corrections.SingleAsync()).Embedding;
        Assert.NotNull(before);

        // Zelfde concept (alleen whitespace/case-variant), maar nu ligt Ollama plat.
        answer = Original("legion  betekent dat je een item op de chain finalizet");
        var svc2 = new ClarificationMiningService(db, Ai(() => answer), DownEmbeddings());
        var second = await svc2.RunAsync(force: true);

        Assert.Equal(0, second.NewItems);
        Assert.Equal(0, second.Failed);
        var ruling = Assert.Single(await db.Corrections.ToListAsync());
        Assert.NotNull(ruling.Embedding); // niet overschreven met null
    }

    // --- testinfra -------------------------------------------------------

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

    /// <summary>Deterministische bag-of-words-embedding: identieke tekst geeft
    /// een identieke vector (afstand 0), teksten die veel woorden delen liggen
    /// dicht bij elkaar en onverwante teksten ver — genoeg om de semantische
    /// dedupe-poort echt te testen (parafrase dichtbij, ander concept ver).</summary>
    private static EmbeddingService BagOfWordsEmbeddings() => new(
        new HttpClient(new StubHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var vectors = doc.RootElement.GetProperty("input").EnumerateArray()
                .Select(e => BagOfWords(e.GetString() ?? ""))
                .ToArray();
            return Json(new { embeddings = vectors });
        }))
        { BaseAddress = new Uri("http://ollama.test") });

    private static EmbeddingService DownEmbeddings() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static float[] BagOfWords(string text)
    {
        var v = new float[EmbeddingConfig.Dimensions];
        foreach (var tok in Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+"))
        {
            if (tok.Length == 0) continue;
            var h = 2166136261u; // FNV-1a
            foreach (var ch in tok) h = (h ^ ch) * 16777619u;
            v[h % (uint)EmbeddingConfig.Dimensions] += 1f;
        }
        return v;
    }

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static async Task<Document> SeedFaqDocAsync(
        RbRulesDbContext db, DateTimeOffset? retrievedAt = null)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ and Clarifications", Url = SourceUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "Uitleg over Legion.", ContentHash = "hash1",
        };
        if (retrievedAt is { } at) doc.RetrievedAt = at;
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }
}
