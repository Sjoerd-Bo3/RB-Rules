using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#279 — de brein-mining draait de per-kaart/per-subject-lus parallel.
/// De winst is puur wandkloktijd (~40s rb-ai per kaart; 900 kaarten sequentieel is
/// tien uur), maar de risico's zitten ergens anders: <c>DbContext</c> is niet
/// thread-safe, en twee kaarten kunnen dezelfde interactie voorstellen terwijl de
/// promotie-poort lees-dan-schrijf doet op een UNIEKE sleutel.
///
/// Deze tests leggen dus drie dingen vast die je niet mag aannemen:
/// <list type="number">
/// <item>er draaien écht meerdere extracties tegelijk (aantoonbaar, niet "de code
/// ziet er parallel uit") — en zonder factory blijft het exact sequentieel;</item>
/// <item>elke worker heeft een eigen context, en gelijktijdige verwerking levert
/// geen dubbele én geen verloren feiten;</item>
/// <item>de bestaande semantiek (watermark, deadline, uitval-telling) overleeft de
/// parallellisatie.</item>
/// </list></summary>
public class BreinMiningParallelTests
{
    // ── Draait het écht gelijktijdig? ────────────────────────────────────────

    [Fact]
    public async Task Interacties_MetFactory_DraaienEchtGelijktijdig()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");
        await SeedCardAsync(db, "ogn-003", "Gamma");

        // De probe laat elke aanvraag pas door als er drie tegelijk binnen zijn:
        // een sequentiële lus haalt dat nooit en valt op de timeout terug.
        var probe = new ConcurrencyProbe(expected: 3);
        var svc = InteractionService(db, probe, () => Interactions(DeflectCountersAssault), workers: 3);

        var r = await svc.RunAsync();

        Assert.Equal(3, probe.MaxInFlight);
        Assert.Equal(3, r.FocusCards);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public async Task Interacties_ZonderFactory_BlijvenSequentieel()
    {
        // Zonder IDbContextFactory is er maar één (gedeelde) context: parallel
        // draaien zou dan geen optimalisatie zijn maar corruptie van de
        // change-tracker. De instelling staat bewust op 3 — de factory-check moet
        // hem overrulen.
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");

        var probe = new ConcurrencyProbe(expected: 1);
        var svc = InteractionService(
            db, probe, () => Interactions(DeflectCountersAssault), workers: 3, factory: null);

        var r = await svc.RunAsync();

        Assert.Equal(1, probe.MaxInFlight);
        Assert.Equal(2, r.FocusCards);
    }

    [Fact]
    public async Task Interacties_ElkeKaartKrijgtEenEigenContext()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");
        await SeedCardAsync(db, "ogn-003", "Gamma");

        var factory = Factory(db);
        var probe = new ConcurrencyProbe(expected: 3);
        var svc = InteractionService(
            db, probe, () => Interactions(DeflectCountersAssault), workers: 3, factory: factory);

        await svc.RunAsync();

