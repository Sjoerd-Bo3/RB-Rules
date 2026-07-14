using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Deck-browser (#15 fase 3, spoor A): lijst/facetten/paginering en
/// de per-deck legaliteitscheck (hergebruikt DeckLegality met platte feiten
/// uit Card/CardSet/BanEntry). EF InMemory — geen vector-operaties in dit
/// pad (zelfde patroon als DeckIngestServiceTests).</summary>
public class DeckBrowserServiceTests
{
    private static readonly DateOnly LegalSetDate = new(2025, 1, 1);

    [Fact]
    public async Task ListAsync_FiltertOpDomainEnSorteertOpRecentheidStandaard()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-001-298", "Body Rune");
        AddDeck(db, "deck-oud", ["Body"], paUpdatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        AddDeck(db, "deck-nieuw", ["Body"], paUpdatedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        AddDeck(db, "deck-ander-domein", ["Order"], paUpdatedAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var result = await svc.ListAsync(domain: "Body", sort: null, page: 1);

        Assert.Equal(2, result.Total);
        Assert.Equal(["deck-nieuw", "deck-oud"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_SorteertOpViewsEnLikesOpAanvraag()
    {
        using var db = NewDb();
        AddDeck(db, "laag", ["Body"], views: 5, likes: 50);
        AddDeck(db, "hoog", ["Body"], views: 50, likes: 5);
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);

        var byViews = await svc.ListAsync(domain: null, sort: "views", page: 1);
        Assert.Equal(["hoog", "laag"], byViews.Items.Select(i => i.Id));

        var byLikes = await svc.ListAsync(domain: null, sort: "likes", page: 1);
        Assert.Equal(["laag", "hoog"], byLikes.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_PagineringBuitenBereik_GeeftLegeLijstMetJuisteTotal()
    {
        using var db = NewDb();
        AddDeck(db, "enige", ["Body"]);
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var result = await svc.ListAsync(domain: null, sort: null, page: 2);

        Assert.Equal(1, result.Total);
        Assert.Equal(2, result.Page);
        Assert.Equal(DeckBrowserService.PageSize, result.PageSize);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ListAsync_GebandeKaart_MaaktDeckIllegaalInDeLijst()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-666-298", "Verboden Kaart");
        db.BanEntries.Add(new BanEntry
        {
            Name = "Verboden Kaart", CardRiftboundId = "ogn-666-298",
            Kind = "card", Format = "constructed", SourceUrl = "https://playriftbound.com/bans",
        });
        var deck = AddDeck(db, "deck-geband", ["Body"]);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "maindeck", CardCode = "OGN-666",
            CanonicalRiftboundId = "ogn-666-298", Quantity = 3,
        });
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var result = await svc.ListAsync(domain: null, sort: null, page: 1);

        var item = Assert.Single(result.Items);
        Assert.Equal("illegal", item.Legality.Status);
        Assert.Equal(3, item.CardCount);
    }

    [Fact]
    public async Task ListAsync_FormatFiltertBans_AndereFormatBanTeltNietMee()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-777-298", "Alleen Limited Geband");
        db.BanEntries.Add(new BanEntry
        {
            Name = "Alleen Limited Geband", CardRiftboundId = "ogn-777-298",
            Kind = "card", Format = "limited", SourceUrl = "https://playriftbound.com/bans",
        });
        var deck = AddDeck(db, "deck-limited-ban", ["Body"]);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "maindeck", CardCode = "OGN-777",
            CanonicalRiftboundId = "ogn-777-298", Quantity = 3,
        });
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        // Default format is "constructed" — de limited-ban telt daar niet mee.
        var result = await svc.ListAsync(domain: null, sort: null, page: 1);

        var item = Assert.Single(result.Items);
        Assert.Equal("legal", item.Legality.Status);
    }

    [Fact]
    public async Task FacetsAsync_GeeftUniekeGesorteerdeDomeinenTerug()
    {
        using var db = NewDb();
        AddDeck(db, "d1", ["Order", "Body"]);
        AddDeck(db, "d2", ["Body"]);
        AddDeck(db, "d3", ["Fury"]);
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var facets = await svc.FacetsAsync();

        Assert.Equal(["Body", "Fury", "Order"], facets.Domains);
    }

    [Fact]
    public async Task DetailAsync_OnbekendPaId_GeeftNullTerug()
    {
        using var db = NewDb();
        var svc = new DeckBrowserService(db);

        Assert.Null(await svc.DetailAsync("niet-bestaand"));
    }

    [Fact]
    public async Task DetailAsync_GroepeertPerSectieInCanoniekeVolgordeEnLaatLegeSectiesWeg()
    {
        using var db = NewDb();
        SeedLegalCard(db, "unl-203-219", "Poppy, Keeper of the Hammer");
        SeedLegalCard(db, "ogn-001-298", "Body Rune");
        var deck = AddDeck(db, "deck-secties", ["Body"]);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "legend", CardCode = "UNL-203",
            CanonicalRiftboundId = "unl-203-219", Quantity = 1,
        });
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "maindeck", CardCode = "OGN-001",
            CanonicalRiftboundId = "ogn-001-298", Quantity = 12,
        });
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var detail = await svc.DetailAsync("deck-secties");

        Assert.NotNull(detail);
        // legend vóór maindeck (canonieke volgorde), champions/battlefields/
        // runes/sideboard/bench blijven weg — ze zijn leeg.
        Assert.Equal(["legend", "maindeck"], detail!.Sections.Select(s => s.Section));
        var legendCard = Assert.Single(detail.Sections.First(s => s.Section == "legend").Cards);
        Assert.Equal("Poppy, Keeper of the Hammer", legendCard.CardName);
        Assert.Equal("legal", detail.Legality.Status);
    }

    [Fact]
    public async Task DetailAsync_NietGekoppeldeKaart_IsOnvolledigMetOnbekendeNaam()
    {
        using var db = NewDb();
        var deck = AddDeck(db, "deck-onbekend", ["Body"]);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "maindeck", CardCode = "XYZ-999",
            CanonicalRiftboundId = null, Quantity = 2,
        });
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var detail = await svc.DetailAsync("deck-onbekend");

        Assert.NotNull(detail);
        Assert.Equal("incomplete", detail!.Legality.Status);
        Assert.Equal(1, detail.Legality.UnknownCount);
        var card = Assert.Single(detail.Sections.Single(s => s.Section == "maindeck").Cards);
        Assert.Null(card.CardName);
        Assert.Null(card.CanonicalRiftboundId);
        Assert.Equal("XYZ-999", card.CardCode);
    }

    [Fact]
    public async Task DetailAsync_NogNietVerschenenSet_IsIllegaalMetReden()
    {
        using var db = NewDb();
        db.CardSets.Add(new CardSet { SetId = "van", Name = "Vanguard", PublishedOn = new DateOnly(2126, 1, 1) });
        db.Cards.Add(new Card { RiftboundId = "van-001-100", Name = "Toekomstkaart", SetId = "van" });
        var deck = AddDeck(db, "deck-toekomst", ["Body"]);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "maindeck", CardCode = "VAN-001",
            CanonicalRiftboundId = "van-001-100", Quantity = 4,
        });
        await db.SaveChangesAsync();

        var svc = new DeckBrowserService(db);
        var detail = await svc.DetailAsync("deck-toekomst");

        Assert.NotNull(detail);
        Assert.Equal("illegal", detail!.Legality.Status);
        var issue = Assert.Single(detail.Legality.Issues);
        Assert.Equal(DeckLegalityIssue.NotYetLegal, issue.Reason);
        Assert.Equal("Toekomstkaart", issue.CardName);
    }

    // ── seed-helpers ────────────────────────────────────────────────────

    private static void SeedLegalCard(RbRulesDbContext db, string riftboundId, string name)
    {
        // Eén gedeelde legale set — meerdere kaarten mogen hem hergebruiken
        // zonder een dubbele sleutel (CardSet.SetId) te introduceren.
        if (!db.CardSets.Local.Any(s => s.SetId == "ogn") && db.CardSets.Find("ogn") is null)
            db.CardSets.Add(new CardSet { SetId = "ogn", Name = "Origins", PublishedOn = LegalSetDate });
        db.Cards.Add(new Card { RiftboundId = riftboundId, Name = name, SetId = "ogn" });
    }

    private static Deck AddDeck(
        RbRulesDbContext db, string paId, string[] domains,
        int views = 0, int likes = 0, DateTimeOffset? paUpdatedAt = null)
    {
        var deck = new Deck
        {
            PaId = paId, SourceUrl = $"https://piltoverarchive.com/decks/view/{paId}",
            Domains = domains, Views = views, Likes = likes, PaUpdatedAt = paUpdatedAt,
        };
        db.Decks.Add(deck);
        return deck;
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
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
