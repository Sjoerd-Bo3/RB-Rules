using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van PathRunner (#190): de drain-loop (herhaalt tot
/// JobOutcome.Drained, met een max-herhalingen-vangrail), stop-bij-fout (de
/// rest van het pad draait niet) en de run_log/JobLedger-regels per stap
/// (Kind=padnaam, Ref=stapnaam). Gebruikt de <c>findJob</c>-test-seam zodat
/// geen enkele échte job (rb-ai/Ollama/Neo4j) hoeft te draaien — de stap-
/// definities verwijzen naar verzonnen jobnamen die alleen in de lokale
/// gestubde catalogus bestaan.</summary>
public class PathRunnerTests
{
    [Fact]
    public async Task DrainStap_HerhaaltTotDrained_EnLogtElkeRunApart()
    {
        using var db = NewDb();
        var sp = NewSp(db);
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
        using var db = NewDb();
        var sp = NewSp(db);
        var calls = 0;
        var jobs = Catalog(("never-drains", (sp, report, ct) =>
        {
            calls++;
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
    public async Task NietDrainStap_DraaitPreciesEenKeer_OokAlsDrainedFalseIs()
    {
        using var db = NewDb();
        var sp = NewSp(db);
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
        using var db = NewDb();
        var sp = NewSp(db);
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

        var logs = await db.RunLogs.Where(l => l.Kind == "test-pad").OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, logs.Count); // stap-een (ok) + stap-twee-faalt (error) — stap-drie draaide niet
        Assert.Equal("stap-een", logs[0].Ref);
        Assert.Equal("ok", logs[0].Status);
        Assert.Equal("stap-twee-faalt", logs[1].Ref);
        Assert.Equal("error", logs[1].Status);
        Assert.Contains("rb-ai onbereikbaar", logs[1].Detail);
    }

    [Fact]
    public async Task OnbekendeStap_GooitMeteen_ZonderEnigeJobTeDraaien()
    {
        using var db = NewDb();
        var sp = NewSp(db);
        var jobs = Catalog(); // lege catalogus
        var path = new PathDefinition("test-pad", [new PathStep("bestaat-niet")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, CancellationToken.None, jobs));

        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    // --- testinfra ---------------------------------------------------------

    private static Func<string, JobDefinition?> Catalog(
        params (string Name, Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> Run)[] entries)
    {
        var byName = entries.ToDictionary(e => e.Name, e => new JobDefinition(e.Name, e.Run));
        return name => byName.GetValueOrDefault(name);
    }

    private static IServiceProvider NewSp(RbRulesDbContext db) =>
        new ServiceCollection().AddSingleton(db).BuildServiceProvider();

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
