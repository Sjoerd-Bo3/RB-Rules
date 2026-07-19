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

/// <summary>Orkestratie-tests voor de brein-predicaat-mining (#226/#229, §5). Mock
/// rb-ai levert getypeerde predicaten; de test bewaakt dat ze als candidate (ter
/// review, nooit LLM-alleen gepromoveerd) met 0a-provenance worden vastgelegd,
/// gededupliceerd op de sleutel, en dat rb-ai-uitval geen half feit achterlaat.</summary>
public class BreinPredicateMiningServiceTests
{
    [Fact]
    public async Task RunAsync_MintPredicaten_AlsCandidate_MetProvenance()
    {
        using var db = NewDb();
        var subject = await SeedMechanicAsync(db, "Accelerate");
        await SeedCardAsync(db, "ogn-001", "Speedster",
            "Accelerate: your units do not exhaust when moving to a Showdown.", ["Accelerate"]);

        var svc = Service(db, () => Predicates(new { predicate = "prevents", @object = "exhaust" }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Subjects);
        Assert.Equal(1, r.Mined);
        Assert.Equal(0, r.Failed);

        var pred = await db.MechanicPredicates.SingleAsync();
        Assert.Equal(subject.Id, pred.SubjectEntityId);
        Assert.Equal(MechanicPredicateKinds.Prevents, pred.Predicate);
        Assert.Equal("exhaust", pred.ObjectToken);
        Assert.Equal(MechanicPredicateStatus.Candidate, pred.Status); // nooit LLM-alleen gepromoveerd
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(run.Id, pred.CreatedByRunId); // 0a-provenance
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task RunAsync_GebruiktDefinitieAlsBewijs_ZonderKaart()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank to a friendly unit.");

        var svc = Service(db, () => Predicates(new { predicate = "grants", @object = "tank" }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Subjects);
        Assert.Equal(1, r.Mined);
        var pred = await db.MechanicPredicates.SingleAsync();
        Assert.Equal(MechanicPredicateKinds.Grants, pred.Predicate);
        Assert.Equal("tank", pred.ObjectToken);
    }

    [Fact]
    public async Task RunAsync_ZonderBewijstekst_SlaatSubjectOver()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Mystery"); // geen definitie, geen kaart

        var svc = Service(db, () => Predicates(new { predicate = "grants", @object = "tank" }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Subjects);
        Assert.Equal(0, r.Mined);
        Assert.Equal(1, r.Skipped);
        Assert.Empty(await db.MechanicPredicates.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_RbAiUitval_GeenHalfFeit()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank.");

        var svc = Service(db, () => null); // 500 → null

        var r = await svc.RunAsync(); // mag NIET gooien

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Mined);
        Assert.Empty(await db.MechanicPredicates.ToListAsync());
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(0, run.Verified);
    }

    [Fact]
    public async Task RunAsync_TweeKeer_GeenDuplicaat_SubjectAlGepredikeerd()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank.");

        var svc = Service(db, () => Predicates(new { predicate = "grants", @object = "tank" }));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        Assert.Equal(1, first.Mined);
        // Tweede run: het subject draagt al een predicaat ⇒ uit de selectie (geen
        // stille her-run-kosten), dus geen nieuwe subjecten.
        Assert.Equal(0, second.Subjects);
        Assert.Equal(1, await db.MechanicPredicates.CountAsync());
    }

    [Fact]
    public async Task RunAsync_DubbelPredicaatInResponse_LegtSlechtsEenRijAan()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank to a friendly unit.");

        // rb-ai levert hetzelfde (predicate, object) tweemaal + één afwijkend: de
        // dedupe (parser-muur + service-sleutel) mag de duplicaat NOOIT als tweede rij
        // op de unieke dedupe-sleutel binnenlaten; het afwijkende predicaat wél.
        var svc = Service(db, () => Predicates(
            new { predicate = "grants", @object = "tank" },
            new { predicate = "grants", @object = "tank" },
            new { predicate = "prevents", @object = "exhaust" }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Subjects);
        Assert.Equal(2, r.Mined); // de duplicaat is samengevallen, het afwijkende blijft
        Assert.Equal(2, await db.MechanicPredicates.CountAsync());
        var keys = await db.MechanicPredicates
            .Select(p => p.Predicate + "|" + p.ObjectToken).ToListAsync();
        Assert.Equal(keys.Count, keys.Distinct().Count()); // geen dubbele dedupe-sleutel
    }

    [Fact]
    public async Task RunAsync_AiLeegResultaat_GeenUitval_GeenFeit()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank.");

        // 200 met een lege predicaat-lijst: een geldige, lege attempt — GEEN degradatie
        // (te onderscheiden van 500/null → Failed++).
        var svc = Service(db, () => Predicates());

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Subjects);
        Assert.Equal(0, r.Mined);
        Assert.Equal(0, r.Failed); // niet als uitval geteld
        Assert.Empty(await db.MechanicPredicates.ToListAsync());
        var run = await db.MiningRuns.SingleAsync();
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(0, run.Verified);
    }

    // ── Nachtrun-deadline (#245) ───────────────────────────────────────────────
    [Fact]
    public async Task RunAsync_DeadlineVerstreken_StoptDirect_GeenPredicaat_MeerWerk()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Bastion", definition: "Bastion grants Tank to a friendly unit.");
        // rb-ai zou een predicaat minen, maar de deadline is al verstreken: de lus
        // breekt vóór de eerste rb-ai-aanroep — geen predicaat, en er blijft werk liggen.
        var svc = Service(db, () => Predicates(new { predicate = "grants", @object = "Tank" }));

        var r = await svc.RunAsync(maxSubjects: NightlyWindow.UncappedItems,
            deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(0, r.Subjects);  // niets verwerkt
        Assert.True(r.CapHit);        // deadline afgekapt ⇒ vers werk blijft liggen
        Assert.Empty(await db.MechanicPredicates.ToListAsync());
    }

    // ── testinfra ─────────────────────────────────────────────────────────────

    private static BreinPredicateMiningService Service(RbRulesDbContext db, Func<string?> body) =>
        new(db, Ai(body));

    private static string Predicates(params object[] items) =>
        JsonSerializer.Serialize(new { predicates = items });

    private static async Task<CanonicalEntity> SeedMechanicAsync(
        RbRulesDbContext db, string label, string? definition = null)
    {
        var e = new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Mechanic, CanonicalLabel = label, AltLabels = [],
            Definition = definition, Status = CanonicalEntityStatus.Canonical,
            CreatedByRunId = Ulid.NewUlid(),
        };
        db.CanonicalEntities.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task SeedCardAsync(
        RbRulesDbContext db, string id, string name, string text, string[] mechanics)
    {
        db.Cards.Add(new Card { RiftboundId = id, Name = name, TextPlain = text, Mechanics = mechanics });
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
