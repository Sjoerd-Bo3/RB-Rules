using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Feed-presentatie van changeconsolidatie (#206): de publieke lijst
/// (en het admin-overzicht, via dezelfde <see cref="ChangeFeedService"/>)
/// toont alleen primaire changes; secundaire (geconsolideerde) changes
/// verdwijnen uit de hoofdlijst maar komen genest terug als
/// <see cref="ChangeFeedItem.ConfirmedBy"/>.</summary>
public class ChangeFeedServiceTests
{
    [Fact]
    public async Task ListAsync_SecundaireChange_VerdwijntUitHoofdlijst_KomtGenestTerug()
    {
        using var db = NewDb();
        var official = Source("rules-hub", official: true);
        var community = Source("mobalytics", official: false);
        db.Sources.AddRange(official, community);
        var primary = new Change
        {
            SourceId = "rules-hub", ChangeType = "ban", Summary = "Viktor banned.",
            DetectedAt = new DateTimeOffset(2026, 7, 16, 6, 46, 0, TimeSpan.Zero),
        };
        db.Changes.Add(primary);
        await db.SaveChangesAsync();
        db.Changes.Add(new Change
        {
            SourceId = "mobalytics", ChangeType = "ban", Summary = "Community: Viktor banned.",
            Meaning = "Deck lists must drop Viktor.", Diff = "- Viktor legal\n+ Viktor banned",
            DetectedAt = new DateTimeOffset(2026, 7, 16, 6, 51, 0, TimeSpan.Zero),
            ConsolidatedWithId = primary.Id,
        });
        await db.SaveChangesAsync();

        var feed = new ChangeFeedService(db);
        var items = await feed.ListAsync(severity: null, type: null, source: null, take: 50);

        var item = Assert.Single(items);
        Assert.Equal(primary.Id, item.Id);
        var confirmation = Assert.Single(item.ConfirmedBy);
        Assert.Equal("mobalytics", confirmation.SourceId);
        Assert.Equal("Community: Viktor banned.", confirmation.Summary);
        // Review-fix finding 3: de secundaire details (duiding + voor/na)
        // blijven ná consolidatie inspecteerbaar via de bevestiging zelf.
        Assert.Equal("Deck lists must drop Viktor.", confirmation.Meaning);
        Assert.Equal("- Viktor legal\n+ Viktor banned", confirmation.Diff);
    }

    [Fact]
    public async Task DeleteAsync_Primaire_VerwijdertOokHaarSecundairen()
    {
        // Review-fix finding 9: zonder cascade zou de FK-SetNull de
        // secundaire als losse kaart laten herrijzen — terwijl de beheerder
        // net "dit event weg uit de feed" besloot.
        using var db = NewDb();
        db.Sources.AddRange(Source("rules-hub", official: true), Source("mobalytics", official: false));
        var primary = new Change { SourceId = "rules-hub", ChangeType = "ban", Summary = "Viktor banned." };
        db.Changes.Add(primary);
        await db.SaveChangesAsync();
        db.Changes.Add(new Change
        {
            SourceId = "mobalytics", ChangeType = "ban", Summary = "Community: Viktor banned.",
            ConsolidatedWithId = primary.Id,
        });
        await db.SaveChangesAsync();

        var r = await new ChangeFeedService(db).DeleteAsync(primary.Id);

        Assert.True(r.Found);
        Assert.Equal(1, r.RemovedConfirmations);
        Assert.Empty(await db.Changes.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_Secundaire_LaatDePrimaireStaan()
    {
        using var db = NewDb();
        db.Sources.AddRange(Source("rules-hub", official: true), Source("mobalytics", official: false));
        var primary = new Change { SourceId = "rules-hub", ChangeType = "ban", Summary = "Viktor banned." };
        db.Changes.Add(primary);
        await db.SaveChangesAsync();
        var secondary = new Change
        {
            SourceId = "mobalytics", ChangeType = "ban", Summary = "Community: Viktor banned.",
            ConsolidatedWithId = primary.Id,
        };
        db.Changes.Add(secondary);
        await db.SaveChangesAsync();

        var r = await new ChangeFeedService(db).DeleteAsync(secondary.Id);

        Assert.True(r.Found);
        Assert.Equal(0, r.RemovedConfirmations);
        var remaining = Assert.Single(await db.Changes.ToListAsync());
        Assert.Equal(primary.Id, remaining.Id);
    }

    [Fact]
    public async Task DeleteAsync_OnbekendId_GeeftNotFound()
    {
        using var db = NewDb();

        var r = await new ChangeFeedService(db).DeleteAsync(999);

        Assert.False(r.Found);
        Assert.Equal(0, r.RemovedConfirmations);
    }

    [Fact]
    public async Task ListAsync_GeenConsolidatie_ConfirmedByIsLeeg()
    {
        using var db = NewDb();
        db.Sources.Add(Source("rules-hub", official: true));
        db.Changes.Add(new Change
        {
            SourceId = "rules-hub", ChangeType = "ban", Summary = "Viktor banned.",
        });
        await db.SaveChangesAsync();

        var feed = new ChangeFeedService(db);
        var items = await feed.ListAsync(null, null, null, take: 50);

        Assert.Empty(Assert.Single(items).ConfirmedBy);
    }

    [Fact]
    public async Task ListAsync_MeerdereBevestigingen_GesorteerdOpDetectiemoment()
    {
        using var db = NewDb();
        db.Sources.AddRange(
            Source("rules-hub", official: true),
            Source("mobalytics", official: false),
            Source("riftcodex-community", official: false));
        var primary = new Change { SourceId = "rules-hub", ChangeType = "ban", Summary = "Viktor banned." };
        db.Changes.Add(primary);
        await db.SaveChangesAsync();
        db.Changes.Add(new Change
        {
            SourceId = "riftcodex-community", ChangeType = "ban", Summary = "later",
            DetectedAt = DateTimeOffset.UtcNow.AddMinutes(10), ConsolidatedWithId = primary.Id,
        });
        db.Changes.Add(new Change
        {
            SourceId = "mobalytics", ChangeType = "ban", Summary = "eerder",
            DetectedAt = DateTimeOffset.UtcNow.AddMinutes(5), ConsolidatedWithId = primary.Id,
        });
        await db.SaveChangesAsync();

        var feed = new ChangeFeedService(db);
        var items = await feed.ListAsync(null, null, null, take: 50);

        var confirmations = Assert.Single(items).ConfirmedBy;
        Assert.Equal(2, confirmations.Count);
        Assert.Equal("eerder", confirmations[0].Summary);
        Assert.Equal("later", confirmations[1].Summary);
    }

    [Fact]
    public async Task ConfirmationsByPrimaryIdAsync_LegeInvoer_GeeftLegeDictionary()
    {
        using var db = NewDb();
        var feed = new ChangeFeedService(db);

        var result = await feed.ConfirmationsByPrimaryIdAsync([]);

        Assert.Empty(result);
    }

    private static Source Source(string id, bool official) => new()
    {
        Id = id, Name = id, Url = $"https://example.test/{id}",
        Type = official ? "official" : "community",
        TrustTier = official ? (short)1 : (short)3,
        Rank = 100, Parser = "html", Cadence = "daily",
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
