using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De gehoiste recente-decks-pool (#318-review B2): de pool is
/// expliciete invoer van <see cref="DeckPopularityQuery.ForCanonicalAsync(
/// RbRulesDbContext, string, List{long}, System.Threading.CancellationToken)"/>
/// en begrenst álle tellingen — noemer, aandeel én co-occurrence. Zo kan het
/// /ask-deck-meta-kanaal de pool één keer ophalen en voor meerdere kaarten
/// hergebruiken zonder dat de cijfers verschuiven; het volledige-signaal-
/// gedrag zelf staat in CardDetailServiceDeckPopularityTests.</summary>
public class DeckPopularityQueryTests
{
    private const string CardId = "ogn-011-298";
    private const string CoPlayId = "ogn-022-298";

    [Fact]
    public async Task ForCanonicalAsync_MetPool_AlleTellingenBinnenDePool()
    {
        using var db = NewDb();
        var decks = Enumerable.Range(0, 5).Select(i => new Deck
        {
            PaId = $"deck-{i}",
            SourceUrl = $"https://piltoverarchive.com/decks/view/deck-{i}",
            PaUpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
        }).ToList();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        // De kaart in deck 0 én deck 4; de co-play-kaart alleen in deck 0.
        db.DeckCards.AddRange(
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 2 },
            new DeckCard { DeckId = decks[4].Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 3 },
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "OGN-022", CanonicalRiftboundId = CoPlayId, Quantity = 1 });
        await db.SaveChangesAsync();

        // Pool = decks 0-2: deck 4 bestaat wél maar telt nergens mee.
        var pool = decks.Take(3).Select(d => d.Id).ToList();
        var pop = await DeckPopularityQuery.ForCanonicalAsync(
            db, CardId, pool, CancellationToken.None);

        Assert.Equal(3, pop.RecentDeckCount); // noemer = de meegegeven pool
        Assert.Equal(1, pop.DeckCount); // alleen deck 0; deck 4 valt erbuiten
        Assert.Equal(2.0, pop.AverageCopiesWhenPlayed); // deck 4's 3 stuks tellen niet mee
        var co = Assert.Single(pop.TopCoPlayed);
        Assert.Equal(CoPlayId, co.RiftboundId);
        Assert.Equal(1, co.DeckCount);
    }

    [Fact]
    public async Task ForCanonicalAsync_ZonderPoolParameter_IsGelijkAanVolledigePool()
    {
        // De convenience-overload (dossier-pad) = RecentDeckIdsAsync + de
        // pool-overload; beide leveren identieke cijfers.
        using var db = NewDb();
        var decks = Enumerable.Range(0, 4).Select(i => new Deck
        {
            PaId = $"deck-{i}",
            SourceUrl = $"https://piltoverarchive.com/decks/view/deck-{i}",
            PaUpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
        }).ToList();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        db.DeckCards.Add(new DeckCard
        {
            DeckId = decks[1].Id, Section = "maindeck", CardCode = "OGN-011",
            CanonicalRiftboundId = CardId, Quantity = 2,
        });
        await db.SaveChangesAsync();

        var viaConvenience = await DeckPopularityQuery.ForCanonicalAsync(
            db, CardId, CancellationToken.None);
        var pool = await DeckPopularityQuery.RecentDeckIdsAsync(db, CancellationToken.None);
        var viaPool = await DeckPopularityQuery.ForCanonicalAsync(
            db, CardId, pool, CancellationToken.None);

        Assert.Equal(viaConvenience.RecentDeckCount, viaPool.RecentDeckCount);
        Assert.Equal(viaConvenience.DeckCount, viaPool.DeckCount);
        Assert.Equal(viaConvenience.Percentage, viaPool.Percentage);
        Assert.Equal(viaConvenience.AverageCopiesWhenPlayed, viaPool.AverageCopiesWhenPlayed);
        Assert.Equal(viaConvenience.ThinData, viaPool.ThinData);
        Assert.Equal(4, viaPool.RecentDeckCount);
        Assert.Equal(1, viaPool.DeckCount);
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als de andere InMemory-tests).</summary>
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
