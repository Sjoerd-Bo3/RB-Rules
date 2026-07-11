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

/// <summary>Regressietests voor de run-semantiek van de claims-mining
/// (#92/#93): claims_mined_at pas ná geslaagde verwerking, cap laat het
/// onverwerkte deel ongemarkeerd, en elke faalstap is herleidbaar in run_log.
/// rb-ai en Ollama zijn de échte clients op gestubde HTTP-handlers; de
/// database is EF InMemory — de geteste paden raken bewust geen
/// pgvector-operaties (CosineDistance vertaalt alleen naar Postgres).</summary>
public class ClaimMiningServiceTests
{
    private const string SourceId = "community-gids";

    private const string OneClaimAnswer =
        """{"claims": [{"topicType": "concept", "topicRef": "mulligan", "statement": "Je mag één keer je starthand omruilen.", "quote": "one mulligan"}]}""";

    private const string TwoClaimsAnswer =
        """
        {"claims": [
          {"topicType": "concept", "topicRef": "mulligan", "statement": "Je mag één keer je starthand omruilen."},
          {"topicType": "concept", "topicRef": "scoren", "statement": "Je scoort door een battlefield te houden."}
        ]}
        """;

    [Fact]
    public async Task RunAsync_EmbeddingFailure_LaatDocumentOngemarkeerd_EnLogtReden()
    {
        // Regressie #93: op productie faalden 60/60 claims op de embedding-
        // stap zonder één zichtbare foutregel — en de documenten werden
        // ondanks nul opgeslagen claims als gemined gemarkeerd (#92-klasse).
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        var svc = new ClaimMiningService(db, Ai(() => OneClaimAnswer), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.NewClaims);
        Assert.Contains("redenen in run_log", r.Message);
        Assert.Null(doc.ClaimsMinedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "claims" && l.Status == "error");
        Assert.Contains("embedding mislukt", error.Detail);

        // Het document blijft staan en wordt bij een volgende run opnieuw
        // geprobeerd (#92: één her-run volstaat om de queue te vullen).
        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents);
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_EnLaatDocumentStaan()
    {
        // Regressie #93a: bij parse-uitval hoort de afgekapte rauwe respons in
        // run_log (scout-patroon, PR #87) — nooit meer een stille teller.
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        var svc = new ClaimMiningService(
            db, Ai(() => "Ik zie hier geen bruikbare\nclaims, sorry!"), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClaimsMinedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "claims" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        // Platgeslagen (geen newlines) en herkenbaar afgekapt meegelogd.
        Assert.Contains("Respons (afgekapt): Ik zie hier geen bruikbare claims, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_LogtUitval_EnLaatDocumentStaan()
    {
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        var svc = new ClaimMiningService(db, Ai(() => null), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClaimsMinedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "claims" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);
    }

    [Fact]
    public async Task RunAsync_GeenClaimsGevonden_MarkeertDocumentWel()
    {
        // Een geldig-lege oogst is een geslaagd resultaat ("aantoonbaar niets
        // te vinden"): markeren, anders blijft het document eeuwig terugkomen.
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        var svc = new ClaimMiningService(
            db, Ai(() => """{"claims": []}"""), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClaimsMinedAt);
        var again = await svc.RunAsync();
        Assert.Equal(0, again.Documents);
    }

    [Fact]
    public async Task RunAsync_GeslaagdeExtractie_MarkeertDocument_EnSlaatClaimOp()
    {
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        var svc = new ClaimMiningService(db, Ai(() => OneClaimAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.NewClaims);
        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClaimsMinedAt);
        var claim = await db.Claims.SingleAsync();
        Assert.Equal("mulligan", claim.TopicRef);
        Assert.NotNull(claim.Embedding);
        var evidence = await db.ClaimSources.SingleAsync();
        Assert.Equal(SourceId, evidence.SourceId);
        Assert.Equal("one mulligan", evidence.QuoteExcerpt);
    }

    [Fact]
    public async Task RunAsync_CapMiddenInDocument_LaatDocumentOngemarkeerd_TotEenHerRun()
    {
        // Cap-semantiek (#92): stopt de run op de kostencap, dan blijft het
        // onverwerkte deel ongemarkeerd en maakt één her-run het werk af.
        using var db = NewDb();
        var doc = await SeedCommunityDocAsync(db);
        await SeedExistingClaimsAsync(db); // beide claims bestaan al → geen vector-pad nodig
        var svc = new ClaimMiningService(db, Ai(() => TwoClaimsAnswer), Embeddings(ok: false));

        var r = await svc.RunAsync(maxClaims: 1);

        Assert.Equal(1, r.Corroborated);
        Assert.Equal(0, r.Failed);
        Assert.Contains("cap van 1 claims bereikt", r.Message);
        Assert.Null(doc.ClaimsMinedAt);

        // Her-run zonder cap-druk: dedupe maakt de eerste claim idempotent
        // (zelfde bron = "al bekend"), de tweede wordt alsnog verwerkt en
        // daarná pas wordt het document gemarkeerd.
        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents);
        Assert.Equal(1, again.Corroborated);
        Assert.NotNull(doc.ClaimsMinedAt);
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

    /// <summary>Echte RbAiClient op een gestubde handler: null ⇒ 500 (uitval),
    /// anders het gegeven antwoord als {"answer": ...}.</summary>
    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Echte EmbeddingService op een gestubde Ollama: ok ⇒ één
    /// embedding met de juiste dimensie, anders 500 (Ollama-uitval — de
    /// productie-faalvorm van #93).</summary>
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

    private static async Task<Document> SeedCommunityDocAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Community-gids", Url = "https://example.com/gids",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "Uitleg over mulligans en scoren.",
            ContentHash = "hash",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Beide claims uit <see cref="TwoClaimsAnswer"/> bestaan al via
    /// een andere bron: de run corroboreert dan via de exacte-tekst-sneltoets
    /// (stap 1) en heeft geen embeddings of vector-queries nodig.</summary>
    private static async Task SeedExistingClaimsAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = "andere-bron", Name = "Andere bron", Url = "https://example.com/anders",
            Type = "community", TrustTier = 3, Rank = 5, Parser = "html", Cadence = "weekly",
        });
        foreach (var (topicRef, statement) in new[]
        {
            ("mulligan", "Je mag één keer je starthand omruilen."),
            ("scoren", "Je scoort door een battlefield te houden."),
        })
        {
            var claim = new Claim
            {
                TopicType = "concept", TopicRef = topicRef, Statement = statement,
                TrustScore = 0.5, OfficialStatus = "unclear",
            };
            db.Claims.Add(claim);
            await db.SaveChangesAsync();
            db.ClaimSources.Add(new ClaimSource
            {
                ClaimId = claim.Id, SourceId = "andere-bron", Url = "https://example.com/anders",
            });
        }
        await db.SaveChangesAsync();
    }
}
