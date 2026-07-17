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

/// <summary>Run-semantiek van de changeconsolidatie (#206): een deterministische
/// kandidaat-poort (type/bron/venster/overlappende refs) vóór een LLM-toets
/// ("zelfde gebeurtenis?"), AI-uitval/onparseerbare output is transiënt (geen
/// koppeling, gewoon overgeslagen — de veilige kant), de primaire wint op
/// TrustTier dan vroegste detectie, en koppelingen zijn idempotent zonder
/// ketens. rb-ai is de échte client op een gestubde HTTP-handler; de database
/// is EF InMemory (RelationTriageServiceTests-patroon).</summary>
public class ChangeConsolidationServiceTests
{
    private static readonly DateTimeOffset RulesHubDetectedAt =
        new(2026, 7, 16, 6, 46, 0, TimeSpan.Zero);

    private const string SameAnswer = """{"sameEvent": true}""";
    private const string DifferentAnswer = """{"sameEvent": false}""";

    [Fact]
    public async Task RunAsync_16JuliBackfillScenario_OfficieelWordtPrimairCommunityBevestiging()
    {
        // Het exacte terugwerkende scenario uit issue #206: de banupdate van
        // 16 juli staat als twee losse changes — Rules Hub (officieel,
        // trust 1, 06:46) en Mobalytics (community, trust 3, 06:51), beide
        // over dezelfde kaart. Eerste run na deze feature moet dit paar
        // meteen oppikken (geen aparte backfill nodig).
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var rulesHub = await SeedChangeAsync(
            db, "rules-hub", official: true, RulesHubDetectedAt,
            summary: "Viktor is banned in Constructed.");
        var mobalytics = await SeedChangeAsync(
            db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5),
            summary: "Community report: Viktor banned.");
        var svc = new ChangeConsolidationService(db, Ai(() => SameAnswer));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Merged);
        Assert.Equal(1, r.Judged);
        Assert.Equal(0, r.Skipped);
        Assert.Null(rulesHub.ConsolidatedWithId);
        Assert.Equal(rulesHub.Id, mobalytics.ConsolidatedWithId);

        var summary = await db.RunLogs.SingleAsync(l => l.Kind == "consolidatechanges" && l.Status == "ok");
        Assert.Contains("1 paar(en) geconsolideerd", summary.Detail);
    }

    [Fact]
    public async Task RunAsync_LlmNee_BlijftOngekoppeld()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var a = await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var b = await SeedChangeAsync(db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5), "Viktor banned.");
        var svc = new ChangeConsolidationService(db, Ai(() => DifferentAnswer));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Merged);
        Assert.Equal(1, r.Judged);
        Assert.Null(a.ConsolidatedWithId);
        Assert.Null(b.ConsolidatedWithId);
    }

    [Fact]
    public async Task RunAsync_GeenOverlappendeRefs_SlaatLlmCallOver()
    {
        // De poort filtert vóór de LLM-toets: verschillende kaarten ⇒ geen
        // kandidaat, dus rb-ai wordt hier niet eens aangeroepen.
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        await SeedCardAsync(db, "ogn-002", "Jinx");
        var a = await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var b = await SeedChangeAsync(db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5), "Jinx banned.");
        var calls = 0;
        var svc = new ChangeConsolidationService(db, Ai(() => { calls++; return SameAnswer; }));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Merged);
        Assert.Equal(0, r.Judged);
        Assert.Equal(0, calls);
        Assert.Null(a.ConsolidatedWithId);
        Assert.Null(b.ConsolidatedWithId);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_SlaatOver_Transient_LaterAlsnogGekoppeld()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var a = await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var b = await SeedChangeAsync(db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5), "Viktor banned.");
        var svc = new ChangeConsolidationService(db, Ai(() => null));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Merged);
        Assert.Equal(0, r.Judged);
        Assert.Equal(1, r.Skipped);
        Assert.Null(a.ConsolidatedWithId);
        Assert.Null(b.ConsolidatedWithId);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "consolidatechanges" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);

        // Transiënt: de volgende run pakt hetzelfde paar gewoon weer op.
        var again = await new ChangeConsolidationService(db, Ai(() => SameAnswer)).RunAsync();
        Assert.Equal(1, again.Merged);
        Assert.Equal(a.Id, b.ConsolidatedWithId);
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_SlaatOver()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var a = await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var b = await SeedChangeAsync(db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5), "Viktor banned.");
        var svc = new ChangeConsolidationService(db, Ai(() => "I cannot judge this,\nsorry!"));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Merged);
        Assert.Equal(1, r.Skipped);
        Assert.Null(a.ConsolidatedWithId);
        Assert.Null(b.ConsolidatedWithId);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "consolidatechanges" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        Assert.Contains("Respons (afgekapt): I cannot judge this, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_Idempotent_TweedeRunKoppeltNietDubbel()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var a = await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var b = await SeedChangeAsync(db, "mobalytics", official: false, RulesHubDetectedAt.AddMinutes(5), "Viktor banned.");
        var calls = 0;
        var svc = new ChangeConsolidationService(db, Ai(() => { calls++; return SameAnswer; }));

        var first = await svc.RunAsync();
        Assert.Equal(1, first.Merged);
        Assert.Equal(1, calls);

        // Tweede run: b is geen root meer (ConsolidatedWithId gezet), dus er
        // is nog maar één ongekoppelde change over — geen nieuw paar, geen
        // extra LLM-call, geen dubbele koppeling.
        var second = await svc.RunAsync();
        Assert.Equal(0, second.Merged);
        Assert.Equal(1, calls);
        Assert.Equal(a.Id, b.ConsolidatedWithId);
    }

    [Fact]
    public async Task RunAsync_NieuweHogereTrustPrimaire_HerpuntBestaandeSecundairen_GeenKetens()
    {
        // Twee community-bronnen melden het event eerst (community1 wint als
        // primaire: gelijke trust, vroegste detectie); later meldt de
        // officiële bron hetzelfde event. Officieel moet dan de nieuwe
        // primaire worden — en community2 (al secundaire van community1)
        // moet mee-verhuizen naar de officiële bron, NIET aan community1
        // blijven hangen (dat zou een keten zijn: officieel <- community1 <-
        // community2).
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        var community1 = await SeedChangeAsync(
            db, "mobalytics", official: false, RulesHubDetectedAt, "Viktor banned.");
        var community2 = await SeedChangeAsync(
            db, "riftcodex-community", official: false, RulesHubDetectedAt.AddMinutes(10), "Viktor banned.");
        var svc = new ChangeConsolidationService(db, Ai(() => SameAnswer));
        var first = await svc.RunAsync();
        Assert.Equal(1, first.Merged);
        Assert.Equal(community1.Id, community2.ConsolidatedWithId);

        var official = await SeedChangeAsync(
            db, "rules-hub", official: true, RulesHubDetectedAt.AddMinutes(20), "Viktor banned.");
        var second = await new ChangeConsolidationService(db, Ai(() => SameAnswer)).RunAsync();

        Assert.Equal(1, second.Merged);
        Assert.Null(official.ConsolidatedWithId);
        Assert.Equal(official.Id, community1.ConsolidatedWithId);
        // Nooit ketens: community2 wijst nu rechtstreeks naar de nieuwe
        // wortel-primaire (official), niet meer naar community1.
        Assert.Equal(official.Id, community2.ConsolidatedWithId);
    }

    [Fact]
    public async Task RunAsync_MinderDanTweeChanges_DoetNiets()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Viktor");
        await SeedChangeAsync(db, "rules-hub", official: true, RulesHubDetectedAt, "Viktor banned.");
        var calls = 0;
        var svc = new ChangeConsolidationService(db, Ai(() => { calls++; return SameAnswer; }));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Merged);
        Assert.Equal(0, calls);
        Assert.Contains("minder dan twee", r.Message);
    }

    // --- testinfra (patroon RelationTriageServiceTests) --------------------

    private static async Task SeedCardAsync(RbRulesDbContext db, string riftboundId, string name)
    {
        db.Cards.Add(new Card { RiftboundId = riftboundId, Name = name });
        await db.SaveChangesAsync();
    }

    private static async Task<Change> SeedChangeAsync(
        RbRulesDbContext db, string sourceId, bool official, DateTimeOffset detectedAt, string summary)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == sourceId))
            db.Sources.Add(new Source
            {
                Id = sourceId, Name = sourceId, Url = $"https://example.test/{sourceId}",
                Type = official ? "official" : "community",
                TrustTier = official ? (short)1 : (short)3,
                Rank = 100, Parser = "html", Cadence = "daily",
            });
        var change = new Change
        {
            SourceId = sourceId, ChangeType = "ban", Summary = summary, DetectedAt = detectedAt,
        };
        db.Changes.Add(change);
        await db.SaveChangesAsync();
        return change;
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
            // MergeAsync gebruikt een echte transactie (multi-row); de
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
