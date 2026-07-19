using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Orkestratie-tests voor de brein-interactie-mining (#226, §3.1/§3.4).
/// Mock rb-ai (gestubde HTTP-handler op de echte RbAiClient) levert
/// tool-forced-achtige kandidaten; de test bewaakt de bedrading: extractie →
/// entity-resolutie (fase 1) VÓÓR kandidaat-creatie → fase-2-promotie-poort →
/// atomair feit+provenance. Plus de degradatie (rb-ai null → geen half feit, job
/// rondt netjes af). De poort-tiers zelf staan al in ReifiedInteractionTests; hier
/// gaat het om de koppeling.</summary>
public class BreinInteractionMiningServiceTests
{
    // ── Promotie + provenance (lexicale steun aanwezig) ──────────────────────
    [Fact]
    public async Task RunAsync_LexicaleSteun_PromoveertMetProvenanceEnConditie()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage during a Showdown. Assault deals damage.",
            ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = new[] { new { on_kind = "WINDOW", window = "Showdown", subject_role = "patient" } },
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.FocusCards);
        Assert.Equal(1, r.Extracted);
        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Failed);

        var ix = await db.Interactions.Include(x => x.Conditions).SingleAsync();
        Assert.Equal("mechanic:Deflect", ix.AgentRef);
        Assert.Equal("mechanic:Assault", ix.PatientRef);
        Assert.Equal("COUNTERS", ix.Kind);
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
        var cond = Assert.Single(ix.Conditions);
        Assert.Equal(InteractionConditionKinds.Window, cond.OnKind);
        Assert.Equal("Showdown", cond.Value);

        // Provenance (0a): het feit draagt een Assertion die naar de run + de bronkaart
        // wijst — atomair met het feit geschreven (rode draad #236).
        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal(BrainRef.Interaction(ix.Id).Format(), assertion.Subject);
        Assert.Equal(FactKinds.Interaction, assertion.FactKind);
        Assert.Equal("card:ogn-001", assertion.DerivedFromRef);
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(run.Id, assertion.MiningRunId);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(FactKinds.Interaction, run.Kind);
    }

    // ── Entity-resolutie VÓÓR kandidaat-creatie: synoniem → canonieke ref ────
    [Fact]
    public async Task RunAsync_ResolvetKeywordSynoniem_NaarCanoniekeRef_VoorCreatie()
    {
        using var db = NewDb();
        // Canonieke keyword-entiteit met een alias; de kaart draagt de aliasvorm.
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = "Deflect",
            AltLabels = ["Deflecting"], Status = CanonicalEntityStatus.Canonical,
            CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();
        await SeedCardAsync(db, "ogn-010", "Gamma", "Unit",
            "Deflecting prevents Assault damage.", ["Deflecting", "Assault"]);

        // rb-ai noemt de canonieke ref (die de service aanbood na resolutie).
        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Extracted);
        var ix = await db.Interactions.SingleAsync();
        // De aliasvorm "Deflecting" is naar de canonieke "Deflect" geresolveerd VÓÓR
        // de ref ontstond — geen tweede knoop (versla #2).
        Assert.Equal("mechanic:Deflect", ix.AgentRef);
    }

    // ── Cold-start: emergente card×card zonder steun → hypothese, niet weg ───
    [Fact]
    public async Task RunAsync_CardCardZonderSteun_ParkeertAlsHypothese()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit", "Some effect about combat.", ["Fury"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit", "Another effect entirely.", ["Fury"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-001", to = "card:ogn-002", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        // Eén focus-kaart zodat er precies één extractie-call is (deterministisch).
        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Hypothesized);
        Assert.Equal(0, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.ModelHypothesizedUnruled, ix.Status);
    }

    // ── Keyword-paar zonder lexicale steun → kandidaat (wacht op corroboratie) ─
    [Fact]
    public async Task RunAsync_KeywordPaarZonderSteun_WordtKandidaat()
    {
        using var db = NewDb();
        // Tekst bevat de keyword-labels NIET letterlijk ⇒ geen lexicale steun.
        await SeedCardAsync(db, "ogn-020", "Delta", "Unit", "It has some ability.", ["Snipe", "Tank"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Snipe", to = "mechanic:Tank", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Candidates);
        Assert.Equal(0, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Candidate, ix.Status);
    }

    // ── Degradatie: rb-ai null → geen half feit, job rondt netjes af ─────────
    [Fact]
    public async Task RunAsync_RbAiUitval_GeenHalfFeit_JobRondtNetjesAf()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = Service(db, () => null); // 500 → RbAiClient geeft null

        var r = await svc.RunAsync(); // mag NIET gooien

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Extracted);
        Assert.Empty(await db.Interactions.ToListAsync());
        Assert.Empty(await db.Assertions.ToListAsync());
        // De run is aangemaakt maar leeg — een geldige, lege attempt, geen half feit.
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(0, run.Verified);
    }

    // ── Idempotent: herhaald draaien maakt geen duplicaten ───────────────────
    [Fact]
    public async Task RunAsync_TweeKeer_GeenDuplicaat()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage during a Showdown. Assault deals damage.",
            ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        await svc.RunAsync();
        await svc.RunAsync();

        Assert.Equal(1, await db.Interactions.CountAsync());
    }

    // ── testinfra ─────────────────────────────────────────────────────────────

    private static BreinInteractionMiningService Service(RbRulesDbContext db, Func<string?> body) =>
        new(db, Ai(body), new EntityResolutionService(db), new InteractionPromotionService(db));

    private static string Interactions(params object[] items) =>
        JsonSerializer.Serialize(new { interactions = items });

    private static async Task SeedCardAsync(
        RbRulesDbContext db, string id, string name, string type, string text, string[] mechanics)
    {
        db.Cards.Add(new Card
        {
            RiftboundId = id, Name = name, Type = type, TextPlain = text, Mechanics = mechanics,
        });
        await db.SaveChangesAsync();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static RbAiClient Ai(Func<string?> body) => new(
        new HttpClient(new StubHandler(_ => body() is { } b
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(b, Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
}
