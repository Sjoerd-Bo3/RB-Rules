using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Herkoppel-pass (#355): de linker opnieuw over ongelinkte
/// deck-rijen, zonder Piltover-fetch — de bulk-run slaat ongewijzigde decks
/// immers over en heelt ze dus nooit zelf.</summary>
public class DeckRelinkTests
{
    private static RbRulesDbContext Db()
    {
        return new InMemoryDbContext(new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    [Fact]
    public async Task RelinkUnlinkedAsync_HeeltSterCodesEnTeltDeRest()
    {
        await using var db = Db();
        db.Cards.Add(new Card { RiftboundId = "unl-233-219", Name = "Jinx, Loose Cannon" });
        db.Cards.Add(new Card
        {
            RiftboundId = "unl-233-star-219",
            Name = "Jinx, Loose Cannon (Overnumbered)",
            VariantOf = "unl-233-219"
        });
        var deck = new Deck { Id = 1, PaId = "pa-1", SourceUrl = "https://example.test/1" };
        db.Decks.Add(deck);
        // De ster-rij is met de oude linker null gebleven (#351); de
        // onbekende code hoort ook ná de pass null te blijven.
        db.DeckCards.Add(new DeckCard
        {
            DeckId = 1, Section = "legend", CardCode = "UNL-233*",
            CanonicalRiftboundId = null, Quantity = 1
        });
        db.DeckCards.Add(new DeckCard
        {
            DeckId = 1, Section = "maindeck", CardCode = "ZZZ-999",
            CanonicalRiftboundId = null, Quantity = 2
        });
        // Al gekoppelde rijen blijven onaangeroerd.
        db.DeckCards.Add(new DeckCard
        {
            DeckId = 1, Section = "maindeck", CardCode = "UNL-233a",
            CanonicalRiftboundId = "unl-233-219", Quantity = 2
        });
        await db.SaveChangesAsync();

        var ingest = new DeckIngestService(db, new HttpClient());
        var (healed, remaining) = await ingest.RelinkUnlinkedAsync();

        Assert.Equal(1, healed);
        Assert.Equal(1, remaining);
        var star = await db.DeckCards.SingleAsync(c => c.CardCode == "UNL-233*");
        Assert.Equal("unl-233-219", star.CanonicalRiftboundId);
        var unknown = await db.DeckCards.SingleAsync(c => c.CardCode == "ZZZ-999");
        Assert.Null(unknown.CanonicalRiftboundId);
    }

    /// <summary>Zelfde InMemory-wrapper als DeckFetchServiceTests: InMemory
    /// kent het pgvector-type niet, dus vectors reizen als tekst (alleen
    /// opslag — vector-queries blijven buiten deze tests).</summary>
    private class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(
                            new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                                .ValueConverter<Pgvector.Vector, string>(
                                v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }
}
