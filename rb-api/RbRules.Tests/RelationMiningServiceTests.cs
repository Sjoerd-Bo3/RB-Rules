using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van de relatie-mining (#116), met de #92/#93-lessen
/// als contract: relations_mined_at pas ná geslaagde verwerking, parse-uitval
/// logt de rauwe respons in run_log, her-runs zijn idempotent en verworpen
/// kinds komen niet opnieuw de queue in. rb-ai is de échte client op een
/// gestubde HTTP-handler; de database is EF InMemory (geen pgvector/ILike-
/// paden: de tests seeden bewust hooguit één mechaniek zodat de
/// mechanieken-overzichtspass — die ILike gebruikt — niet draait).</summary>
public class RelationMiningServiceTests
{
    private const string GoodAnswer = """
        {"relations": [
          {"from": "mechanic:Deflect", "to": "section:core-rules-pdf/7.4",
           "kind": "wordt beperkt door", "explanation": "Deflect werkt alleen op combat-schade (§7.4)."},
          {"from": "concept:combat", "to": "mechanic:Deflect",
           "kind": "ontgrendelt", "explanation": "Combat maakt Deflect relevant."}
        ]}
        """;

    [Fact]
    public async Task RunAsync_GeslaagdeExtractie_SlaatVoorstellenOp_EnMarkeertDoc()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(db, Ai(() => GoodAnswer));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Units);
        Assert.Equal(2, r.NewRelations);
        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.RelationsMinedAt);

        var relations = await db.Relations.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, relations.Count);
        Assert.All(relations, rel =>
        {
            Assert.Equal("unreviewed", rel.Status);
            Assert.Equal("concept:combat", rel.Provenance);
            Assert.Equal(ClaimScoring.TierWeight(2), rel.Trust);
        });
        Assert.Equal("wordt beperkt door", relations[0].Kind);

        // "ontgrendelt" is geen seed-kind → kandidaat in de reviewqueue;
        // "wordt beperkt door" is seed en wordt dus níét als kandidaat gemeld.
        var kind = Assert.Single(await db.RelationKinds.ToListAsync());
        Assert.Equal("ontgrendelt", kind.Kind);
        Assert.Equal("candidate", kind.Status);
        Assert.Equal(1, kind.Occurrences);
        Assert.Equal(1, r.NewKinds);
    }

    [Fact]
    public async Task RunAsync_HerRun_IsIdempotent()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(db, Ai(() => GoodAnswer));

        await svc.RunAsync();
        var again = await svc.RunAsync();

        // Doc is gemarkeerd: geen eenheden meer, geen dubbele voorstellen.
        Assert.Equal(0, again.Units);
        Assert.Equal(2, await db.Relations.CountAsync());

        // force her-mined wél, maar de dedupe houdt de opslag idempotent.
        var forced = await svc.RunAsync(force: true);
        Assert.Equal(1, forced.Units);
        Assert.Equal(0, forced.NewRelations);
        Assert.Equal(2, forced.Duplicates);
        Assert.Equal(2, await db.Relations.CountAsync());
    }

    [Fact]
    public async Task RunAsync_DocGewijzigdNaMining_WordtVanzelfOpnieuwGemined()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(db, Ai(() => GoodAnswer));
        await svc.RunAsync();

        // Primer-herziening (bijv. set-release): updated_at schuift voorbij
        // de marker — zelf-invaliderend, geen handmatige reset nodig.
        doc.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Units);
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_EnLaatDocStaan()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(
            db, Ai(() => "Ik zie hier geen relaties,\nsorry!"));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.RelationsMinedAt);
        var error = await db.RunLogs.SingleAsync(
            l => l.Kind == "relations" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        // Platgeslagen en herkenbaar afgekapt meegelogd (#93/PR #87-patroon).
        Assert.Contains("Respons (afgekapt): Ik zie hier geen relaties, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_LogtUitval_EnLaatDocStaan()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(db, Ai(() => null));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.RelationsMinedAt);
        var error = await db.RunLogs.SingleAsync(
            l => l.Kind == "relations" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);

        // Volgende run pakt hetzelfde anker opnieuw op.
        var again = await new RelationMiningService(db, Ai(() => GoodAnswer)).RunAsync();
        Assert.Equal(1, again.Units);
        Assert.NotNull(doc.RelationsMinedAt);
    }

    [Fact]
    public async Task RunAsync_VerworpenKind_WordtNietOpnieuwOpgevoerd()
    {
        using var db = NewDb();
        await SeedConceptWorldAsync(db);
        db.RelationKinds.Add(new RelationKind
        {
            Kind = "ontgrendelt", Status = "rejected", Occurrences = 1,
        });
        await db.SaveChangesAsync();
        var svc = new RelationMiningService(db, Ai(() => GoodAnswer));

        var r = await svc.RunAsync();

        // Het seed-kind-voorstel komt binnen; het verworpen kind niet — en
        // er ontstaat géén nieuwe kandidaat.
        Assert.Equal(1, r.NewRelations);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(0, r.NewKinds);
        var rel = Assert.Single(await db.Relations.ToListAsync());
        Assert.Equal("wordt beperkt door", rel.Kind);
        var kind = Assert.Single(await db.RelationKinds.ToListAsync());
        Assert.Equal("rejected", kind.Status);
        Assert.Equal(1, kind.Occurrences);
    }

    [Fact]
    public async Task RunAsync_GehallucineerdeRefs_KomenDeDatabaseNietIn()
    {
        using var db = NewDb();
        var doc = await SeedConceptWorldAsync(db);
        var svc = new RelationMiningService(db, Ai(() => """
            {"relations": [{"from": "mechanic:Verzonnen", "to": "card:nep-001",
             "kind": "counters", "explanation": "hallucinatie"}]}
            """));

        var r = await svc.RunAsync();

        // Geparsed maar niets bruikbaars: dat is een geldig (leeg) resultaat,
        // dus het doc wordt gemarkeerd; de nep-refs bestaan nergens.
        Assert.Equal(0, r.NewRelations);
        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.RelationsMinedAt);
        Assert.Empty(await db.Relations.ToListAsync());
    }

    // --- testinfra (patroon ClaimMiningServiceTests) ----------------------

    /// <summary>Eén primer-doc ("combat") met een §-sectie en een kaart met
    /// mechaniek Deflect: het anker biedt concept-, sectie-, mechaniek- en
    /// kaart-refs aan. Positief: één mechaniek — de mechanieken-overzichtspass
    /// (ILike, niet InMemory-vertaalbaar) blijft zo buiten beeld.</summary>
    private static async Task<KnowledgeDoc> SeedConceptWorldAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = "core-rules-pdf", Name = "Core Rules", Url = "https://example.test/rules",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "daily",
        });
        db.Documents.Add(new Document
        {
            Id = 1, SourceId = "core-rules-pdf", Content = "regels", ContentHash = "h1",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = "core-rules-pdf", SectionCode = "7.4",
            ChunkIndex = 0, Text = "Deflect reduces combat damage dealt to this unit.",
        });
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-011-298", Name = "Shen", Mechanics = ["Deflect"],
            TextPlain = "[Deflect]", Tags = [],
        });
        var doc = new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat",
            Body = "Combat draait om schade; Deflect vermindert die. Shen is het schoolvoorbeeld.",
            SectionRefs = "7.4", Status = "approved",
        };
        db.KnowledgeDocs.Add(doc);
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
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { answer = a }) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);
}
