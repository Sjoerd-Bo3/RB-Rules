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

/// <summary>Regressietests voor de FAQ-/clarificatie-concept-extractie
/// (#177): losse verduidelijkingen worden direct geverifieerde rulings (de
/// bron is per definitie officieel), met een gefocuste embedding en
/// idempotentie op documentniveau (ClarifiedAt, #92/#93-patroon). De
/// concept-niveau-dedupe (parafrase-herkenning via de embedding-poort) staat
/// apart in ClarificationMiningDedupeTests, met een bag-of-words-embedding-
/// stub. Zelfde testinfra-patroon als ClaimMiningServiceTests: echte
/// RbAiClient/EmbeddingService op gestubde HTTP-handlers, EF InMemory (vector
/// als tekst-conversie).</summary>
public class ClarificationMiningServiceTests
{
    private const string SourceId = "playriftbound-com-unleashed-rules-faq-and-clarifications";
    private const string SourceUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    // Realistische, ingekorte fixture: één multi-concept-alinea zoals de
    // echte FAQ (Reflection tokens + Arcane Shift + Legion in dezelfde slab).
    private const string TwoConceptsAnswer =
        """
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion", "sectionRef": "402.3",
           "clarification": "Legion betekent dat je een item op de chain finalizet.",
           "quote": "Legion means you finalize an item on the chain"},
          {"topicType": "concept", "topicRef": "Reflection tokens",
           "clarification": "Reflection tokens tellen niet mee voor het handlimiet."}
        ]}
        """;

    private const string OneConceptAnswer =
        """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent dat je een item op de chain finalizet."}]}""";

