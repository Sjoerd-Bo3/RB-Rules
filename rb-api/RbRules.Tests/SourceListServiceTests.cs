using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Bronnenlijst-projectie voor het beheer (#180): de negeer-status
/// (IgnoredAt/IgnoreReason) en de goedkoop-berekende negeer-kandidaat-vlag,
/// via vier gebatchte tellingen (run_log/Change/ClaimSource/Correction) i.p.v.
/// een query per bron. Database is EF InMemory (SourceDossierServiceTests-
/// patroon).</summary>
public class SourceListServiceTests
{
    [Fact]
    public async Task ListAsync_GeenBronnen_LegeLijst()
    {
        using var db = NewDb();
        var items = await new SourceListService(db).ListAsync();
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_BevatGenegeerdeBronnenMetReden()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        var ignoredAt = DateTimeOffset.UtcNow;
        db.Sources.Add(Source("s2", ignoredAt: ignoredAt, ignoreReason: "merch-artikel"));
        await db.SaveChangesAsync();

        var items = await new SourceListService(db).ListAsync();

        Assert.Equal(2, items.Count);
        var ignored = items.Single(i => i.Id == "s2");
        Assert.Equal(ignoredAt, ignored.IgnoredAt);
        Assert.Equal("merch-artikel", ignored.IgnoreReason);
        Assert.Null(items.Single(i => i.Id == "s1").IgnoredAt);
    }

    [Fact]
    public async Task ListAsync_MinderDanTweeScans_GeenKandidaat()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_TweeVoltooideScansNietsOpgeleverd_Kandidaat()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.True(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_MislukteScansTellenNietMee()
    {
        // Twee mislukte pogingen zeggen niets over de bron zelf — pas
        // voltooide scans (status != "error") tellen mee.
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "error", Detail = "HTTP 500" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "error", Detail = "HTTP 500" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_MetChanges_GeenKandidaatOndanksMeerdereScans()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "changed" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.Changes.Add(new Change { SourceId = "s1", ChangeType = "errata", Severity = "medium" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_MetClaimBijdrage_GeenKandidaat()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1", trustTier: 3));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        var claim = new Claim { TopicType = "card", TopicRef = "Testkaart", Statement = "werkt zo" };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimSources.Add(new ClaimSource { ClaimId = claim.Id, SourceId = "s1", Url = "https://x/thread" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_MetClarifyMiningRuling_GeenKandidaat()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "Legion = finalize.",
            Provenance = "clarify-mining:s1", Status = "verified",
        });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_RulingVanAndereBron_TeltNietMee()
    {
        // Provenance-koppeling is bron-specifiek — een ruling op s2 mag s1
        // niet "redden" van de kandidaat-vlag.
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.Sources.Add(Source("s2"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "Legion = finalize.",
            Provenance = "clarify-mining:s2", Status = "verified",
        });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync(), i => i.Id == "s1");

        Assert.True(item.IsIgnoreCandidate);
    }

    [Fact]
    public async Task ListAsync_AlGenegeerdeBron_NooitKandidaat()
    {
        // De kandidaat-vlag is een suggestie om NOG te negeren — een reeds
        // genegeerde bron heeft dat al gehad, dus geen hint meer nodig.
        using var db = NewDb();
        db.Sources.Add(Source("s1", ignoredAt: DateTimeOffset.UtcNow));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new SourceListService(db).ListAsync());

        Assert.False(item.IsIgnoreCandidate);
    }

    // --- testinfra (SourceDossierServiceTests-patroon) --------------------

    private static Source Source(
        string id, short trustTier = 1,
        DateTimeOffset? ignoredAt = null, string? ignoreReason = null) => new()
    {
        Id = id, Name = id, Url = $"https://playriftbound.com/news/{id}",
        Type = "official", TrustTier = trustTier, Parser = "html", Cadence = "daily",
        IgnoredAt = ignoredAt, IgnoreReason = ignoreReason,
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
