using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De agentic relatievoorstellen-poort zelf (#120), hier rechtstreeks
/// getest om de #321-begrenzing te pinnen: BrainService.NodeAsync resolvet óók
/// ruling:/source:/erratum:/… — soorten die de RELATES_TO-projectie nooit als
/// eindpunt schrijft. Vóór #321 landde zo'n voorstel als geldige rij die sinds
/// #320 elke rebuild stil verdampte; nu weigert de poort hem mét reden, in de
/// terugkoppeling (TraceLine) én in run_log. De AskService-bedrading eromheen
/// staat in AskServiceAgenticTests.</summary>
public class AgenticRelationServiceTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Does Deflect stop Viktor's ability?";

    // ── #321: eindpunt-soorten buiten de projectie worden geweigerd ────

    [Fact]
    public async Task StoreProposals_RulingEnSourceEindpunt_GeweigerdMetReden()
    {
        using var db = NewDb();
        var rulingId = await SeedAsync(db);
        var svc = Svc(db);

        // Beide refs bestáán in het brein (NodeAsync zou ze resolven): de
        // weigering komt uit de projectie-spiegel, niet uit het hallucinatie-weer.
        var raw = $$"""
            {"relations": [
              {"from": "card:test-viktor", "to": "ruling:{{rulingId}}", "kind": "clarifies", "explanation": "The ruling explains Viktor's ability."},
              {"from": "card:test-viktor", "to": "source:{{SourceId}}", "kind": "requires", "explanation": "The source documents the interaction."},
              {"from": "card:test-viktor", "to": "card:test-yasuo", "kind": "counters", "explanation": "Viktor bypasses Deflect."}
            ]}
            """;

        var result = await svc.StoreProposalsAsync(Question, raw);

        // Alleen het kaart↔kaart-voorstel landt; er komt géén rij met een
        // ruling:- of source:-eindpunt de database in — die zou sinds #320
        // elke rebuild stil verdampen.
        var relations = await db.Relations.ToListAsync();
        var enige = Assert.Single(relations);
        Assert.Equal("card:test-viktor", enige.FromRef);
        Assert.Equal("card:test-yasuo", enige.ToRef);

        Assert.Equal(1, result.Stored);
        Assert.Equal(2, result.OutsideProjection);
        Assert.Equal(0, result.Blocked);

        // De reden is zichtbaar in de relatie-terugkoppeling (trace-regel)…
        Assert.Contains("2 geweigerd (eindpunt-soort projecteert niet: ruling, source)",
            result.TraceLine);

        // …én in run_log.
        var log = await db.RunLogs.SingleAsync(l => l.Ref == "agentic-ask");
        Assert.Contains("2 geweigerd (eindpunt-soort projecteert niet: ruling, source)",
            log.Detail);
    }

    [Fact]
    public async Task StoreProposals_AlleVijfProjecteerbareSoorten_BlijvenGewoonWerken()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var claimId = db.Claims.Single().Id;
        var svc = Svc(db);

        // Eindpunten over alle vijf soorten (card, mechanic, section, concept,
        // claim) — de poort mag hier niets van weigeren.
        var raw = $$"""
            {"relations": [
              {"from": "card:test-viktor", "to": "mechanic:Deflect", "kind": "counters", "explanation": "Viktor bypasses Deflect."},
              {"from": "mechanic:Deflect", "to": "section:{{SourceId}}/466.2", "kind": "clarifies", "explanation": "The showdown rules define Deflect."},
              {"from": "claim:{{claimId}}", "to": "concept:combat", "kind": "clarifies", "explanation": "The claim refines the combat primer."}
            ]}
            """;

        var result = await svc.StoreProposalsAsync(Question, raw);

        Assert.Equal(3, result.Stored);
        Assert.Equal(0, result.OutsideProjection);
        Assert.Equal(0, result.Blocked);
        Assert.Equal(3, await db.Relations.CountAsync());
        Assert.DoesNotContain("geweigerd", result.TraceLine);
    }

    [Fact]
    public async Task StoreProposals_OnbekendeRefBlijftGeweerd_TellersLopenNietDoorElkaar()
    {
        using var db = NewDb();
        var rulingId = await SeedAsync(db);
        var svc = Svc(db);

        // Drie uitkomsten naast elkaar: verzonnen ref (hallucinatie-weer),
        // niet-projecteerbare soort (poort #321) en een geldig voorstel.
        var raw = $$"""
            {"relations": [
              {"from": "card:test-viktor", "to": "card:verzonnen-999", "kind": "counters", "explanation": "This ref does not exist."},
              {"from": "ruling:{{rulingId}}", "to": "card:test-yasuo", "kind": "clarifies", "explanation": "The ruling covers Yasuo."},
              {"from": "card:test-viktor", "to": "card:test-yasuo", "kind": "counters", "explanation": "Viktor bypasses Deflect."}
            ]}
            """;

        var result = await svc.StoreProposalsAsync(Question, raw);

        Assert.Equal(1, result.Stored);
        Assert.Equal(1, result.Blocked);
        Assert.Equal(1, result.OutsideProjection);
        Assert.Contains("1 geweerd (onbekende ref)", result.TraceLine);
        Assert.Contains("1 geweigerd (eindpunt-soort projecteert niet: ruling)",
            result.TraceLine);
    }

    // --- testinfra -------------------------------------------------------

    private static AgenticRelationService Svc(RbRulesDbContext db) =>
        new(db, new BrainService(
            db, FailingEmbeddings(), new CardResolver(db), NullLogger<BrainService>.Instance));

    /// <summary>Failing Ollama (patroon #100): NodeAsync gebruikt geen
    /// embeddings, dus dit pad blijft er volledig buiten.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new ThrowingHandler())
        { BaseAddress = new Uri("http://ollama.test") });

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("ollama plat");
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

    /// <summary>Knopen voor álle betrokken soorten, zodat elke kandidaat-ref in
    /// de tests het hallucinatie-weer passeert en de uitkomst puur van de
    /// #321-poort afhangt. Geeft het id van de geverifieerde ruling terug.</summary>
    private static async Task<long> SeedAsync(RbRulesDbContext db)
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
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "466.2",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen during a showdown.",
        });
        db.Cards.AddRange(
            new Card { RiftboundId = "test-viktor", Name = "Viktor" },
            new Card { RiftboundId = "test-yasuo", Name = "Yasuo", Mechanics = ["Deflect"] });
        db.KnowledgeDocs.Add(new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat",
            Body = "How combat works.", Status = "approved",
        });
        db.Claims.Add(new Claim
        {
            TopicType = "mechanic", TopicRef = "Deflect",
            Statement = "Deflect only blocks targeted effects.",
            Status = "accepted", Corroboration = 2, TrustScore = 0.6,
        });
        var ruling = new Correction
        {
            Scope = "card", Ref = "Viktor", Text = "Viktor's ability is not targeted.",
            Question = "Does Deflect stop it?", Provenance = "official",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.Corrections.Add(ruling);
        await db.SaveChangesAsync();
        return ruling.Id;
    }
}
