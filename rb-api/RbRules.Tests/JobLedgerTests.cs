using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Grootboeklogica van de periodieke zelfverrijking (#122): de
/// scheduler leest run_log (kind "job" — JobRunner schrijft die regel bij
/// elke jobafronding) om zijn vensters te bepalen. Handmatige runs tellen
/// daardoor mee en een container-herstart veroorzaakt geen dubbele run.</summary>
public class JobLedgerTests
{
    [Fact]
    public async Task LastRunAsync_GeenEerdereRun_GeeftNull()
    {
        using var db = NewDb();
        // Regels van andere soorten (de scout logt zelf onder kind "scout")
        // of van andere jobs zijn geen grootboek voor deze job.
        db.RunLogs.Add(new RunLog { Kind = "scout", Ref = "scout", Status = "info" });
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "claims", Status = "ok" });
        await db.SaveChangesAsync();

        Assert.Null(await new JobLedger(db).LastRunAsync("scout"));
    }

    [Fact]
    public async Task LastRunAsync_PaktDeLaatsteAfronding_OokEenMislukte()
    {
        using var db = NewDb();
        var ouder = DateTimeOffset.UtcNow.AddDays(-9);
        var nieuwer = DateTimeOffset.UtcNow.AddDays(-2);
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "scout", Status = "ok", CreatedAt = ouder });
        // Ook een mislukte afronding vult het venster: herstel loopt via de
        // handmatige job, niet via elke tick opnieuw proberen.
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "scout", Status = "error", CreatedAt = nieuwer });
        await db.SaveChangesAsync();

        Assert.Equal(nieuwer, await new JobLedger(db).LastRunAsync("scout"));
    }

    [Fact]
    public async Task LastRunsAsync_LaatsteRunPerJob_MetStatus()
    {
        using var db = NewDb();
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var t1 = DateTimeOffset.UtcNow.AddHours(-1);
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "relations", Status = "ok", CreatedAt = t0 });
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "relations", Status = "error", CreatedAt = t1 });
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "scan", Status = "ok", CreatedAt = t0 });
        // Service-eigen logregels (kind "relations") zijn geen jobafronding.
        db.RunLogs.Add(new RunLog { Kind = "relations", Ref = "concept:x", Status = "ok", CreatedAt = t1 });
        await db.SaveChangesAsync();

        var runs = await new JobLedger(db).LastRunsAsync();

        Assert.Equal(2, runs.Count);
        var relations = Assert.Single(runs, r => r.Name == "relations");
        Assert.Equal("error", relations.Status);
        Assert.Equal(t1, relations.At);
        var scan = Assert.Single(runs, r => r.Name == "scan");
        Assert.Equal("ok", scan.Status);
        Assert.Equal(t0, scan.At);
    }

    [Fact]
    public async Task LastRunAsync_DecksJob_LeestDeJobRunnerAfrondingUitHetGrootboek()
    {
        // Piltover-decks (#15 fase 3, spoor C): de scheduler moet zijn
        // 3-uursvenster op precies dezelfde kind="job"/ref="decks"-regel
        // kunnen bepalen als handmatige "decks"-runs al schrijven (#148,
        // JobRunner.TryStart) — geen apart grootboek voor de periodieke
        // trigger.
        using var db = NewDb();
        var lastDeckRun = DateTimeOffset.UtcNow.AddHours(-4);
        db.RunLogs.Add(new RunLog { Kind = "job", Ref = "decks", Status = "ok", CreatedAt = lastDeckRun });
        // Eigen logregels van DeckIngestService (kind "deckingest") zijn geen
        // jobafronding en mogen het venster niet vullen.
        db.RunLogs.Add(new RunLog
        {
            Kind = "deckingest", Ref = "deck:abc", Status = "ok", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(lastDeckRun, await new JobLedger(db).LastRunAsync("decks"));
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
}
