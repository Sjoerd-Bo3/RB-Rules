using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Regressietests voor de deck-code-port (#15). De verwachte strings
/// zijn gegenereerd met de referentie-implementatie zelf
/// (Piltover-Archive/RiftboundDeckCodes v1.3.0, TypeScript): elke encode-vector
/// is daar letterlijk uitgekomen, zodat onze codes byte-voor-byte compatibel
/// zijn met het ecosysteem (PiltoverArchive-app e.d.).</summary>
public class DeckCodeTests
{
    /// <summary>Het Kai'Sa-voorbeelddeck uit de README van de bron-repo.</summary>
    private static readonly List<DeckListEntry> KaisaMain =
    [
        new("OGN-007", 7),
        new("OGN-089", 5),
        new("OGN-004", 3),
        new("OGN-009", 3),
        new("OGN-012", 3),
        new("OGN-027", 3),
        new("OGN-029", 3),
        new("OGN-087", 3),
        new("OGN-095", 3),
        new("OGN-096", 3),
        new("OGN-103", 3),
        new("OGN-104", 3),
        new("OGN-116", 3),
        new("OGN-039", 2),
        new("OGN-122", 2),
        new("OGN-248", 2),
        new("OGN-013", 1),
        new("OGN-247", 1),
        new("OGN-280", 1),
        new("OGN-288", 1),
        new("OGN-292", 1),
    ];

    private static readonly List<DeckListEntry> KaisaSideboard =
    [
        new("OGN-022", 2),
        new("OGN-024", 2),
        new("OGN-093", 2),
        new("OGN-088", 1),
        new("OGN-114", 1),
    ];

    /// <summary>Historische versie 2-code uit de README van de bron-repo
    /// (gemaakt vóór chosen champion bestond).</summary>
    private const string ReadmeV2Code =
        "CIAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHE";

    // ---- Encode: exacte referentiestrings -------------------------------

    [Fact]
    public void Encode_KaisaMainOnly_MatchesReference()
    {
        var code = DeckCode.Encode(new DeckList(KaisaMain, []));
        Assert.Equal("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAAAAA", code);
    }

    [Fact]
    public void Encode_KaisaWithSideboard_MatchesReference()
    {
        var code = DeckCode.Encode(new DeckList(KaisaMain, KaisaSideboard));
        Assert.Equal("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHEAA", code);
    }

    [Fact]
    public void Encode_KaisaWithChosenChampion_MatchesReference()
    {
        var code = DeckCode.Encode(new DeckList(KaisaMain, KaisaSideboard, "OGN-103"));
        Assert.Equal("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHEAIAABTQ", code);
    }

    [Fact]
    public void Encode_MixedSetsAndVariants_MatchesReference()
    {
        // Voorbeeld 5 uit examples/basic-usage.ts van de bron-repo.
        var deck = new DeckList(
            [
                new("OGN-001", 3),
                new("OGN-050a", 2),
                new("OGS-022s", 2),
                new("SFD-015", 1),
                new("SFD-100b", 1),
                new("ARC-042", 2),
            ],
            []);
        Assert.Equal("CMAAAAAAAAAAAAAAAEAQAAABAMAQAAJSAEAQEFQBAIACUAQBAMAA6AIDANSAAAAAAA", DeckCode.Encode(deck));
    }

    [Fact]
    public void Encode_RuneDeck_UsesVersion4_MatchesReference()
    {
        // R-genummerde runes dwingen versie 4 af (vlag-byte per kaartnummer);
        // dekt meteen de nieuwste sets UNL, VEN en RAD.
        var deck = new DeckList(
            [
                new("SFD-R02", 7),
                new("UNL-R05a", 5),
                new("OGN-095", 3),
                new("VEN-001", 2),
                new("RAD-123", 1),
            ],
            []);
        var code = DeckCode.Encode(deck);
        Assert.Equal("CQAAAAAAAAAQCAYAAEBAAAIBAQAQCBIAAEAQAAAAL4AQCBIAAAAQCAIGAAAHWAAAAAAA", code);
        Assert.StartsWith("CQ", code); // eerste byte 0x14 = format 1, versie 4
    }

    [Fact]
    public void Encode_RuneChampion_MatchesReference()
    {
        var deck = new DeckList(
            [
                new("SFD-R02", 7),
                new("UNL-R05a", 5),
                new("OGN-095", 3),
                new("VEN-001", 2),
                new("RAD-123", 1),
            ],
            [],
            "SFD-R02");
        Assert.Equal("CQAAAAAAAAAQCAYAAEBAAAIBAQAQCBIAAEAQAAAAL4AQCBIAAAAQCAIGAAAHWAAAAAAQGAABAI", DeckCode.Encode(deck));
    }

    [Fact]
    public void Encode_EmptyDeck_MatchesReference()
    {
        var code = DeckCode.Encode(new DeckList([], []));
        Assert.Equal("CMAAAAAAAAAAAAAAAAAAAAAAAAAA", code);
        Assert.StartsWith("CM", code); // eerste byte 0x13 = format 1, versie 3
    }

    [Fact]
    public void Encode_TwaalfKopieen_MatchesReference()
    {
        // De bovengrens van het main deck (12, voor runes) moet meedoen.
        var code = DeckCode.Encode(new DeckList([new("OGN-300", 12)], []));
        Assert.Equal("CMAQCAAAVQBAAAAAAAAAAAAAAAAAAAAAAAAA", code);
    }

    [Fact]
    public void Encode_SignedSterEnS_ZijnEquivalent()
    {
        // "s" en "*" zijn twee notaties voor dezelfde signed-variant (id 2).
        var withS = DeckCode.Encode(new DeckList([new("OGN-007s", 1)], []));
        var withStar = DeckCode.Encode(new DeckList([new("OGN-007*", 1)], []));
        Assert.Equal("CMAAAAAAAAAAAAAAAAAACAIAAIDQAAAAAA", withS);
        Assert.Equal(withS, withStar);
    }

    [Fact]
    public void Encode_IsVolgordeOnafhankelijk()
    {
        // Canonieke sortering (set, variant, nummer) maakt de code
        // deterministisch: zelfde deck in willekeurige volgorde = zelfde code.
        var shuffled = new DeckList([.. KaisaMain.AsEnumerable().Reverse()], [.. KaisaSideboard.AsEnumerable().Reverse()]);
        var ordered = new DeckList(KaisaMain, KaisaSideboard);
        Assert.Equal(DeckCode.Encode(ordered), DeckCode.Encode(shuffled));
    }

    [Fact]
    public void Encode_RunesEnNormaleNummersInEenGroep_MatchesReference()
    {
        // Zelfde set/variant/aantal: normale nummers sorteren vóór runenummers
        // (numerieke collatie van de referentie).
        var deck = new DeckList(
            [
                new("SFD-R02", 3),
                new("SFD-015", 3),
                new("SFD-R01", 3),
                new("SFD-120", 3),
            ],
            []);
        var code = DeckCode.Encode(deck);
        Assert.Equal("CQAAAAAAAAAAAAAAAECAGAAAB4AHQAIBAEBAAAAAAAAAA", code);

        var decoded = DeckCode.Decode(code);
        Assert.Equal(
            new[] { "SFD-015", "SFD-120", "SFD-R01", "SFD-R02" },
            decoded.MainDeck.Select(c => c.CardCode));
    }

    // ---- Decode ----------------------------------------------------------

    [Fact]
    public void Decode_ReadmeV2Code_LevertKaisaDeckMetSideboard()
    {
        var decoded = DeckCode.Decode(ReadmeV2Code);

        // Decodevolgorde is canoniek: aantallen aflopend, nummers oplopend.
        Assert.Equal(
            KaisaMain.OrderByDescending(c => c.Count).ThenBy(c => c.CardCode, StringComparer.Ordinal),
            decoded.MainDeck);
        Assert.Equal(
            KaisaSideboard.OrderByDescending(c => c.Count).ThenBy(c => c.CardCode, StringComparer.Ordinal),
            decoded.Sideboard);
        // Versie 2 kende nog geen chosen champion.
        Assert.Null(decoded.ChosenChampion);
    }

    [Fact]
    public void Decode_ChampionCode_LevertChampion()
    {
        var decoded = DeckCode.Decode(
            "CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHEAIAABTQ");
        Assert.Equal("OGN-103", decoded.ChosenChampion);
        Assert.Equal(21, decoded.MainDeck.Count);
        Assert.Equal(5, decoded.Sideboard.Count);
    }

    [Fact]
    public void Decode_ChampionMetVariant_BehoudtSuffix()
    {
        var decoded = DeckCode.Decode("CMAAAAAAAAAAAAAAAEAQAALHAAAAAAAAAEAACZY");
        Assert.Equal(new[] { new DeckListEntry("OGN-103a", 3) }, decoded.MainDeck);
        Assert.Equal("OGN-103a", decoded.ChosenChampion);
    }

    [Fact]
    public void Decode_SignedSuffixOptie_GeldtOokVoorChampion()
    {
        const string code = "CMAAAAAAAAAAAAAAAAAACAIAAIDQAAAAAEAAEBY";

        var standaard = DeckCode.Decode(code);
        Assert.Equal(new[] { new DeckListEntry("OGN-007s", 1) }, standaard.MainDeck);
        Assert.Equal("OGN-007s", standaard.ChosenChampion);

        var ster = DeckCode.Decode(code, signedSuffix: '*');
        Assert.Equal(new[] { new DeckListEntry("OGN-007*", 1) }, ster.MainDeck);
        Assert.Equal("OGN-007*", ster.ChosenChampion);
    }

    [Fact]
    public void Decode_RuneDeck_LevertRNummersMetPadding()
    {
        var decoded = DeckCode.Decode("CQAAAAAAAAAQCAYAAEBAAAIBAQAQCBIAAEAQAAAAL4AQCBIAAAAQCAIGAAAHWAAAAAAA");
        Assert.Equal(
            new[]
            {
                new DeckListEntry("SFD-R02", 7),
                new DeckListEntry("UNL-R05a", 5),
                new DeckListEntry("OGN-095", 3),
                new DeckListEntry("VEN-001", 2),
                new DeckListEntry("RAD-123", 1),
            },
            decoded.MainDeck);
        Assert.Empty(decoded.Sideboard);
    }

    [Fact]
    public void Decode_KleineLetters_WordtGeaccepteerd()
    {
        // De referentie decodeert hoofdletterongevoelig.
        var decoded = DeckCode.Decode("cmaaaaaaaaaaaaaaaaaacaiaaaaqaaaaaa");
        Assert.Equal(new[] { new DeckListEntry("OGN-001", 1) }, decoded.MainDeck);
    }

    [Fact]
    public void Decode_LeegDeck_LevertLegeLijsten()
    {
        var decoded = DeckCode.Decode("CMAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.Empty(decoded.MainDeck);
        Assert.Empty(decoded.Sideboard);
        Assert.Null(decoded.ChosenChampion);
    }

    // ---- Roundtrips ------------------------------------------------------

    [Theory]
    [InlineData("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAAAAA")]
    [InlineData("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHEAA")]
    [InlineData("CMAAAAAAAAAQCAAAA4AACAIAABMQAAILAAAAICIMDMOVOX3AM5UHIAIDAAACO6XYAEAQKAAABX3QDGACUABKIAQAAEBQAAAWDBOQCAQAABMHEAIAABTQ")]
    [InlineData("CMAAAAAAAAAAAAAAAEAQAAABAMAQAAJSAEAQEFQBAIACUAQBAMAA6AIDANSAAAAAAA")]
    [InlineData("CQAAAAAAAAAQCAYAAEBAAAIBAQAQCBIAAEAQAAAAL4AQCBIAAAAQCAIGAAAHWAAAAAAA")]
    [InlineData("CQAAAAAAAAAQCAYAAEBAAAIBAQAQCBIAAEAQAAAAL4AQCBIAAAAQCAIGAAAHWAAAAAAQGAABAI")]
    [InlineData("CMAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("CMAQCAAAVQBAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("CMAAAAAAAAAAAAAAAEAQAALHAAAAAAAAAEAACZY")]
    [InlineData("CMAAAAAAAAAAAAAAAAAACAIAAIDQAAAAAEAAEBY")]
    [InlineData("CQAAAAAAAAAAAAAAAECAGAAAB4AHQAIBAEBAAAAAAAAAA")]
    public void Roundtrip_V3EnV4Codes_ZijnStabiel(string code)
    {
        // Decode → encode reproduceert de code exact (canonieke volgorde).
        Assert.Equal(code, DeckCode.Encode(DeckCode.Decode(code)));
    }

    [Fact]
    public void Roundtrip_V2Code_HercodeertAlsV3MetZelfdeInhoud()
    {
        // Een historische v2-code hercodeert als v3 (de encoder schrijft nooit
        // oude versies); de inhoud moet identiek blijven.
        var decoded = DeckCode.Decode(ReadmeV2Code);
        var recoded = DeckCode.Encode(decoded);
        Assert.StartsWith("CM", recoded);

        var opnieuw = DeckCode.Decode(recoded);
        Assert.Equal(decoded.MainDeck, opnieuw.MainDeck);
        Assert.Equal(decoded.Sideboard, opnieuw.Sideboard);
        Assert.Null(opnieuw.ChosenChampion);
    }

    // ---- Foutpaden -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_LegeInvoer_GooitNetteFout(string? code)
    {
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode(code));
        Assert.Contains("Lege", ex.Message);
    }

    [Fact]
    public void Decode_TeKorteCode_GooitNetteFout()
    {
        // Eén base32-teken levert nog geen hele byte op.
        Assert.Throws<DeckCodeException>(() => DeckCode.Decode("C"));
    }

    [Theory]
    [InlineData("CM!AAA")] // teken buiten het alfabet
    [InlineData("CM0AAA")] // 0 en 1 zitten niet in RFC 4648-base32
    [InlineData("CM AAA")] // spatie middenin
    public void Decode_OngeldigTeken_GooitNetteFout(string code)
    {
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode(code));
        Assert.Contains("teken", ex.Message);
    }

    [Fact]
    public void Decode_OnbekendeVersie_WordtGeweigerd()
    {
        // Eerste byte 0x15 = format 1, versie 5 (hoger dan wij kennen).
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode("CV"));
        Assert.Contains("versie 5", ex.Message);
    }

    [Fact]
    public void Decode_OnbekendFormat_WordtGeweigerd()
    {
        // Eerste byte 0x23 = format 2 — bestaat niet.
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode("EMAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Contains("format 2", ex.Message);
    }

    [Fact]
    public void Decode_AfgekapteCode_GooitNetteFout()
    {
        // Prefix van een geldige code: de bytestroom eindigt middenin.
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode("CMAQCAAAVQ"));
        Assert.Contains("afgekapt", ex.Message);
    }

    [Fact]
    public void Decode_OnbekendeSetId_WordtGeweigerd()
    {
        // Handgemaakt: versie 3, één kaart in set-id 9 — een toekomstige set.
        // Bytes: 0x13, 11x groepstal 0, [1 groep, 1 kaart, set 9, variant 0,
        // nummer 1], sideboard 3x 0, champion 0.
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode("CMAAAAAAAAAAAAAAAAAACAIJAAAQAAAAAA"));
        Assert.Contains("set-id 9", ex.Message);
    }

    [Fact]
    public void Decode_OnbekendeVariantId_WordtGeweigerd()
    {
        // Handgemaakt: als set-id-test, maar met variant-id 4. Bewuste
        // afwijking van de TS-referentie, die de onbekende variant stilletjes
        // laat wegvallen en zo naar de verkeerde kaart decodeert.
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Decode("CMAAAAAAAAAAAAAAAAAACAIAAQAQAAAAAA"));
        Assert.Contains("variant-id 4", ex.Message);
    }

    [Fact]
    public void Decode_OngeldigeSignedSuffix_WordtGeweigerd()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeckCode.Decode("CMAAAAAAAAAAAAAAAAAAAAAAAAAA", signedSuffix: 'x'));
    }

    [Theory]
    [InlineData("INVALID-FORMAT")] // geen numeriek kaartnummer
    [InlineData("OGN007")] // geen streepje
    [InlineData("OGN-")] // leeg nummer
    [InlineData("-007")] // lege set
    [InlineData("OGN-007aa")] // dubbele variant
    public void Encode_OngeldigeKaartcode_GooitNetteFout(string cardCode)
    {
        var deck = new DeckList([new(cardCode, 1)], []);
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains(cardCode, ex.Message);
    }

    [Fact]
    public void Encode_OnbekendeSet_GooitNetteFout()
    {
        var deck = new DeckList([new("ZZZ-001", 1)], []);
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains("ZZZ", ex.Message);
    }

    [Fact]
    public void Encode_OnbekendeVariant_GooitNetteFout()
    {
        var deck = new DeckList([new("OGN-001x", 1)], []);
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains("variant", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    public void Encode_AantalBuitenBereikMainDeck_GooitNetteFout(int count)
    {
        // Bewuste afwijking van de TS-referentie, die zulke kaarten stilletjes
        // uit de code weglaat (dataverlies); wij weigeren expliciet.
        var deck = new DeckList([new("OGN-001", count)], []);
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains("aantal", ex.Message);
    }

    [Fact]
    public void Encode_AantalBuitenBereikSideboard_GooitNetteFout()
    {
        // Sideboard kent alleen aantallen 1–3 (runes mogen daar niet in).
        var deck = new DeckList([], [new("OGN-001", 4)]);
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains("sideboard", ex.Message);
    }

    [Fact]
    public void Encode_OngeldigeChampionCode_GooitNetteFout()
    {
        var deck = new DeckList([new("OGN-001", 1)], [], "ZZZ-001");
        var ex = Assert.Throws<DeckCodeException>(() => DeckCode.Encode(deck));
        Assert.Contains("ZZZ", ex.Message);
    }
}
