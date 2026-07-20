using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Lokale terugval voor de presentatievelden (#270): wat we kunnen
/// afleiden voor de ~141 kaarten die alléén via riftcodex binnenkomen.</summary>
public class CardPresentationTests
{
    // ---- afmetingen uit de URL (#269) -------------------------------------

    [Theory]
    // De echte Sanity-CDN-vorm: hex-hash, maat, extensie, querystring.
    [InlineData(
        "https://cmsassets.rgpub.io/sanity/images/dsfx7636/game_data_live/" +
        "89929cfa4417c99576477793529c6808af145919-744x1039.png?accountingTag=RB", 744, 1039)]
    // Battlefield: liggend (#269 — deze werden bijgesneden).
    [InlineData(
        "https://cmsassets.rgpub.io/sanity/images/dsfx7636/game_data_live/" +
        "7447b04d1e78192509e89e5ff3556368ea5c471a-1039x744.png?accountingTag=RB", 1039, 744)]
    [InlineData("https://example.com/kaart-744x1039.png", 744, 1039)]
    [InlineData("https://example.com/kaart-800x600.webp", 800, 600)]
    public void SizeFromUrl_ReadsSanityDimensions(string url, int width, int height)
    {
        var size = CardPresentation.SizeFromUrl(url);
        Assert.NotNull(size);
        Assert.Equal((width, height), (size.Value.Width, size.Value.Height));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/kaart.png")]
    // De hash zelf bevat hex en streepjes — geen maat, dus geen match.
    [InlineData("https://cmsassets.rgpub.io/…/7447b04d1e78192509e89e5ff3556368ea5c471a.png")]
    // Losse getallen zonder de -WxH.-vorm mogen niet als maat gelezen worden.
    [InlineData("https://example.com/2026-07-20.png")]
    public void SizeFromUrl_NullWhenUrlCarriesNoSize(string? url) =>
        Assert.Null(CardPresentation.SizeFromUrl(url));

    [Theory]
    [InlineData(1039, 744, true)]      // battlefield
    [InlineData(744, 1039, false)]     // unit/spell
    [InlineData(null, null, false)]    // onbekend telt als staand
    [InlineData(744, null, false)]
    public void IsLandscape_OnlyWhenBreedGroterDanHoog(int? w, int? h, bool expected) =>
        Assert.Equal(expected, CardPresentation.IsLandscape(w, h));

    // ---- alt-tekst --------------------------------------------------------

    [Fact]
    public void ComposeAltText_VolgtRiotsEigenBewoording()
    {
        // Riot schrijft: "Riftbound Battlefield: Abandoned Hall. <tekst>".
        var alt = CardPresentation.ComposeAltText(
            "Abandoned Hall", null, "Battlefield",
            "When a player plays a spell, they may give a unit +1 :rb_might: this turn.");
        Assert.Equal(
            "Riftbound Battlefield: Abandoned Hall. When a player plays a spell, " +
            "they may give a unit +1 [might] this turn.",
            alt);
    }

    [Fact]
    public void ComposeAltText_NeemtSupertypeMee()
    {
        var alt = CardPresentation.ComposeAltText("Aphelios, Exalted", "Champion", "Unit", null);
        Assert.Equal("Riftbound Champion Unit: Aphelios, Exalted.", alt);
    }

    [Fact]
    public void ComposeAltText_ZonderTypeEnZonderTekst()
    {
        // Token-kaarten hebben een lege type-lijst (unl-t04/t08).
        Assert.Equal("Riftbound card: Buff.",
            CardPresentation.ComposeAltText("Buff", null, null, null));
    }

    [Fact]
    public void ComposeAltText_MaaktIconTokensLeesbaar()
    {
        // Rauwe :rb_…:-tokens zijn onleesbaar voor een screenreader.
        var alt = CardPresentation.ComposeAltText(
            "Vanguard Captain", null, "Unit", "Play two 1 :rb_might: tokens for :rb_energy_2:.");
        Assert.Contains("[might]", alt);
        Assert.Contains("(2)", alt);
        Assert.DoesNotContain(":rb_", alt);
    }

    // ---- kleuren ----------------------------------------------------------

    [Theory]
    [InlineData("#222C44", "#222c44")]
    [InlineData("222C44", "#222c44")]
    [InlineData("#abc", "#abc")]
    public void NormalizeHexColor_NormaliseertNaarKleineLettersMetHekje(
        string input, string expected) =>
        Assert.Equal(expected, CardPresentation.NormalizeHexColor(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rood")]
    [InlineData("#12345")]
    // De waarde belandt in een style-attribuut: alles wat geen hexkleur is
    // wordt geweigerd, anders is dat een injectiepad.
    [InlineData("#fff;background:url(javascript:alert(1))")]
    [InlineData("red\"; onload=\"alert(1)")]
    public void NormalizeHexColor_WeigertAllesWatGeenHexkleurIs(string? input) =>
        Assert.Null(CardPresentation.NormalizeHexColor(input));
}
