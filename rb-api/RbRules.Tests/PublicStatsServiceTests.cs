using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Publieke dashboard-telstanden (#214): canonieke kaarten,
/// geverifieerde rulings, bans en recente (primaire, niet-editoriale)
/// wijzigingen binnen het venster.</summary>
public class PublicStatsServiceTests
{
    [Fact]
    public async Task GetAsync_TeltDeJuisteStanden()
    {
        using var db = NewDb();
        // Kaarten: 2 canoniek, 1 variant (telt niet mee).
        db.Cards.Add(new Card { RiftboundId = "a", Name = "Aatrox" });
        db.Cards.Add(new Card { RiftboundId = "b", Name = "Braum" });
        db.Cards.Add(new Card { RiftboundId = "b2", Name = "Braum", VariantOf = "b" });
        // Rulings: 1 verified, 1 unverified (telt niet mee).
        db.Corrections.Add(new Correction { Scope = "card", Ref = "Aatrox", Text = "…", Status = "verified" });
        db.Corrections.Add(new Correction { Scope = "card", Ref = "Braum", Text = "…", Status = "unverified" });
        // Bans: 2.
        db.BanEntries.Add(new BanEntry { Name = "Aatrox", Kind = "card", SourceUrl = "https://x/1" });
        db.BanEntries.Add(new BanEntry { Name = "Braum", Kind = "card", SourceUrl = "https://x/2" });
        // Changes: 1 recent primair (telt), 1 oud (niet), 1 editorial (niet),
        // 1 secundair (niet).
        db.Changes.Add(new Change { SourceId = "s", ChangeType = "ban", DetectedAt = DateTimeOffset.UtcNow });
        db.Changes.Add(new Change { SourceId = "s", ChangeType = "ban", DetectedAt = DateTimeOffset.UtcNow.AddDays(-30) });
        db.Changes.Add(new Change { SourceId = "s", ChangeType = "editorial", DetectedAt = DateTimeOffset.UtcNow });
        db.Changes.Add(new Change { SourceId = "s", ChangeType = "ban", DetectedAt = DateTimeOffset.UtcNow, ConsolidatedWithId = 999 });
        await db.SaveChangesAsync();

        var stats = await new PublicStatsService(db).GetAsync();

        Assert.Equal(2, stats.Cards);
        Assert.Equal(1, stats.VerifiedRulings);
        Assert.Equal(2, stats.Bans);
        Assert.Equal(1, stats.RecentChanges);
    }

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
