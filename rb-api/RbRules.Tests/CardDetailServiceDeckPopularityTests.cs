using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Deck-gebruikssignaal (#15 golf 1 spoor B): het aandeel van de
/// recente Piltover Archive-decks dat een kaart speelt, met eerlijke
/// dunne/lege staat onder de drempel en een vaste poolgrootte i.p.v. een
/// kalendervenster (zie CardDetailService.DeckPopularityAsync).</summary>
public class CardDetailServiceDeckPopularityTests
{
    private const string CardId = "ogn-011-298";

    [Fact]
    public async Task DossierAsync_GeenDecksInDeBank_GeeftLegeStaatMetNulNoemer()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(0, pop.RecentDeckCount);
        Assert.Equal(0, pop.DeckCount);
        Assert.Equal(0, pop.Percentage);
        Assert.Null(pop.AverageCopiesWhenPlayed);
        Assert.True(pop.ThinData);
        Assert.Empty(pop.TopCoPlayed);
    }

    [Fact]
    public async Task DossierAsync_OnderDrempel_ThinDataMaarTelaantallenKloppen()
    {
        // Review-motivatie (#15): een kleine noemer mag nooit als hard
        // percentage overkomen — ThinData markeert dat, maar de ruwe
        // aantallen (2 van de 5) blijven eerlijk berekend.
        using var db = NewDb();
        db.Cards.Add(Card());
        AddDecks(db, count: 5, cardInDecks: [0, 1]);
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(5, pop.RecentDeckCount);
        Assert.Equal(2, pop.DeckCount);
        Assert.Equal(40.0, pop.Percentage);
        Assert.True(pop.ThinData); // 5 < MinRecentDecksForSignal (20)
    }

    [Fact]
    public async Task DossierAsync_BovenDrempel_PercentageEnThinDataKloppen()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        AddDecks(db, count: 25, cardInDecks: [0, 1, 2, 3, 4]);
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(25, pop.RecentDeckCount);
        Assert.Equal(5, pop.DeckCount);
        Assert.Equal(20.0, pop.Percentage);
        Assert.False(pop.ThinData); // 25 >= MinRecentDecksForSignal (20)
    }

    [Fact]
    public async Task DossierAsync_SideboardBenchEnLegend_TellenNietMee()
    {
        // Motivatie (#15): sideboard is matchup-tech (geen kernidentiteit),
        // bench is PA's bouwer-kladblok (geen ingeleverde lijst), legend is
        // een 1-op-1-signaal — geen van drie hoort in "populair in decks".
        using var db = NewDb();
        db.Cards.Add(Card());
        var deck = NewDeck(0);
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = deck.Id, Section = "sideboard", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 },
            new DeckCard { DeckId = deck.Id, Section = "bench", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 },
            new DeckCard { DeckId = deck.Id, Section = "legend", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(1, pop.RecentDeckCount);
        Assert.Equal(0, pop.DeckCount); // geen van de drie secties telt mee
        Assert.Equal(0, pop.Percentage);
    }

    [Fact]
    public async Task DossierAsync_KaartInMeerdereSectiesVanZelfdeDeck_TeltDeckMaarEenKeer()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        var deck = NewDeck(0);
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = deck.Id, Section = "champions", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 },
            new DeckCard { DeckId = deck.Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 2 });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(1, pop.DeckCount); // één deck, ondanks twee matchende rijen
        Assert.Equal(100.0, pop.Percentage);
        Assert.Equal(3.0, pop.AverageCopiesWhenPlayed); // som van beide rijen: 1 + 2
    }

    [Fact]
    public async Task DossierAsync_GemiddeldAantalExemplaren_IsGemiddeldeVanDeSomPerDeck()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        var deckA = NewDeck(0);
        var deckB = NewDeck(1);
        db.Decks.AddRange(deckA, deckB);
        await db.SaveChangesAsync();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = deckA.Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 3 },
            new DeckCard { DeckId = deckB.Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(2, pop.DeckCount);
        Assert.Equal(2.0, pop.AverageCopiesWhenPlayed); // (3 + 1) / 2
    }

    [Fact]
    public async Task DossierAsync_TopCoOccurrence_OrdentOpAantalDecksAflopend()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        // 3 decks bevatten de dossierkaart; "vaak-samen" komt in 3, "af-en-toe" in 1.
        var decks = Enumerable.Range(0, 3).Select(NewDeck).ToList();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        foreach (var deck in decks)
            db.DeckCards.Add(new DeckCard
            {
                DeckId = deck.Id, Section = "maindeck", CardCode = "OGN-011",
                CanonicalRiftboundId = CardId, Quantity = 1,
            });
        db.DeckCards.AddRange(
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "X1", CanonicalRiftboundId = "vaak-samen", Quantity = 1 },
            new DeckCard { DeckId = decks[1].Id, Section = "maindeck", CardCode = "X1", CanonicalRiftboundId = "vaak-samen", Quantity = 1 },
            new DeckCard { DeckId = decks[2].Id, Section = "maindeck", CardCode = "X1", CanonicalRiftboundId = "vaak-samen", Quantity = 1 },
            new DeckCard { DeckId = decks[0].Id, Section = "maindeck", CardCode = "X2", CanonicalRiftboundId = "af-en-toe", Quantity = 1 });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(2, pop.TopCoPlayed.Count);
        Assert.Equal("vaak-samen", pop.TopCoPlayed[0].RiftboundId);
        Assert.Equal(3, pop.TopCoPlayed[0].DeckCount);
        Assert.Equal("af-en-toe", pop.TopCoPlayed[1].RiftboundId);
        Assert.Equal(1, pop.TopCoPlayed[1].DeckCount);
    }

    [Fact]
    public async Task DossierAsync_OnbekendeKaartenInAndereSecties_TellenNietAlsCoOccurrence()
    {
        // Onbekende PA-kaarten (CanonicalRiftboundId == null, #15 spoor 2:
        // nog niet elke printing gekoppeld) mogen nooit als co-occurrence
        // opduiken — dat zou een niet-navigeerbare kaartverwijzing geven.
        using var db = NewDb();
        db.Cards.Add(Card());
        var deck = NewDeck(0);
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        db.DeckCards.AddRange(
            new DeckCard { DeckId = deck.Id, Section = "maindeck", CardCode = "OGN-011", CanonicalRiftboundId = CardId, Quantity = 1 },
            new DeckCard { DeckId = deck.Id, Section = "maindeck", CardCode = "onbekend", CanonicalRiftboundId = null, Quantity = 1 });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Empty(pop.TopCoPlayed);
    }

    [Fact]
    public async Task DossierAsync_RecentVenster_DecksBuitenDePoolTellenNietMee()
    {
        // De poolgrootte (RecentDeckWindow) is een vaste top-N op
        // PaUpdatedAt, geen kalendervenster (zie CardDetailService-
        // motivatie): het oudste deck valt precies buiten de pool en zijn
        // kaart mag dus niet meetellen, ook al staat de kaart er wél in.
        using var db = NewDb();
        db.Cards.Add(Card());
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // RecentDeckWindow (500) verse decks zónder de kaart...
        var freshDecks = Enumerable.Range(0, CardDetailService.RecentDeckWindow)
            .Select(i => new Deck
            {
                PaId = $"vers-{i}", SourceUrl = $"https://piltoverarchive.com/decks/view/vers-{i}",
                PaUpdatedAt = baseline.AddDays(i + 1), // allemaal ná de outsider
            })
            .ToList();
        // ... en precies één outsider, ouder dan alle andere — valt buiten de pool.
        var outsider = new Deck
        {
            PaId = "outsider", SourceUrl = "https://piltoverarchive.com/decks/view/outsider",
            PaUpdatedAt = baseline,
        };
        db.Decks.AddRange(freshDecks);
        db.Decks.Add(outsider);
        await db.SaveChangesAsync();
        db.DeckCards.Add(new DeckCard
        {
            DeckId = outsider.Id, Section = "maindeck", CardCode = "OGN-011",
            CanonicalRiftboundId = CardId, Quantity = 1,
        });
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(CardId))!.DeckPopularity;

        Assert.Equal(CardDetailService.RecentDeckWindow, pop.RecentDeckCount);
        Assert.Equal(0, pop.DeckCount); // de enige drager van de kaart viel buiten de pool
    }

    [Fact]
    public async Task DossierAsync_VariantPrinting_DeeltHetSignaalVanDeCanoniekeKaart()
    {
        // Zelfde patroon als de rest van het dossier (#57): een alt-art-
        // pagina toont het deck-signaal van zijn canonieke kaart.
        const string variantId = "ogn-011a-298";
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Cards.Add(new Card { RiftboundId = variantId, Name = "Test Kaart (Alternate Art)", VariantOf = CardId, SetId = "OGN" });
        AddDecks(db, count: 3, cardInDecks: [0]);
        await db.SaveChangesAsync();

        var pop = (await Service(db).DossierAsync(variantId))!.DeckPopularity;

        Assert.Equal(1, pop.DeckCount);
        Assert.Equal(3, pop.RecentDeckCount);
    }

    // --- testinfra ---------------------------------------------------------

    private static Card Card() => new() { RiftboundId = CardId, Name = "Test Kaart", SetId = "OGN" };

    private static Deck NewDeck(int i) => new()
    {
        PaId = $"deck-{i}",
        SourceUrl = $"https://piltoverarchive.com/decks/view/deck-{i}",
        PaUpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
    };

    /// <summary>count decks aanmaken; de kaart zit (via maindeck) in de
    /// decks op de indices in cardInDecks.</summary>
    private static void AddDecks(RbRulesDbContext db, int count, int[] cardInDecks)
    {
        var decks = Enumerable.Range(0, count).Select(NewDeck).ToList();
        db.Decks.AddRange(decks);
        db.SaveChanges();
        foreach (var i in cardInDecks)
            db.DeckCards.Add(new DeckCard
            {
                DeckId = decks[i].Id, Section = "maindeck", CardCode = "OGN-011",
                CanonicalRiftboundId = CardId, Quantity = 1,
            });
    }

    private static CardDetailService Service(RbRulesDbContext db) => new(db, new CardResolver(db));

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als CardDetailServiceErrataTests).</summary>
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
