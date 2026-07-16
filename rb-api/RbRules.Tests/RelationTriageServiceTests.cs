using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van de relatie-triage (#199 v1): een al
/// mens-beoordeeld voorstel wordt nooit her-getriaged, AI-uitval/onparseerbare
/// output is transiënt (geen marker, gewoon overgeslagen), CapHit is het
/// #190-drain-signaal (vóór verwerking bepaald — gefaalde items in dit batch
/// tellen niet mee), en het bestaande accept-/reject-pad blijft de enige
/// plek die Status wijzigt (los via DecideAsync, of in bulk per
/// aanbevelingsgroep via BulkDecideAsync). rb-ai is de échte client op een
/// gestubde HTTP-handler; de database is EF InMemory — de relaties in deze
/// tests gebruiken bewust alleen concept-/section-refs (geen mechanic:) zodat
/// de ILike-context-lookup (niet InMemory-vertaalbaar, zelfde beperking als
/// RelationMiningServiceTests) buiten beeld blijft.</summary>
public class RelationTriageServiceTests
{
    private const string AcceptAnswer = """
        {"recommendation": "accept", "reason": "The section confirms the relation.", "refs": ["7.4"]}
        """;

    [Fact]
    public async Task RunAsync_GeldigOordeel_SlaatAanbevelingOp()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        var svc = new RelationTriageService(db, Ai(() => AcceptAnswer));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Judged);
        Assert.Equal(1, r.Accepted);
        Assert.Equal(0, r.Rejected);
        Assert.Equal(0, r.Unsure);
        Assert.Equal(0, r.Skipped);
        Assert.False(r.CapHit);

        Assert.Equal("accept", relation.Recommendation);
        Assert.Contains("The section confirms the relation.", relation.RecommendationReason);
        Assert.Contains("refs: 7.4", relation.RecommendationReason);
        Assert.NotNull(relation.RecommendedAt);
        // De aanbeveling is geen autoriteit: Status blijft ongemoeid.
        Assert.Equal("unreviewed", relation.Status);

        var summary = await db.RunLogs.SingleAsync(l => l.Kind == "relationtriage" && l.Status == "ok");
        Assert.Contains("1 beoordeeld", summary.Detail);
        Assert.Contains("1 accept", summary.Detail);
    }

    [Fact]
    public async Task RunAsync_MensBeoordeeldVoorstel_WordtNooitGetriaged()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        relation.Status = "accepted";
        relation.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var calls = 0;
        var svc = new RelationTriageService(db, Ai(() => { calls++; return AcceptAnswer; }));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Judged);
        Assert.Null(relation.Recommendation);
        // Niet alleen geen aanbeveling: rb-ai is voor dit item ook echt niet
        // aangeroepen (het item zat nooit in de kandidatenquery).
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_AlGetriageerdVoorstel_WordtNietOpnieuwBeoordeeld()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        relation.Recommendation = "unsure";
        relation.RecommendationReason = "eerdere run";
        relation.RecommendedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var calls = 0;
        var svc = new RelationTriageService(db, Ai(() => { calls++; return AcceptAnswer; }));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Judged);
        Assert.Equal("unsure", relation.Recommendation);
        Assert.Equal("eerdere run", relation.RecommendationReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_SlaatOver_Transient_GeenAanbeveling()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        var svc = new RelationTriageService(db, Ai(() => null));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Judged);
        Assert.Equal(1, r.Skipped);
        Assert.Null(relation.Recommendation);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "relationtriage" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);

        // Transiënt: de volgende run pakt hetzelfde voorstel gewoon weer op.
        var again = await new RelationTriageService(db, Ai(() => AcceptAnswer)).RunAsync();
        Assert.Equal(1, again.Judged);
        Assert.Equal("accept", relation.Recommendation);
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_SlaatOver()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        var svc = new RelationTriageService(
            db, Ai(() => "I cannot judge this,\nsorry!"));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Judged);
        Assert.Equal(1, r.Skipped);
        Assert.Null(relation.Recommendation);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "relationtriage" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        Assert.Contains("Respons (afgekapt): I cannot judge this, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_OnbekendeRecommendationWaarde_SlaatOver()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        var svc = new RelationTriageService(
            db, Ai(() => """{"recommendation": "maybe", "reason": "geen idee"}"""));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Judged);
        Assert.Equal(1, r.Skipped);
        Assert.Null(relation.Recommendation);
    }

    [Fact]
    public async Task RunAsync_MeerVoorstellenDanMaxItems_ZetCapHit()
    {
        using var db = NewDb();
        await SeedRelationAsync(db, "concept:combat", "section:core-rules-pdf/7.4");
        await SeedRelationAsync(db, "concept:combat", "section:core-rules-pdf/7.5");
        var svc = new RelationTriageService(db, Ai(() => AcceptAnswer));

        var r = await svc.RunAsync(maxItems: 1);

        Assert.Equal(1, r.Judged);
        Assert.True(r.CapHit);
        Assert.Contains("cap van 1 bereikt", r.Message);

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Judged);
        Assert.False(again.CapHit);
    }

    [Fact]
    public async Task DecideAsync_ZetStatusEnReviewedAt_LaatAanbevelingStaan()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        relation.Recommendation = "accept";
        relation.RecommendationReason = "reason";
        relation.RecommendedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var svc = new RelationTriageService(db, Ai(() => null));

        var outcome = await svc.DecideAsync(relation.Id, "accept", note: "ziet er goed uit");

        Assert.Equal(RelationDecisionOutcome.Applied, outcome);
        Assert.Equal("accepted", relation.Status);
        Assert.NotNull(relation.ReviewedAt);
        Assert.Equal("ziet er goed uit", relation.ReviewNote);
        // Herkomst blijft zichtbaar (#199 eis 4): de aanbeveling verdwijnt niet.
        Assert.Equal("accept", relation.Recommendation);
    }

    [Fact]
    public async Task DecideAsync_OnbekendId_GeeftNotFound()
    {
        using var db = NewDb();
        var svc = new RelationTriageService(db, Ai(() => null));

        var outcome = await svc.DecideAsync(999, "accept", note: null);

        Assert.Equal(RelationDecisionOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task BulkDecideAsync_RaaktAlleenDeGevraagdeGroep()
    {
        using var db = NewDb();
        var accept1 = await SeedRelationAsync(db, "concept:combat", "section:core-rules-pdf/7.4");
        var accept2 = await SeedRelationAsync(db, "concept:combat", "section:core-rules-pdf/7.5");
        var reject1 = await SeedRelationAsync(db, "concept:combat", "section:core-rules-pdf/7.6");
        accept1.Recommendation = "accept";
        accept2.Recommendation = "accept";
        reject1.Recommendation = "reject";
        await db.SaveChangesAsync();
        var svc = new RelationTriageService(db, Ai(() => null));

        var applied = await svc.BulkDecideAsync("accept", "accept");

        Assert.Equal(2, applied);
        Assert.Equal("accepted", accept1.Status);
        Assert.Equal("accepted", accept2.Status);
        // De reject-groep is ongemoeid — de bulk-actie raakt alleen de
        // gevraagde aanbevelingsgroep.
        Assert.Equal("unreviewed", reject1.Status);
    }

    [Fact]
    public async Task BulkDecideAsync_RaaktGeenAlAlBeoordeeldeItems()
    {
        // Een mens-oordeel wint altijd: een al geaccepteerd/verworpen item
        // met dezelfde aanbeveling (bv. van vóór de bulk-klik) telt niet mee.
        using var db = NewDb();
        var alreadyAccepted = await SeedRelationAsync(db);
        alreadyAccepted.Recommendation = "accept";
        alreadyAccepted.Status = "accepted";
        alreadyAccepted.ReviewedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        var svc = new RelationTriageService(db, Ai(() => null));

        var applied = await svc.BulkDecideAsync("accept", "accept");

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task BulkDecideAsync_OngeldigeDecision_DoetNiets()
    {
        using var db = NewDb();
        var relation = await SeedRelationAsync(db);
        relation.Recommendation = "accept";
        await db.SaveChangesAsync();
        var svc = new RelationTriageService(db, Ai(() => null));

        var applied = await svc.BulkDecideAsync("accept", "delete");

        Assert.Equal(0, applied);
        Assert.Equal("unreviewed", relation.Status);
    }

    // --- testinfra (patroon RelationMiningServiceTests) --------------------

    private static async Task<Relation> SeedRelationAsync(
        RbRulesDbContext db,
        string fromRef = "concept:combat", string toRef = "section:core-rules-pdf/7.4")
    {
        if (!await db.Sources.AnyAsync(s => s.Id == "core-rules-pdf"))
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
            db.RuleChunks.Add(new RuleChunk
            {
                DocumentId = 1, SourceId = "core-rules-pdf", SectionCode = "7.5",
                ChunkIndex = 1, Text = "Combat damage is dealt simultaneously.",
            });
            db.RuleChunks.Add(new RuleChunk
            {
                DocumentId = 1, SourceId = "core-rules-pdf", SectionCode = "7.6",
                ChunkIndex = 2, Text = "Units are removed when their health reaches zero.",
            });
            db.KnowledgeDocs.Add(new KnowledgeDoc
            {
                Kind = "primer", Topic = "combat", Title = "Combat",
                Body = "Combat is about dealing damage.", Status = "approved",
            });
        }

        var relation = new Relation
        {
            FromRef = fromRef, ToRef = toRef, Kind = "clarifies",
            Explanation = "Deflect reduces combat damage.",
            Provenance = "concept:combat", Trust = ClaimScoring.TierWeight(2),
        };
        db.Relations.Add(relation);
        await db.SaveChangesAsync();
        return relation;
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
            // BulkDecideAsync gebruikt een echte transactie (multi-row); de
            // InMemory-provider negeert die (CardSyncRepairTests-patroon) —
            // de echte transactiegrens draait alleen tegen Postgres.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
