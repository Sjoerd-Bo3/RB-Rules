using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van de kennis-hertoets (#119): grootboek-
/// idempotentie (ok pas na succes, #93), draft-met-reden zonder stapeling,
/// uitgestelde changes bij official-check-uitval, en de venstergrenzen
/// (unknown wacht op naclassificatie; changes van de lopende scan wachten op
/// de her-index). Database is EF InMemory; de official-check is een stub —
/// CosineDistance vertaalt alleen naar Postgres, en de echte check is al
/// gedekt door de claims-pipeline-tests.</summary>
public class KnowledgeRecheckServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task RunAsync_MarkeertDocEnHertoetstClaim_EnVinktChangeAfNaSucces()
    {
        using var db = NewDb();
        var (change, doc, claim) = await SeedAsync(db);
        var svc = Service(db, () => ("contradicted", "§101.2 zegt het tegendeel", null));

        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        Assert.Equal(1, r.Changes);
        Assert.Equal(1, r.DocsMarked);
        Assert.Equal(1, r.ClaimsRechecked);
        Assert.Equal(1, r.ClaimsOutdated);
        Assert.Equal(0, r.Deferred);

        // Draft-met-reden: nooit stil terugzetten — de beheerder ziet in de
        // reviewqueue wélke change het doc terughaalde.
        Assert.Equal("draft", doc.Status);
        var reason = Assert.Single(KnowledgeRecheck.MarkerReasons(doc.Body));
        Assert.Contains($"#{change.Id}", reason);
        Assert.Contains("§101.2", reason);

        Assert.Equal("superseded", claim.Status);
        Assert.Equal("contradicted", claim.OfficialStatus);
        Assert.StartsWith(KnowledgeRecheck.ClaimReasonPrefix, claim.StatusReason);

        var ok = Assert.Single(await db.RunLogs
            .Where(l => l.Kind == "recheck" && l.Status == "ok").ToListAsync());
        Assert.Equal($"change:{change.Id}", ok.Ref);

        // Grootboek: een tweede run doet niets meer.
        var again = await svc.RunAsync(since: Now.AddDays(-14), before: Now);
        Assert.Equal(0, again.Changes);
        Assert.Single(await db.RunLogs.Where(l => l.Kind == "recheck").ToListAsync());
    }

    [Fact]
    public async Task RunAsync_OfficialCheckWeg_MarkeertDocWel_MaarLaatChangeStaan()
    {
        using var db = NewDb();
        var (change, doc, claim) = await SeedAsync(db);
        var svc = Service(db, () => ("unchecked", null, "rb-ai niet beschikbaar"));

        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        // Het doc-deel is puur DB en slaagt; het claim-deel wacht — markeren
        // na succes (#93): geen "ok"-grootboekregel, wél een herleidbare
        // info-regel.
        Assert.Equal(1, r.Deferred);
        Assert.Equal("draft", doc.Status);
        Assert.Equal("accepted", claim.Status);
        Assert.Equal("unchecked", claim.OfficialStatus);
        var info = Assert.Single(await db.RunLogs.Where(l => l.Kind == "recheck").ToListAsync());
        Assert.Equal("info", info.Status);
        Assert.Contains("rb-ai niet beschikbaar", info.Detail);

        // Volgende run mét oordeel: claim alsnog hertoetst, change afgevinkt,
        // en de kanttekening op het doc stapelt niet.
        var retry = Service(db, () => ("contradicted", null, null));
        var r2 = await retry.RunAsync(since: Now.AddDays(-14), before: Now);
        Assert.Equal(0, r2.Deferred);
        Assert.Equal("superseded", claim.Status);
        Assert.Single(KnowledgeRecheck.MarkerReasons(doc.Body));
        Assert.Single(await db.RunLogs
            .Where(l => l.Kind == "recheck" && l.Status == "ok").ToListAsync());
        _ = change;
    }

    [Fact]
    public async Task RunAsync_BevestigdeClaim_BlijftAccepted_MetBijgewerkteOfficialStatus()
    {
        using var db = NewDb();
        var (_, _, claim) = await SeedAsync(db);
        var svc = Service(db, () => ("confirmed", null, null));

        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        Assert.Equal(1, r.ClaimsRechecked);
        Assert.Equal(0, r.ClaimsOutdated);
        Assert.Equal("accepted", claim.Status);
        Assert.Equal("confirmed", claim.OfficialStatus);
    }

    [Fact]
    public async Task RunAsync_GeenBetrokkenKennis_VinktChangeAf()
    {
        using var db = NewDb();
        await SeedAsync(db, diff: "Alleen een redactionele opmerking.");
        var svc = Service(db, () => throw new InvalidOperationException("mag niet aangeroepen worden"));

        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        Assert.Equal(1, r.Changes);
        Assert.Equal(0, r.DocsMarked);
        var ok = Assert.Single(await db.RunLogs.Where(l => l.Kind == "recheck").ToListAsync());
        Assert.Equal("ok", ok.Status);
        Assert.Contains("geen betrokken kennis", ok.Detail);
    }

    [Fact]
    public async Task RunAsync_UnknownChange_WachtOpNaclassificatie()
    {
        using var db = NewDb();
        var (change, doc, claim) = await SeedAsync(db, changeType: "unknown");
        var svc = Service(db, () => ("confirmed", null, null));

        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        // Niet afvinken: de naclassificatie (#58) kan het type alsnog op
        // core-rule zetten en dan hoort de hertoets alsnog te draaien.
        Assert.Equal(0, r.Changes);
        Assert.Empty(await db.RunLogs.Where(l => l.Kind == "recheck").ToListAsync());
        Assert.Equal("approved", doc.Status);
        Assert.Equal("accepted", claim.Status);
        _ = change;
    }

    [Fact]
    public async Task RunAsync_ChangeVanDeLopendeScan_WachtOpDeHerIndex()
    {
        using var db = NewDb();
        await SeedAsync(db, detectedAt: Now.AddMinutes(5));
        var svc = Service(db, () => ("confirmed", null, null));

        // before = de starttijd van de scan: de official-check moet tegen de
        // hér-geïndexeerde regeltekst toetsen, dus deze change wacht één run.
        var r = await svc.RunAsync(since: Now.AddDays(-14), before: Now);

        Assert.Equal(0, r.Changes);
        Assert.Empty(await db.RunLogs.Where(l => l.Kind == "recheck").ToListAsync());
    }

    // --- testinfra -------------------------------------------------------

    private static KnowledgeRecheckService Service(
        RbRulesDbContext db, Func<(string, string?, string?)> verdict) =>
        new(db, new StubClaims(db, verdict));

    /// <summary>Test-seam (#119): alleen de official-check is gestubd — het
    /// echte pad (chunks + LLM) is gedekt door de claims-pipeline-tests en
    /// vertaalt niet naar EF InMemory (CosineDistance).</summary>
    private sealed class StubClaims(RbRulesDbContext db, Func<(string, string?, string?)> verdict)
        : ClaimMiningService(db, StubAi(), StubEmbeddings())
    {
        public override Task<(string OfficialStatus, string? Reason, string? Degraded)>
            CheckOfficialAsync(
                string statement, Vector vec, IReadOnlyList<string> officialSourceIds,
                CancellationToken ct) =>
            Task.FromResult(verdict());
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private static RbAiClient StubAi() => new(
        new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static EmbeddingService StubEmbeddings() => new(
        new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://ollama.test") });

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

    /// <summary>Standaardscenario: een core-rule-change die §101.2 raakt, een
    /// goedgekeurd primer-doc dat op §101.2 leunt en een accepted claim over
    /// diezelfde sectie.</summary>
    private static async Task<(Change Change, KnowledgeDoc Doc, Claim Claim)> SeedAsync(
        RbRulesDbContext db,
        string changeType = "core-rule",
        string diff = "+ Regel 101.2 is herschreven.",
        DateTimeOffset? detectedAt = null)
    {
        db.Sources.Add(new Source
        {
            Id = "core-rules-pdf", Name = "Core Rules", Url = "https://example.com/core.pdf",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "weekly",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            SourceId = "core-rules-pdf", SectionCode = "101.2",
            Text = "De oorspronkelijke regeltekst.",
        });
        var change = new Change
        {
            SourceId = "core-rules-pdf", ChangeType = changeType, Severity = "high",
            Summary = "Regelwijziging", Diff = diff,
            DetectedAt = detectedAt ?? Now.AddHours(-2),
        };
        db.Changes.Add(change);
        var doc = new KnowledgeDoc
        {
            Kind = "primer", Topic = "turn-structure", Title = "The turn structure",
            Body = "Eerste alinea.\n\nTweede alinea (§101.2).",
            SectionRefs = "101.2", Status = "approved",
        };
        db.KnowledgeDocs.Add(doc);
        var claim = new Claim
        {
            TopicType = "section", TopicRef = "101.2",
            Statement = "Je mag dit maar één keer per beurt doen.",
            Status = "accepted", OfficialStatus = "unchecked",
            Embedding = new Vector(new float[EmbeddingConfig.Dimensions]),
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (change, doc, claim);
    }
}
