using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Parser voor de publieke Piltover Archive-deck-pagina's (#15).
/// De fixture is een echte pagina (2026-07-13), ingekort tot de relevante
/// RSC-chunks maar structuurgetrouw (conventie: fixtures spiegelen de live
/// vormen) — Next.js-flight-chunks, $D-datums, $undefined-referenties.</summary>
public class PiltoverDeckPageTests
{
    private static readonly string Fixture = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "pa-deck-2026-07-13.html"));

    [Fact]
    public void Parse_EchtePagina_LeestKopEnStatistieken()
    {
        var deck = PiltoverDeckPage.Parse(Fixture);

        Assert.NotNull(deck);
        Assert.Equal("b865434d-7247-41cd-aef8-0e8e4e4ec6c0", deck.Id);
        Assert.Equal("With Hammers.", deck.Name);
        Assert.Equal(74, deck.Views);
        Assert.Equal(0, deck.Likes);
        // "$D2026-06-08T10:09:44.648Z" — de flight-datumprefix moet eraf.
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 10, 9, 44, 648, TimeSpan.Zero), deck.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 13, 53, 29, 172, TimeSpan.Zero), deck.UpdatedAt);
        // Domeinen komen via de legend (PA noemt ze colors).
        Assert.Equal(["Body", "Order"], deck.Domains);
    }

    [Fact]
    public void Parse_EchtePagina_LeestAlleSecties()
    {
        var deck = PiltoverDeckPage.Parse(Fixture)!;

        // De live sectie-indeling: legend als los object, daarnaast zes
        // kaartsecties (bench kan leeg zijn; hier is hij gevuld).
        var perSectie = deck.Cards
            .GroupBy(c => c.Section)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, perSectie["legend"]);
        Assert.Equal(1, perSectie["champions"]);
        Assert.Equal(3, perSectie["battlefields"]);
        Assert.Equal(2, perSectie["runes"]);
        Assert.Equal(17, perSectie["maindeck"]);
        Assert.Equal(6, perSectie["sideboard"]);
        Assert.Equal(18, perSectie["bench"]);

        var legend = Assert.Single(deck.Cards, c => c.Section == "legend");
        Assert.Equal("UNL-203", legend.VariantNumber);
        Assert.Equal("Poppy, Keeper of the Hammer", legend.CardName);
        Assert.Equal(1, legend.Quantity);

        // Alt-art-variantnummer met letter-suffix, mét aantal.
        var rune = Assert.Single(deck.Cards, c => c.VariantNumber == "OGN-126a");
        Assert.Equal("runes", rune.Section);
        Assert.Equal("Body Rune", rune.CardName);
        Assert.Equal(8, rune.Quantity);
    }

    [Fact]
    public void Parse_PaginaZonderDeck_GeeftNull()
    {
        Assert.Null(PiltoverDeckPage.Parse("<html><body>niets</body></html>"));
        Assert.Null(PiltoverDeckPage.Parse(""));
        // Wel flight-chunks, geen deck-object.
        Assert.Null(PiltoverDeckPage.Parse(
            """<script>self.__next_f.push([1,"{\"page\":\"cards\"}"])</script>"""));
    }

    [Fact]
    public void Parse_DeckWoordInProse_TeltNietAlsDeck()
    {
        // '"deck":' met een niet-uuid-id (of zonder object) is geen deck —
        // de parser zoekt door tot een blok dat echt als deck parst.
        var html =
            """<script>self.__next_f.push([1,"{\"deck\":{\"id\":\"geen-uuid\"},\"artikel\":\"over een deck\"}"])</script>""";
        Assert.Null(PiltoverDeckPage.Parse(html));
    }

    [Fact]
    public void Parse_DeckOverChunkgrensHeen_WordtAaneengeregen()
    {
        // Het deck-object kan over een push-chunk-grens lopen: eerst plakken,
        // dan pas zoeken (de fixture heeft hem toevallig in één chunk).
        var html =
            """
            <script>self.__next_f.push([1,"{\"deck\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"na"])</script>
            <script>self.__next_f.push([1,"me\":\"Gesplitst\",\"views\":3,\"likes\":1}}"])</script>
            """;
        var deck = PiltoverDeckPage.Parse(html);

        Assert.NotNull(deck);
        Assert.Equal("Gesplitst", deck.Name);
        Assert.Equal(3, deck.Views);
    }

    [Fact]
    public void Parse_OntbrekendeVelden_WordenNullOfLeeg()
    {
        // Minimaal geldig deck: alleen een uuid-id. Alles daaromheen is
        // optioneel — ontbreken mag nooit een crash zijn.
        var html =
            """<script>self.__next_f.push([1,"{\"deck\":{\"id\":\"11111111-2222-3333-4444-555555555555\"}}"])</script>""";
        var deck = PiltoverDeckPage.Parse(html);

        Assert.NotNull(deck);
        Assert.Null(deck.Name);
        Assert.Null(deck.CreatedAt);
        Assert.Equal(0, deck.Views);
        Assert.Empty(deck.Domains);
        Assert.Empty(deck.Cards);
    }

    [Fact]
    public void Parse_EntryMetMeerdereVarianten_KiestDeGekozenVariant()
    {
        // De entry wijst met variantId naar één van card.cardVariants — die
        // wint van de eerste in de lijst.
        var html =
            """<script>self.__next_f.push([1,"{\"deck\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"maindeck\":[{\"variantId\":\"v2\",\"quantity\":2,\"card\":{\"name\":\"Testkaart\",\"cardVariants\":[{\"id\":\"v1\",\"variantNumber\":\"OGN-001\"},{\"id\":\"v2\",\"variantNumber\":\"OGN-001a\"}]}}]}}"])</script>""";
        var deck = PiltoverDeckPage.Parse(html)!;

        var entry = Assert.Single(deck.Cards);
        Assert.Equal("OGN-001a", entry.VariantNumber);
        Assert.Equal("Testkaart", entry.CardName);
        Assert.Equal(2, entry.Quantity);
    }

    [Fact]
    public void Parse_NulBytesInUserdata_WordenGesaneerd()
    {
        // Review-fix #15: een NUL-byte is legaal in JSON maar Postgres
        // weigert hem in text-kolommen — zonder wasstraat zou élke run
        // deterministisch op zo'n deck stranden. In de flight-chunk staat de
        // NUL dubbel-ge-escaped (\\u0000: een laag JS-string, een laag JSON);
        // pas JsonDocument maakt er een echte NUL van en Str/CleanText wast
        // hem eruit. Andere control chars worden een spatie (leesbaar).
        var html =
            """<script>self.__next_f.push([1,"{\"deck\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"name\":\"Gif\\u0000deck\",\"maindeck\":[{\"quantity\":1,\"card\":{\"name\":\"Kaart\\u0000naam\",\"cardVariants\":[{\"id\":\"v1\",\"variantNumber\":\"OGN\\u0000-001\"}]}}]}}"])</script>""";
        var deck = PiltoverDeckPage.Parse(html);

        Assert.NotNull(deck);
        Assert.Equal("Gifdeck", deck.Name);
        var entry = Assert.Single(deck.Cards);
        Assert.Equal("Kaartnaam", entry.CardName);
        Assert.Equal("OGN-001", entry.VariantNumber);
    }

    [Fact]
    public void CleanText_NulWeg_AndereControlCharsEenSpatie()
    {
        Assert.Equal("ab", PiltoverDeckPage.CleanText("a\0b"));
        Assert.Equal("a b", PiltoverDeckPage.CleanText("a\nb"));
        Assert.Equal("schoon", PiltoverDeckPage.CleanText("schoon"));
        Assert.Null(PiltoverDeckPage.CleanText(null));
        Assert.Equal("", PiltoverDeckPage.CleanText(""));
    }

    [Fact]
    public void Unescape_UnicodeEnStuurtekens()
    {
        // PA-namen bevatten unicode-escapes (【】 e.d.) en \n in beschrijvingen.
        Assert.Equal("【RQ】Hartford", PiltoverDeckPage.Unescape(
            @"\u3010RQ\u3011Hartford"));
        Assert.Equal("regel1\nregel2 \"quote\"", PiltoverDeckPage.Unescape(
            @"regel1\nregel2 \""quote\"""));
        // Onbekende escape verliest alleen de backslash — geen crash.
        Assert.Equal("x'y", PiltoverDeckPage.Unescape(@"x\'y"));
    }

    [Fact]
    public void SanitizeFlight_DatumsReferentiesEnDollars()
    {
        // $D-datum → kale datum; elke andere string die met één $ begint is
        // een flight-referentie ($undefined, $L…) → null. Gebruikerstekst met
        // een $ voorop serialiseert flight zelf als $$ — die dollar blijft;
        // een $ mídden in een string sowieso.
        Assert.Equal(
            """{"a":"2026-01-01T00:00:00.000Z","b":null,"c":null,"d":"$ 5 deck","e":"deck voor $ 5"}""",
            PiltoverDeckPage.SanitizeFlight(
                """{"a":"$D2026-01-01T00:00:00.000Z","b":"$undefined","c":"$L12","d":"$$ 5 deck","e":"deck voor $ 5"}"""));
    }
}

/// <summary>Sitemap-lezer van piltoverarchive.com (#15): index → shards,
/// shard → deck-uuids met lastmod (de basis voor gerichte versheid).</summary>
public class PiltoverSitemapTests
{
    [Fact]
    public void ShardUrls_AlleenEigenHost_EnAlleenSitemapPaden()
    {
        // De index is pagina-inhoud: een vreemde host of een pad buiten
        // /sitemap (zoals hun robots-disallowed /api/) mag er nooit doorheen.
        var xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://piltoverarchive.com/sitemap/0</loc></sitemap>
              <sitemap><loc>https://piltoverarchive.com/sitemap/1</loc></sitemap>
              <sitemap><loc>https://kwaadaardig.example/sitemap/0</loc></sitemap>
              <sitemap><loc>https://piltoverarchive.com/api/decks</loc></sitemap>
            </sitemapindex>
            """;
        Assert.Equal(
            ["https://piltoverarchive.com/sitemap/0", "https://piltoverarchive.com/sitemap/1"],
            PiltoverSitemap.ShardUrls(xml));
    }

    [Fact]
    public void DeckEntries_AlleenDeckUrls_MetLastmod()
    {
        // Live vorm (2026-07-13): shards mengen decks met andere pagina's;
        // decks dragen een lastmod.
        var xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://piltoverarchive.com/cards</loc>
                <lastmod>2026-07-13T18:44:44.631Z</lastmod>
              </url>
              <url>
                <loc>https://piltoverarchive.com/decks/view/99f9a2a9-b0fd-4399-bdfd-4327a030c6e3</loc>
                <lastmod>2026-06-25T01:22:05.612Z</lastmod>
                <changefreq>weekly</changefreq>
              </url>
              <url>
                <loc>https://piltoverarchive.com/decks/view/023499a9-5dae-4188-9e91-9e7b334fe3de</loc>
              </url>
              <url>
                <loc>https://piltoverarchive.com/decks/view/geen-uuid</loc>
              </url>
            </urlset>
            """;
        var entries = PiltoverSitemap.DeckEntries(xml);

        Assert.Equal(2, entries.Count);
        Assert.Equal("99f9a2a9-b0fd-4399-bdfd-4327a030c6e3", entries[0].Uuid);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 25, 1, 22, 5, 612, TimeSpan.Zero),
            entries[0].LastModified);
        // Zonder lastmod: entry telt mee, versheid valt terug op de 7-dagenregel.
        Assert.Equal("023499a9-5dae-4188-9e91-9e7b334fe3de", entries[1].Uuid);
        Assert.Null(entries[1].LastModified);
    }
}