        // Eén verse context per kaart: elke kaart is zijn eigen unit-of-work, en de
        // gedeelde scoped context wordt door geen enkele worker aangeraakt.
        Assert.Equal(3, factory.Created);
    }

    // ── Geen dubbele, geen verloren feiten ───────────────────────────────────

    [Fact]
    public async Task Interacties_DriedKaartenZelfdePaar_LeverenEenFeitMetProvenancePerKaart()
    {
        // Dit is precies het geval waarop een naïeve parallellisatie stukloopt: alle
        // drie de kaarten dragen [Deflect] én [Assault], dus alle drie stellen ze
        // hetzelfde paar voor. De poort doet lees-dan-schrijf op een unieke index
        // (AgentRef, PatientRef, Kind) — zonder serialisatie concluderen twee workers
        // allebei "bestaat nog niet" en knalt de tweede op een unique-violation.
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");
        await SeedCardAsync(db, "ogn-003", "Gamma");
        // Regeltekst-bewijs zodat het mech↔mech-paar blijft PROMOVEREN (#324):
        // kaarttekst alleen zou het paar tot kandidaat maken, en deze test gaat
        // juist over drie gelijktijdige promoties op dezelfde unieke sleutel.
        await SeedDeflectRuleAsync(db);

        var probe = new ConcurrencyProbe(expected: 3);
        var svc = InteractionService(db, probe, () => Interactions(DeflectCountersAssault), workers: 3);

        var r = await svc.RunAsync();

        Assert.Equal(3, probe.MaxInFlight);   // het racende geval is écht opgetreden
        Assert.Equal(3, r.Extracted);
        Assert.Equal(3, r.Promoted);

        // GEEN duplicaten: één feit op de sleutel.
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("mechanic:Deflect", ix.AgentRef);
        Assert.Equal("mechanic:Assault", ix.PatientRef);
        Assert.Equal(InteractionStatus.Promoted, ix.Status);

        // GEEN verloren feiten: elke kaart liet haar eigen provenance achter.
        var derivedFrom = await db.Assertions
            .Where(a => a.FactKind == FactKinds.Interaction)
            .Select(a => a.DerivedFromRef)
            .ToListAsync();
        Assert.Equal(3, derivedFrom.Count);
        Assert.Equal(
            ["card:ogn-001", "card:ogn-002", "card:ogn-003"],
            derivedFrom.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Interacties_ParallelleRun_ZetHetWatermarkOpElkeVerwerkteKaart()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");
        await SeedCardAsync(db, "ogn-003", "Gamma");

        var probe = new ConcurrencyProbe(expected: 3);
        var svc = InteractionService(db, probe, () => Interactions(DeflectCountersAssault), workers: 3);

        await svc.RunAsync();

        // Het watermark (#273) is wat de volgorde-onafhankelijkheid draagt: zonder
        // markering herkauwt de volgende run dezelfde kaarten. Het wordt door de
        // worker-context geschreven, dus de gedeelde context mag er niet voor nodig
        // zijn.
        var run = await db.MiningRuns.SingleAsync();
        var cards = await db.Cards.AsNoTracking().OrderBy(c => c.RiftboundId).ToListAsync();
        Assert.All(cards, c =>
        {
            Assert.NotNull(c.InteractionsMinedAt);
            Assert.Equal(run.Id, c.InteractionsMinedByRunId);
        });

        // Een tweede run vindt niets meer te doen — idempotent, ook parallel.
        var again = await InteractionService(
            db, new ConcurrencyProbe(1), () => Interactions(DeflectCountersAssault), workers: 3)
            .RunAsync();
        Assert.Equal(0, again.FocusCards);
    }

    [Fact]
    public async Task Interacties_ParallelleUitval_TeltPerOorzaak_ZonderWatermark()
    {
        // Degradatie moet ook parallel het verwachte pad blijven: geen half feit, en
        // de kaart komt terug (géén watermark) — anders verdwijnt ze stil uit de
        // wachtrij omdat een worker toevallig een 429 ving.
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");

        var probe = new ConcurrencyProbe(expected: 2);
        var svc = InteractionService(db, probe, () => null, workers: 2);   // null → 500

        var r = await svc.RunAsync();

        Assert.Equal(2, r.Failed);
        Assert.Equal("5xx×2", r.FailureDetail);
        Assert.Empty(await db.Interactions.ToListAsync());
        Assert.All(await db.Cards.AsNoTracking().ToListAsync(),
            c => Assert.Null(c.InteractionsMinedAt));
    }

    [Fact]
    public async Task Interacties_DeadlineVerstreken_StoptOokParallel()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha");
        await SeedCardAsync(db, "ogn-002", "Beta");

        var svc = InteractionService(
            db, new ConcurrencyProbe(1), () => Interactions(DeflectCountersAssault), workers: 2);

        var r = await svc.RunAsync(deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Elke worker stopt bij zijn eerstvolgende kaart; er blijft vers werk liggen.
        Assert.Equal(0, r.FocusCards);
        Assert.True(r.CapHit);
        Assert.Empty(await db.Interactions.ToListAsync());
    }

    // ── Predicaten: zelfde lus, eigen context per subject ────────────────────

    [Fact]
    public async Task Predicaten_MetFactory_DraaienEchtGelijktijdig_ZonderVerlies()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Accelerate", "Accelerate prevents exhaust.");
        await SeedMechanicAsync(db, "Bastion", "Bastion prevents exhaust.");
        await SeedMechanicAsync(db, "Deflect", "Deflect prevents exhaust.");

        var factory = Factory(db);
        var probe = new ConcurrencyProbe(expected: 3);
        var svc = new BreinPredicateMiningService(
            db, ProbedAi(probe, () => Predicates(new { predicate = "prevents", @object = "exhaust" })),
            factory, new BreinMiningSettings(3));

        var r = await svc.RunAsync();

        Assert.Equal(3, probe.MaxInFlight);
        Assert.Equal(3, r.Subjects);
        Assert.Equal(3, r.Mined);
        Assert.Equal(0, r.Failed);
        Assert.Equal(3, factory.Created);   // eigen context per subject

        // Eén predicaat per subject: de dedupe-sleutel begint bij het subject, en elk
        // subject wordt door precies één worker opgepakt — dus geen verlies, geen
        // duplicaten, ondanks dezelfde predicate/object-combinatie.
        var subjects = await db.MechanicPredicates.Select(p => p.SubjectEntityId).ToListAsync();
        Assert.Equal(3, subjects.Count);
        Assert.Equal(3, subjects.Distinct().Count());
    }

    [Fact]
    public async Task Predicaten_ZonderFactory_BlijvenSequentieel()
    {
        using var db = NewDb();
        await SeedMechanicAsync(db, "Accelerate", "Accelerate prevents exhaust.");
        await SeedMechanicAsync(db, "Bastion", "Bastion prevents exhaust.");

        var probe = new ConcurrencyProbe(expected: 1);
        var svc = new BreinPredicateMiningService(
            db, ProbedAi(probe, () => Predicates(new { predicate = "prevents", @object = "exhaust" })),
            dbFactory: null, settings: new BreinMiningSettings(3));

        var r = await svc.RunAsync();

        Assert.Equal(1, probe.MaxInFlight);
        Assert.Equal(2, r.Mined);
    }

    // ── Instellingen ─────────────────────────────────────────────────────────

    [Fact]
    public void Instellingen_DefaultSpiegeltDeAchtergrondDeelcapVanRbAi()
    {
        // 3 = AI_MAX_CONCURRENCY (5) − AI_INTERACTIVE_RESERVE (2). Precies zoveel
        // workers als er achtergrond-permits zijn: geen wachtrij, en /ask houdt per
        // constructie slots over. Loopt dit uit de pas, dan meet de mining haar eigen
        // cap als uitval.
        Assert.Equal(3, BreinMiningSettings.Default.Concurrency);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1", 1)]
    [InlineData("", 3)]            // niet gezet → default
    [InlineData("nonsens", 3)]     // typfout mag de mining niet ontregelen
    [InlineData("0", 3)]           // 0 zou de mining stilleggen
    [InlineData("-2", 3)]
    [InlineData("300", 3)]         // boven de bovengrens → default, geen 300 sessies
    public void Instellingen_LezenUitEnv_ValtVeiligTerug(string value, int expected)
    {
        var previous = Environment.GetEnvironmentVariable("BREIN_MINING_CONCURRENCY");
        try
        {
            Environment.SetEnvironmentVariable(
                "BREIN_MINING_CONCURRENCY", value.Length == 0 ? null : value);
            Assert.Equal(expected, BreinMiningSettings.FromEnvironment().Concurrency);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BREIN_MINING_CONCURRENCY", previous);
        }
    }

    // ── testinfra ────────────────────────────────────────────────────────────

    private const string DeflectText =
        "Deflect prevents Assault damage during a Showdown. Assault deals damage.";

    private static object DeflectCountersAssault => new
    {
        from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
        conditions = Array.Empty<object>(),
    };

    /// <summary>Laat elke rb-ai-aanvraag pas door zodra er <c>expected</c> tegelijk
    /// binnen zijn. Dat maakt "het draait parallel" toetsbaar in plaats van
    /// aannemelijk: een sequentiële lus krijgt er nooit meer dan één tegelijk en valt
    /// op de timeout terug (de test faalt dan op <see cref="MaxInFlight"/>, hij hangt
    /// niet).</summary>
    private sealed class ConcurrencyProbe(int expected)
    {
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _inFlight;

        public int MaxInFlight { get; private set; }

        public async Task EnterAsync()
        {
            lock (_gate)
            {
                _inFlight++;
                if (_inFlight > MaxInFlight) MaxInFlight = _inFlight;
                if (_inFlight >= expected) _allArrived.TrySetResult();
            }
            await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        public void Exit()
        {
            lock (_gate) _inFlight--;
        }
    }

    private static BreinInteractionMiningService InteractionService(
        RbRulesDbContext db, ConcurrencyProbe probe, Func<string?> body, int workers) =>
        InteractionService(db, probe, body, workers, Factory(db));

    private static BreinInteractionMiningService InteractionService(
        RbRulesDbContext db, ConcurrencyProbe probe, Func<string?> body, int workers,
        IDbContextFactory<RbRulesDbContext>? factory) =>
        new(db, ProbedAi(probe, body), new EntityResolutionService(db),
            new InteractionPromotionService(db), factory, new BreinMiningSettings(workers));

    /// <summary>rb-ai-stub die door de probe heen loopt; <c>null</c> als body staat
    /// voor uitval (500), zoals in de bestaande mining-tests.</summary>
    private static RbAiClient ProbedAi(ConcurrencyProbe probe, Func<string?> body) =>
        new(new HttpClient(new AsyncStubHandler(async () =>
        {
            await probe.EnterAsync();
            try
            {
                return body() is { } b
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(b, Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            finally
            {
                probe.Exit();
            }
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

    private sealed class AsyncStubHandler(Func<Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond();
    }

    /// <summary>Factory die verse InMemory-contexten op dezelfde store maakt (zelfde
    /// options-instantie ⇒ zelfde store) — het testequivalent van de productie-
    /// <c>IDbContextFactory</c> op Npgsql; telt hoeveel contexten er gemaakt zijn.</summary>
    private static CountingDbFactory Factory(RbRulesDbContext db) =>
        new((DbContextOptions<RbRulesDbContext>)db.GetService<IDbContextOptions>());

    private sealed class CountingDbFactory(DbContextOptions<RbRulesDbContext> options)
        : IDbContextFactory<RbRulesDbContext>
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public RbRulesDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _created);
            return new InMemoryDbContext(options);
        }
    }

    private static string Interactions(params object[] items) =>
        JsonSerializer.Serialize(new { interactions = items });

    private static string Predicates(params object[] items) =>
        JsonSerializer.Serialize(new { predicates = items });

    private static async Task SeedCardAsync(RbRulesDbContext db, string id, string name)
    {
        db.Cards.Add(new Card
        {
            RiftboundId = id, Name = name, Type = "Unit", TextPlain = DeflectText,
            Mechanics = ["Deflect", "Assault"],
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Trust-tier-1-regelsectie met de Deflect↔Assault-bewijszin: sinds
    /// #324 promoveert een mech↔mech-paar alleen op regel-/definitietekst.</summary>
    private static async Task SeedDeflectRuleAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = "core-rules-pdf", Name = "core-rules-pdf",
            Url = "https://playriftbound.com/core-rules-pdf",
            Type = "official", TrustTier = 1, Parser = "pdf", Cadence = "daily",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            SourceId = "core-rules-pdf", SectionCode = "704.1", ChunkIndex = 1,
            Text = DeflectText,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedMechanicAsync(
        RbRulesDbContext db, string label, string definition)
    {
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Mechanic, CanonicalLabel = label, AltLabels = [],
            Definition = definition, Status = CanonicalEntityStatus.Canonical,
            CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();
    }

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
