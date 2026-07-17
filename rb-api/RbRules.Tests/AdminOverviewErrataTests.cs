using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Supersede-signaal in het errata-beheeroverzicht (#168): puur
/// berekend uit TrustTier + EffectiveFrom, geen eigen status-kolom en geen
/// verwijdering — een kandidaat-signaal voor de beheerder.</summary>
public class AdminOverviewErrataTests
{
    [Fact]
    public async Task ErrataAsync_TwoSourcesSameCard_OlderIsMarkedSuperseded()
    {
        using var db = NewDb();
        db.Sources.Add(Official("oud", "https://example.com/errata-oud"));
        db.Sources.Add(Official("nieuw", "https://example.com/errata-nieuw"));
        db.Errata.Add(new Erratum
        {
            Id = 1, CardName = "Test Kaart", CardRiftboundId = "ogn-011-298",
            NewText = "Oude tekst.", SourceUrl = "https://example.com/errata-oud",
            EffectiveFrom = new DateOnly(2025, 1, 1),
        });
        db.Errata.Add(new Erratum
        {
            Id = 2, CardName = "Test Kaart", CardRiftboundId = "ogn-011-298",
            NewText = "Nieuwe tekst.", SourceUrl = "https://example.com/errata-nieuw",
            EffectiveFrom = new DateOnly(2026, 1, 1),
        });
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db, new ChangeFeedService(db)).ErrataAsync();

        var oud = overview.Single(e => e.Id == 1);
        var nieuw = overview.Single(e => e.Id == 2);
        Assert.Equal(2, oud.SupersededByErratumId);
        Assert.Null(nieuw.SupersededByErratumId);
    }

    [Fact]
    public async Task ErrataAsync_SingleSourcePerCard_NeverFlaggedAsSuperseded()
    {
        using var db = NewDb();
        db.Sources.Add(Official("s1", "https://example.com/errata-1"));
        db.Errata.Add(new Erratum
        {
            Id = 1, CardName = "Test Kaart", CardRiftboundId = "ogn-011-298",
            NewText = "Enige tekst.", SourceUrl = "https://example.com/errata-1",
        });
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db, new ChangeFeedService(db)).ErrataAsync();

        Assert.Null(Assert.Single(overview).SupersededByErratumId);
    }

    [Fact]
    public async Task ErrataAsync_DifferentCards_NeverCrossFlagged()
    {
        using var db = NewDb();
        db.Sources.Add(Official("s1", "https://example.com/errata-1"));
        db.Sources.Add(Official("s2", "https://example.com/errata-2"));
        db.Errata.Add(new Erratum
        {
            Id = 1, CardName = "Kaart A", CardRiftboundId = "ogn-011-298",
            NewText = "Tekst A.", SourceUrl = "https://example.com/errata-1",
        });
        db.Errata.Add(new Erratum
        {
            Id = 2, CardName = "Kaart B", CardRiftboundId = "ogn-050-298",
            NewText = "Tekst B.", SourceUrl = "https://example.com/errata-2",
        });
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db, new ChangeFeedService(db)).ErrataAsync();

        Assert.All(overview, e => Assert.Null(e.SupersededByErratumId));
    }

    private static Source Official(string id, string url) => new()
    {
        Id = id, Name = id, Url = url, Type = "official",
        TrustTier = 1, Rank = 100, Parser = "html", Cadence = "weekly",
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AdminOverview-tests).</summary>
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