    [Fact]
    public async Task RunAsync_GeslaagdeExtractie_MarkeertDocument_EnSlaatVerifiedRulingOp()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Documents);
        Assert.Equal(2, r.NewItems);
        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClarifiedAt);

        var legion = await db.Corrections.SingleAsync(c => c.Ref == "Legion");
        Assert.Equal("mechanic", legion.Scope);
        Assert.Equal("verified", legion.Status);
        Assert.NotNull(legion.VerifiedAt);
        Assert.NotNull(legion.Embedding);
        Assert.Equal(SourceUrl, legion.SourceRef);
        Assert.Equal($"clarify-mining:{SourceId}", legion.Provenance);
        Assert.Contains("finalize", legion.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Legion means you finalize", legion.Text); // citaat zichtbaar aangehaald
        Assert.Equal("Legion (§402.3)", legion.Question); // §-anker zichtbaar als label

        var concept = await db.Corrections.SingleAsync(c => c.Ref == "Reflection tokens");
        Assert.Equal("concept", concept.Scope);
        Assert.Equal("Reflection tokens", concept.Question); // geen sectionRef ⇒ kaal label
    }

    [Fact]
    public async Task RunAsync_SectionTopic_UsesRuleSectionScope()
    {
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var raw = """{"clarifications": [{"topicType": "section", "topicRef": "402.3", "clarification": "Een §-verduidelijking."}]}""";
        var svc = new ClarificationMiningService(db, Ai(() => raw), Embeddings(ok: true));

        await svc.RunAsync();

        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("rule_section", ruling.Scope);
        Assert.Equal("402.3", ruling.Ref);
        Assert.Equal("402.3", ruling.Question); // geen dubbele §-suffix als topic zelf al section is
    }

    [Fact]
    public async Task RunAsync_TweedeRunZonderForce_VerwerktGeenDocumentenMeer_IsIdempotent()
    {
        // "Tweede run = 0 nieuw": het document is al ClarifiedAt, dus de
        // eerstvolgende (niet-geforceerde) run slaat het simpelweg over.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        Assert.Equal(2, first.NewItems);
        Assert.Equal(0, second.Documents);
        Assert.Equal(0, second.NewItems);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    [Fact]
    public async Task RunAsync_GeforceerdeHerRun_ZelfdeExtractie_MaaktGeenDubbeleRuling()
    {
        // Idempotent op conceptniveau — óók als het document opnieuw wordt
        // verwerkt (force) en de LLM identiek antwoordt: StoreAsync werkt de
        // bestaande rulings bij i.p.v. te dupliceren. (Parafrase-herkenning:
        // zie ClarificationMiningDedupeTests.)
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync(force: true);

        Assert.Equal(2, first.NewItems);
        Assert.Equal(0, second.NewItems); // zelfde concepten al bekend — bijgewerkt, niet gedupliceerd
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    [Fact]
    public async Task RunAsync_BestaandeAlGeingesteBron_WordtVanzelfGebackfilld()
    {
        // Sjoerd-eis: de mining moet terugwerkend over al-geïngeste FAQ-
        // bronnen draaien, niet alleen nieuw-gescande. De bronselectie kent
        // geen tijdvenster — een pre-existing Source+Document (ClarifiedAt
        // was er nog niet toen dit artikel voor het eerst gescand werd, dus
        // start op null) komt bij de eerste run van deze service gewoon mee.
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db, retrievedAt: DateTimeOffset.UtcNow.AddMonths(-6));
        Assert.Null(doc.ClarifiedAt); // "van vóór deze feature"

        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: true));
        var r = await svc.RunAsync();

        Assert.Equal(1, r.Documents);
        Assert.Equal(1, r.NewItems);
        Assert.NotNull(doc.ClarifiedAt);
        Assert.Equal("Legion", (await db.Corrections.SingleAsync()).Ref);
    }

    [Fact]
    public async Task RunAsync_NietMatchendeBron_WordtOvergeslagen()
    {
        // Een gewone officiële bron zonder faq/clarification-signaal in
        // id/url/naam hoort niet in deze pijplijn — dat is de claims-/errata-
        // paden, niet clarify.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "playriftbound-com-core-rules", Name = "Core Rules",
            Url = "https://playriftbound.com/core-rules.pdf",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = "playriftbound-com-core-rules", Content = "Regeltekst.", ContentHash = "hash",
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Documents);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_CommunityBron_WordtOvergeslagen_OokMetFaqInDeNaam()
    {
        // Autoriteitsmodel #166: alleen TrustTier == 1 mag automatisch
        // verified worden — een community-mirror met "faq" in de naam
        // (trust 3) hoort hier niet mee te doen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "community-faq-mirror", Name = "Community FAQ mirror",
            Url = "https://example.com/faq", Type = "community", TrustTier = 3,
            Rank = 10, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = "community-faq-mirror", Content = "Uitleg.", ContentHash = "hash",
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Documents);
    }

    [Fact]
    public async Task RunAsync_EmbeddingFailure_LaatDocumentOngemarkeerd_EnLogtReden()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.NewItems);
        Assert.Contains("redenen in run_log", r.Message);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("embedding mislukt", error.Detail);

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents); // blijft staan voor een volgende run
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_EnLaatDocumentStaan()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(
            db, Ai(() => "Ik zie hier geen bruikbare\nconcepten, sorry!"), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        Assert.Contains("Respons (afgekapt): Ik zie hier geen bruikbare concepten, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_LogtUitval_EnLaatDocumentStaan()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);
    }

    [Fact]
    public async Task RunAsync_GeenConceptenGevonden_MarkeertDocumentWel()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(
            db, Ai(() => """{"clarifications": []}"""), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClarifiedAt);
        var again = await svc.RunAsync();
        Assert.Equal(0, again.Documents);
    }

    [Fact]
    public async Task RunAsync_CapMiddenInDocument_LaatDocumentOngemarkeerd_TotEenHerRun()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync(maxItems: 1);

        Assert.Equal(1, r.NewItems);
        Assert.Contains("cap van 1 bereikt", r.Message);
        Assert.Null(doc.ClarifiedAt);

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents);
        Assert.Equal(1, again.NewItems); // het eerste item is al bekend, alleen het tweede is nieuw
        Assert.NotNull(doc.ClarifiedAt);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    // --- testinfra (zelfde patroon als ClaimMiningServiceTests) ----------

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
            SourceId = SourceId,
            Content = "Uitleg over Legion, Reflection tokens en Arcane Shift.",
            ContentHash = "hash",
        };
        if (retrievedAt is { } at) doc.RetrievedAt = at;
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }
}
