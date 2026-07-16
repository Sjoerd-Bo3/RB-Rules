using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van PathRunner (#190): de drain-loop (herhaalt tot
/// JobOutcome.Drained, met een max-herhalingen-vangrail én een no-progress-
/// guard), stop-bij-fout (de rest van het pad draait niet) en de
/// run_log/JobLedger-regels per stap (Kind=padnaam, Ref=stapnaam) — die
/// laatste via een EIGEN, verse scope per schrijfactie (review-fix #190:
/// nooit de mogelijk vervuilde context van een gefaalde stap). Gebruikt de
/// <c>findJob</c>-test-seam zodat geen enkele échte job (rb-ai/Ollama/Neo4j)
/// hoeft te draaien; de DbContext is scoped geregistreerd over een gedeelde
/// InMemory-store zodat verse scopes echt verse contexten geven.</summary>
public class PathRunnerTests
{
    [Fact]
    public async Task DrainStap_HerhaaltTotDrained_EnLogtElkeRunApart()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var calls = 0;
        var jobs = Catalog(("mine-stub", (sp, report, ct) =>
        {
            calls++;
            // Pas de derde aanroep drained.
            return Task.FromResult(new JobOutcome($"batch {calls}", Drained: calls >= 3));
        }));
        var path = new PathDefinition("test-pad", [new PathStep("mine-stub", Drain: true)]);

        var outcome = await PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs);

        Assert.Equal(3, calls);
        Assert.Contains("batch 3", outcome.Detail);
        using var db = NewDb(dbName);
        var logs = await db.RunLogs
            .Where(l => l.Kind == "test-pad")
            .OrderBy(l => l.Id)
            .ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, l =>
        {
            Assert.Equal("mine-stub", l.Ref);
            Assert.Equal("ok", l.Status);
        });
        Assert.Contains("run 1", logs[0].Detail);
        Assert.Contains("run 2", logs[1].Detail);
        Assert.Contains("run 3", logs[2].Detail);
    }

    [Fact]
    public async Task DrainStap_StoptOpVangrail_AlsHijBlijftStuiten()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var calls = 0;
        var jobs = Catalog(("never-drains", (sp, report, ct) =>
        {
            calls++;
            // Elke run een ánder detail (er is voortgang, alleen nooit
            // genoeg) — zo test dit de harde MaxRepeats-vangrail en niet de
            // no-progress-guard.
            return Task.FromResult(new JobOutcome($"poging {calls}", Drained: false));
        }));
        var path = new PathDefinition(
            "test-pad", [new PathStep("never-drains", Drain: true, MaxRepeats: 4)]);

        var outcome = await PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs);

        // Vangrail: nooit meer dan MaxRepeats pogingen, en het pad faalt er
        // niet op (de stap zelf gooide geen exception) — wel zichtbaar in de
        // samenvatting dat er nog werk kan liggen.
        Assert.Equal(4, calls);
        Assert.Contains("max 4 herhalingen bereikt", outcome.Detail);
    }

    [Fact]
    public async Task DrainStap_ZonderVoortgang_StoptNaTweeIdentiekeRuns_EnHetPadGaatDoor()
    {
        // No-progress-guard (review-fix #190): een stap die zijn per-run-
        // budget opeet zonder dat er iets verandert (bv. alleen al-bekende
        // items binnen de cap) geeft run na run exact hetzelfde resultaat —
        // dan is verder draineren verspilling. Twee identieke runs volstaan
        // als bewijs; de rest van het pad draait gewoon door.
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var stuckCalls = 0;
        var nextStepRan = false;
        var jobs = Catalog(
            ("stuck", (sp, report, ct) =>
            {
                stuckCalls++;
                return Task.FromResult(new JobOutcome("0 nieuw, cap bereikt", Drained: false));
            }),
            ("daarna", (sp, report, ct) =>
            {
                nextStepRan = true;
                return Task.FromResult(new JobOutcome("klaar"));
            }));
        var path = new PathDefinition("test-pad",
            [new PathStep("stuck", Drain: true, MaxRepeats: 10), new PathStep("daarna")]);

        var outcome = await PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs);

        Assert.Equal(2, stuckCalls); // niet 10: de guard stopt na de tweede identieke run
        Assert.True(nextStepRan);
        Assert.Contains("drain gestopt na run 2: geen voortgang", outcome.Detail);

        using var db = NewDb(dbName);
        var guard = await db.RunLogs.SingleAsync(
            l => l.Kind == "test-pad" && l.Status == "info");
        Assert.Equal("stuck", guard.Ref);
        Assert.Contains("geen voortgang", guard.Detail);
    }

    [Fact]
    public async Task NietDrainStap_DraaitPreciesEenKeer_OokAlsDrainedFalseIs()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var calls = 0;
        var jobs = Catalog(("eenmalig", (sp, report, ct) =>
        {
            calls++;
            return Task.FromResult(new JobOutcome("klaar", Drained: false));
        }));
        var path = new PathDefinition("test-pad", [new PathStep("eenmalig")]); // Drain: false (default)

        await PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FaltStap_StoptPad_DeRestDraaitNiet_EnLogtDeFout()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var laatsteStapGedraaid = false;
        var jobs = Catalog(
            ("stap-een", (sp, report, ct) => Task.FromResult(new JobOutcome("ok"))),
            ("stap-twee-faalt", (sp, report, ct) =>
                throw new InvalidOperationException("rb-ai onbereikbaar")),
            ("stap-drie", (sp, report, ct) =>
            {
                laatsteStapGedraaid = true;
                return Task.FromResult(new JobOutcome("ok"));
            }));
        var path = new PathDefinition("test-pad",
            [new PathStep("stap-een"), new PathStep("stap-twee-faalt"), new PathStep("stap-drie")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs));

        Assert.Equal("rb-ai onbereikbaar", ex.Message);
        Assert.False(laatsteStapGedraaid);

        using var db = NewDb(dbName);
        var logs = await db.RunLogs.Where(l => l.Kind == "test-pad").OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, logs.Count); // stap-een (ok) + stap-twee-faalt (error) — stap-drie draaide niet
        Assert.Equal("stap-een", logs[0].Ref);
        Assert.Equal("ok", logs[0].Status);
        Assert.Equal("stap-twee-faalt", logs[1].Ref);
        Assert.Equal("error", logs[1].Status);
        Assert.Contains("rb-ai onbereikbaar", logs[1].Detail);
    }

    [Fact]
    public async Task FaltStap_MetVervuildeContext_LogtErrorViaVerseScope_EnPropageertDeEchteFout()
    {
        // Review-fix #190: een stap die zijn (gedeelde, scoped) DbContext
        // vervuilt — half werk in de change-tracker, waaronder een entiteit
        // die SaveChanges laat crashen — en dán faalt, mag twee dingen niet
        // veroorzaken: (1) dat de error-regel verloren gaat of het halve werk
        // alsnog gecommit wordt (PathRunner logt via een eigen, verse scope),
        // en (2) dat een log-exceptie de oorspronkelijke stap-fout maskeert.
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var jobs = Catalog(("vervuilt-en-crasht", (stepSp, report, ct) =>
        {
            var stepDb = stepSp.GetRequiredService<RbRulesDbContext>();
            // Half werk mét een vergiftigde rij: Status (required) ontbreekt,
            // dus een SaveChanges op déze context zou een DbUpdateException
            // gooien — precies de maskering die de verse-scope-log voorkomt.
            stepDb.RunLogs.Add(new RunLog
            {
                Kind = "half-werk", Ref = "x", Status = null!, Detail = "mag nooit landen",
            });
            throw new InvalidOperationException("de echte fout");
        }));
        var path = new PathDefinition("test-pad", [new PathStep("vervuilt-en-crasht")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs));

        // De oorspronkelijke fout is de zichtbare — geen DbUpdateException.
        Assert.Equal("de echte fout", ex.Message);

        using var db = NewDb(dbName);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "test-pad");
        Assert.Equal("error", error.Status);
        Assert.Contains("de echte fout", error.Detail);
        // Het halve werk van de gefaalde stap is niet meegecommit.
        Assert.False(await db.RunLogs.AnyAsync(l => l.Kind == "half-werk"));
    }

    [Fact]
    public async Task OnbekendeStap_GooitMeteen_ZonderEnigeJobTeDraaien()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var jobs = Catalog(); // lege catalogus
        var path = new PathDefinition("test-pad", [new PathStep("bestaat-niet")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs));

        using var db = NewDb(dbName);
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    // --- testinfra ---------------------------------------------------------

    private static Func<string, JobDefinition?> Catalog(
        params (string Name, Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> Run)[] entries)
    {
        var byName = entries.ToDictionary(e => e.Name, e => new JobDefinition(e.Name, e.Run));
        return name => byName.GetValueOrDefault(name);
    }

    /// <summary>Scoped DbContext over een gedeelde InMemory-store (zelfde
    /// databasenaam = zelfde data): PathRunner's verse scopes krijgen zo écht
    /// een verse context — precies wat de review-fix borgt.</summary>
    private static ServiceProvider NewSp(string dbName) =>
        new ServiceCollection()
            .AddScoped<RbRulesDbContext>(_ => NewDb(dbName))
            .BuildServiceProvider();

    private static RbRulesDbContext NewDb(string dbName) => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    /// <summary>InMemory kent het pgvector-type niet (patroon JobLedgerTests).</summary>
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
