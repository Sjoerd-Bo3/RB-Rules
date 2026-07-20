using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Deck-code-import (#264): een geplakte code wordt gedecodeerd, op
/// onze canonieke kaarten geprojecteerd en tegen dezelfde legaliteitsregels
/// gelegd als een PA-deck. De hoofdeis is dat élke ongeldige invoer als
/// uitlegbare fout terugkomt in plaats van als exceptie — het endpoint mapt
/// dat op 400, en dat mag nooit een 500 worden. EF InMemory, zelfde patroon
/// als DeckBrowserServiceTests.</summary>
public class DeckCodeServiceTests
{
    /// <summary>Historische versie 2-code uit de README van de bron-repo —
    /// bewijst dat we een échte code uit het ecosysteem accepteren, niet
    /// alleen wat onze eigen encoder produceert.</summary>
    private const string ReadmeV2Code =
        "CIAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHE";

    [Fact]
    public async Task DecodeAsync_EchteCodeUitHetEcosysteem_LevertHetDeckMetAantallen()
    {
        using var db = NewDb();
        await db.SaveChangesAsync();

        var result = await new DeckCodeService(db).DecodeAsync(ReadmeV2Code);

        Assert.Null(result.Error);
        Assert.NotNull(result.Deck);
        // Kai'Sa-voorbeelddeck: 56 kaarten hoofddeck + 8 sideboard.
        Assert.Equal(64, result.Deck!.CardCount);
        Assert.Equal(["maindeck", "sideboard"], result.Deck.Sections.Select(s => s.Section));
        Assert.Equal(56, result.Deck.Sections.Single(s => s.Section == "maindeck").Cards.Sum(c => c.Quantity));
    }

    [Fact]
    public async Task DecodeAsync_KoppeltKaartcodesAanOnzeCanoniekeKaarten()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-007-298", "Kai'Sa, Daughter of the Void");
        await db.SaveChangesAsync();

        var code = DeckCode.Encode(new DeckList([new("OGN-007", 3), new("OGN-999", 1)], []));
        var result = await new DeckCodeService(db).DecodeAsync(code);

        Assert.NotNull(result.Deck);
        var cards = result.Deck!.Sections.Single(s => s.Section == "maindeck").Cards;
        var gekoppeld = cards.Single(c => c.CardCode == "OGN-007");
        Assert.Equal("ogn-007-298", gekoppeld.CanonicalRiftboundId);
        Assert.Equal("Kai'Sa, Daughter of the Void", gekoppeld.CardName);
        // Onbekend is data, geen fout: rauwe code zonder naam, geteld als onbekend.
        var onbekend = cards.Single(c => c.CardCode == "OGN-999");
        Assert.Null(onbekend.CanonicalRiftboundId);
        Assert.Null(onbekend.CardName);
        Assert.Equal(1, result.Deck.UnknownCount);
        Assert.Equal("incomplete", result.Deck.Legality.Status);
    }

    [Fact]
    public async Task DecodeAsync_AltArtInDeCode_ResolvedNaarDeCanoniekeKaart()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-050-298", "Poppy, Keeper of the Hammer");
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-050a-298", Name = "Poppy, Keeper of the Hammer (Alternate Art)",
            SetId = "ogn", VariantOf = "ogn-050-298",
        });
        await db.SaveChangesAsync();

        var code = DeckCode.Encode(new DeckList([new("OGN-050a", 3)], []));
        var result = await new DeckCodeService(db).DecodeAsync(code);

        var card = Assert.Single(result.Deck!.Sections.Single(s => s.Section == "maindeck").Cards);
        Assert.Equal("ogn-050-298", card.CanonicalRiftboundId);
        Assert.Equal(0, result.Deck.UnknownCount);
        Assert.Equal("legal", result.Deck.Legality.Status);
    }

    [Fact]
    public async Task DecodeAsync_GebandeKaart_IsIllegaalMetReden()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-666-298", "Verboden Kaart");
        db.BanEntries.Add(new BanEntry
        {
            Name = "Verboden Kaart", CardRiftboundId = "ogn-666-298",
            Kind = "card", Format = "constructed", SourceUrl = "https://playriftbound.com/bans",
        });
        await db.SaveChangesAsync();

        var code = DeckCode.Encode(new DeckList([new("OGN-666", 3)], []));
        var result = await new DeckCodeService(db).DecodeAsync(code);

        Assert.Equal("illegal", result.Deck!.Legality.Status);
        var issue = Assert.Single(result.Deck.Legality.Issues);
        Assert.Equal(DeckLegalityIssue.Banned, issue.Reason);
        Assert.Equal("Verboden Kaart", issue.CardName);
    }

    /// <summary>Een ban in een ander format telt niet mee — het format reist
    /// mee naar dezelfde legaliteitscontext als de deck-browser.</summary>
    [Fact]
    public async Task DecodeAsync_FormatBepaaltWelkeBansTellen()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-777-298", "Alleen Limited Geband");
        db.BanEntries.Add(new BanEntry
        {
            Name = "Alleen Limited Geband", CardRiftboundId = "ogn-777-298",
            Kind = "card", Format = "limited", SourceUrl = "https://playriftbound.com/bans",
        });
        await db.SaveChangesAsync();

        var code = DeckCode.Encode(new DeckList([new("OGN-777", 3)], []));
        var svc = new DeckCodeService(db);

        Assert.Equal("legal", (await svc.DecodeAsync(code)).Deck!.Legality.Status);
        Assert.Equal("illegal", (await svc.DecodeAsync(code, format: "limited")).Deck!.Legality.Status);
    }

    [Fact]
    public async Task DecodeAsync_ChosenChampion_KrijgtEenEigenSectie()
    {
        using var db = NewDb();
        SeedLegalCard(db, "ogn-103-298", "Champion");
        await db.SaveChangesAsync();

        var code = DeckCode.Encode(new DeckList([new("OGN-103", 3)], [new("OGN-103", 1)], "OGN-103"));
        var result = await new DeckCodeService(db).DecodeAsync(code);

        Assert.Equal(
            ["maindeck", "sideboard", "chosen-champion"],
            result.Deck!.Sections.Select(s => s.Section));
        var champion = Assert.Single(result.Deck.Sections.Single(s => s.Section == "chosen-champion").Cards);
        Assert.Equal("OGN-103", champion.CardCode);
        Assert.Equal(1, champion.Quantity);
    }

    // ── foutpaden: 400, nooit 500 ───────────────────────────────────────

    /// <summary>De kern van #264: elke vorm van rommel komt terug als
    /// <see cref="DeckCodeResult.Error"/>, niet als exceptie. Het endpoint mapt
    /// dat één-op-één op een 400 met uitleg; een ontsnappende exceptie zou
    /// een kale 500 zijn.</summary>
    [Theory]
    [InlineData(null)]                          // geen body-veld
    [InlineData("")]                            // leeg
    [InlineData("   ")]                         // alleen witruimte
    [InlineData("dit is geen deck-code")]       // ongeldige tekens
    [InlineData("CM$$AA")]                      // ongeldig teken midden in
    [InlineData("A")]                           // te kort voor een deck
    [InlineData("CMAAAAAAAAAQCAAAA4AACAIAAB")]  // afgekapte, verder geldige code
    [InlineData("D4AAAA")]                      // onbekend format/versie
    public async Task DecodeAsync_OngeldigeInvoer_GeeftUitlegbareFoutZonderExceptie(string? code)
    {
        using var db = NewDb();
        await db.SaveChangesAsync();

        var result = await new DeckCodeService(db).DecodeAsync(code);

        Assert.Null(result.Deck);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task DecodeAsync_OnrealistischLangeInvoer_WordtGeweigerdZonderTeDecoderen()
    {
        using var db = NewDb();
        await db.SaveChangesAsync();

        var result = await new DeckCodeService(db)
            .DecodeAsync(new string('A', DeckCodeService.MaxCodeLength + 1));

        Assert.Null(result.Deck);
        Assert.Contains("te lang", result.Error);
    }

    // ── seed-helpers ────────────────────────────────────────────────────

    private static void SeedLegalCard(RbRulesDbContext db, string riftboundId, string name)
    {
        if (!db.CardSets.Local.Any(s => s.SetId == "ogn") && db.CardSets.Find("ogn") is null)
            db.CardSets.Add(new CardSet { SetId = "ogn", Name = "Origins", PublishedOn = new DateOnly(2025, 1, 1) });
        db.Cards.Add(new Card { RiftboundId = riftboundId, Name = name, SetId = "ogn" });
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
