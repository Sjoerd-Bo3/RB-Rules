using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Api;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Afbreken van een lopende job (#253). De productiebug: er was geen
/// annulering (het werk kreeg <c>CancellationToken.None</c>), dus de enige uitweg
/// was <c>docker restart</c> — die schrijft géén run_log-afronding, waarna de
/// scheduler de nachtrun meteen opnieuw startte. Deze tests bewaken daarom
/// vooral dát er een afrondingsregel komt (status "cancelled"), dat de
/// éénjob-gate weer vrijgeeft (running == null), en dat afbreken zonder
/// lopende job netjes niets doet.</summary>
public class JobRunnerCancelTests
{
    [Fact]
    public async Task Afbreken_StoptDeRun_SchrijftCancelledInRunLog_EnGeeftDeGateVrij()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var runner = NewRunner(sp);

        var gestart = new TaskCompletionSource();
        var started = runner.TryStart("langlopend", async (_, report, ct) =>
        {
            report("stap 1 van 3");
            gestart.TrySetResult();
            // Coöperatief: precies wat de echte services doen — de token gaat
            // door naar EF/HTTP en gooit daar OperationCanceledException.
            await Task.Delay(Timeout.Infinite, ct);
            return new JobOutcome("nooit bereikt");
        });
        Assert.True(started);
        await gestart.Task;

        var cancelling = runner.TryCancel();
        Assert.NotNull(cancelling);
        Assert.Equal("langlopend", cancelling.Name);
        Assert.True(cancelling.CancelRequested);

        var (running, last) = await WaitForFinishAsync(runner);

        // De gate is weer vrij: een volgende job kan meteen starten.
        Assert.Null(running);
        Assert.NotNull(last);
        Assert.Equal("cancelled", last.Status);
        Assert.Equal("langlopend", last.Name);
        // De bereikte voortgang blijft zichtbaar in het detail.
        Assert.Contains("stap 1 van 3", last.Detail);

        // Cruciaal (#253): de afrondingsregel in het grootboek. Zonder deze
        // regel denkt de scheduler dat de job nog niet draaide vandaag.
        await using var db = NewDb(dbName);
        var log = await db.RunLogs.SingleAsync(l => l.Kind == "job" && l.Ref == "langlopend");
        Assert.Equal("cancelled", log.Status);
        Assert.Contains("afgebroken via beheer", log.Detail);

        // En de scheduler ziet het venster ook echt gevuld: JobLedger is
        // status-agnostisch, dus een cancelled-run telt als "heeft gedraaid".
        Assert.NotNull(await new JobLedger(db).LastRunAsync("langlopend"));
    }

    [Fact]
    public async Task Afbreken_ZonderLopendeJob_DoetNiets()
    {
        await using var sp = NewSp(Guid.NewGuid().ToString());
        var runner = NewRunner(sp);

        // Net gedrag: geen exception, geen half-afgemaakte staat — de endpoint
        // maakt hier een 200 met cancelled:false van, geen 500.
        Assert.Null(runner.TryCancel());
        Assert.Equal((null, null), runner.Snapshot());
    }

    [Fact]
    public async Task NieuweJobKanStarten_NaEenAfbreking()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var runner = NewRunner(sp);

        var gestart = new TaskCompletionSource();
        runner.TryStart("langlopend", async (_, _, ct) =>
        {
            gestart.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new JobOutcome("nooit bereikt");
        });
        await gestart.Task;
        // Zolang hij draait houdt de éénjob-gate een tweede job tegen.
        Assert.False(runner.TryStart("volgende", (_, _, _) => Task.FromResult(new JobOutcome("ok"))));

        runner.TryCancel();
        await WaitForFinishAsync(runner);

        Assert.True(runner.TryStart("volgende", (_, _, _) => Task.FromResult(new JobOutcome("klaar"))));
        var (_, last) = await WaitForFinishAsync(runner);
        Assert.Equal("ok", last!.Status);
        Assert.Equal("volgende", last.Name);
    }

    [Fact]
    public async Task WerkKrijgtEenEchteToken_GeenNone()
    {
        // Regressie op de kern van #253: JobRunner.cs gaf CancellationToken.None
        // door, waardoor annuleren per ontwerp onmogelijk was.
        await using var sp = NewSp(Guid.NewGuid().ToString());
        var runner = NewRunner(sp);

        var token = new TaskCompletionSource<CancellationToken>();
        runner.TryStart("token-check", (_, _, ct) =>
        {
            token.TrySetResult(ct);
            return Task.FromResult(new JobOutcome("ok"));
        });

        var ct = await token.Task;
        Assert.True(ct.CanBeCanceled);
        await WaitForFinishAsync(runner);
    }

    [Fact]
    public async Task GewoneFout_BlijftError_EnWordtNietAlsAfbrekingGeteld()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        var runner = NewRunner(sp);

        runner.TryStart("faalt", (_, _, _) =>
            throw new InvalidOperationException("rb-ai onbereikbaar"));

        var (_, last) = await WaitForFinishAsync(runner);
        Assert.Equal("error", last!.Status);
        Assert.Equal("rb-ai onbereikbaar", last.Detail);

        await using var db = NewDb(dbName);
        var log = await db.RunLogs.SingleAsync(l => l.Kind == "job" && l.Ref == "faalt");
        Assert.Equal("error", log.Status);
    }

    [Fact]
    public async Task Pad_Afgebroken_LogtDeStapAlsCancelled_EnDraaitDeRestNiet()
    {
        // PathRunner-kant (#253): het pad stopt op zijn stap-breekpunt, de
        // stap-regel landt nog (log schrijft bewust zonder token) en latere
        // stappen draaien niet.
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        using var cts = new CancellationTokenSource();
        var stapTweeGedraaid = false;
        var jobs = Catalog(
            ("stap-een", (_, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(new JobOutcome("ok"));
            }),
            ("stap-twee", (_, _, _) =>
            {
                stapTweeGedraaid = true;
                return Task.FromResult(new JobOutcome("ok"));
            }));
        var path = new PathDefinition("test-pad", [new PathStep("stap-een"), new PathStep("stap-twee")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, cts.Token, jobs));

        Assert.False(stapTweeGedraaid);
        await using var db = NewDb(dbName);
        var logs = await db.RunLogs.Where(l => l.Kind == "test-pad").OrderBy(l => l.Id).ToListAsync();
        // Stap-een rondde zelf nog netjes af vóór het breekpunt van stap-twee.
        Assert.Equal(["stap-een"], logs.Select(l => l.Ref));
        Assert.Equal("ok", logs[0].Status);
    }

    [Fact]
    public async Task Pad_StapDieAnnulering_Doorgeeft_LogtCancelled()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = NewSp(dbName);
        using var cts = new CancellationTokenSource();
        var jobs = Catalog(("trage-stap", async (_, _, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new JobOutcome("nooit bereikt");
        }));
        var path = new PathDefinition("test-pad", [new PathStep("trage-stap")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PathRunner.RunAsync(path, sp, _ => { }, cts.Token, jobs));

        await using var db = NewDb(dbName);
        var log = await db.RunLogs.SingleAsync(l => l.Kind == "test-pad");
        Assert.Equal("cancelled", log.Status);
        Assert.Contains("afgebroken via beheer", log.Detail);
    }

    // --- testinfra ---------------------------------------------------------

    /// <summary>Wacht tot de lopende run is afgerond (running == null). De
    /// afronding gebeurt op een achtergrondtaak, dus even pollen; de timeout
    /// laat een blijvende hang falen i.p.v. de suite te laten hangen.</summary>
    private static async Task<(JobRunner.JobState? Running, JobRunner.JobState? Last)> WaitForFinishAsync(
        JobRunner runner)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = runner.Snapshot();
            if (snapshot.Running is null && snapshot.Last is not null) return snapshot;
            await Task.Delay(10);
        }
        Assert.Fail("job rondde niet af binnen 10s");
        return default;
    }

    private static JobRunner NewRunner(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobRunner>.Instance);

    private static Func<string, JobDefinition?> Catalog(
        params (string Name, Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> Run)[] entries)
    {
        var byName = entries.ToDictionary(e => e.Name, e => new JobDefinition(e.Name, e.Run));
        return name => byName.GetValueOrDefault(name);
    }

    /// <summary>Scoped DbContext over een gedeelde InMemory-store (patroon
    /// PathRunnerTests): elke verse scope krijgt écht een verse context.</summary>
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
