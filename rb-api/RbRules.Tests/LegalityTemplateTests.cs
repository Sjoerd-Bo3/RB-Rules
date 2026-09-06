using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Het deterministische legaliteitsantwoord (#383): gezaghebbende
/// tabellen (banlijst, sets) zonder taalmodel ertussen.</summary>
public class LegalityTemplateTests
{
    private static readonly DateOnly Vandaag = new(2026, 9, 6);

    [Theory]
    [InlineData("is Viktor banned?", true)]
    [InlineData("Mag ik Viktor spelen in constructed?", true)]
    [InlineData("Is Viktor legaal?", true)]
    [InlineData("hoeveel kopieën van Viktor mag ik spelen?", false)]
    [InlineData("mag ik 4x Viktor in mijn deck?", false)]
    [InlineData("is een playset Viktor toegestaan?", false)]
    [InlineData("deckbouw: mag Viktor erin?", false)]
    public void Applies_AlleenZuivereLegaliteitsvragen(string vraag, bool verwacht) =>
        Assert.Equal(verwacht, LegalityTemplate.Applies(vraag, recognisedCards: 1));

    [Fact]
    public void Applies_ZonderHerkendeKaart_Nooit() =>
        Assert.False(LegalityTemplate.Applies("is viktor banned?", recognisedCards: 0));

    [Fact]
    public void Build_GebandeKaart_OordeelUitlegEnCitaat()
    {
        var r = LegalityTemplate.Build(
            [new("Viktor", Banned: true, "constructed", new DateOnly(2026, 7, 16), "Origins", new DateOnly(2025, 10, 1))],
            Vandaag, "Rules Hub", "https://playriftbound.com/hub");

        Assert.StartsWith("**Oordeel:** Viktor is niet toegestaan: de kaart staat op de banlijst.", r.Answer);
        Assert.Contains("**Zekerheid:** Bevestigd", r.Answer);
        Assert.Contains("staat op de actuele banlijst [1] voor constructed, sinds 16 juli 2026.", r.Answer);
        Assert.Contains("legaal sinds 1 oktober 2025", r.Answer);
        Assert.DoesNotContain("### Let op", r.Answer);
        Assert.Equal("https://playriftbound.com/hub", r.SourceUrl);
    }

    [Fact]
    public void Build_NietGebandVerschenenSet_Toegestaan()
    {
        var r = LegalityTemplate.Build(
            [new("Teemo", Banned: false, null, null, "Origins", new DateOnly(2025, 10, 1))],
            Vandaag, "Rules Hub", "https://playriftbound.com/hub");

        Assert.StartsWith("**Oordeel:** Teemo is toegestaan.", r.Answer);
        Assert.Contains("Teemo staat niet op de actuele banlijst [1].", r.Answer);
        Assert.DoesNotContain("### Let op", r.Answer);
    }

    [Fact]
    public void Build_ToekomstigeSet_NogNietToegestaanMetLetOp()
    {
        var r = LegalityTemplate.Build(
            [new("Nieuwe Held", Banned: false, null, null, "Spiritforged", new DateOnly(2026, 11, 1))],
            Vandaag, "Rules Hub", "https://playriftbound.com/hub");

        Assert.StartsWith("**Oordeel:** Nieuwe Held is nog niet toegestaan: de set is nog niet verschenen.", r.Answer);
        Assert.Contains("pas legaal vanaf 1 november 2026", r.Answer);
        Assert.Contains("### Let op", r.Answer);
        Assert.Contains("Tot de release van Spiritforged", r.Answer);
    }

    [Fact]
    public void Build_OnbekendeReleasedatum_DoetGeenHardeClaim()
    {
        // SetLegality.Announced: een onbekende datum kan een allang verschenen
        // set zijn — dus "toegestaan" op de banlijst, maar geen datumclaim.
        var r = LegalityTemplate.Build(
            [new("Oude Kaart", Banned: false, null, null, "Origins", null)],
            Vandaag, "Rules Hub", "https://playriftbound.com/hub");

        Assert.StartsWith("**Oordeel:** Oude Kaart is toegestaan.", r.Answer);
        Assert.Contains("Oude Kaart komt uit Origins.", r.Answer);
        Assert.DoesNotContain("legaal sinds", r.Answer);
        Assert.DoesNotContain("pas legaal", r.Answer);
    }

    [Fact]
    public void Build_MeerdereKaartenGemengd_LetOpVermeldtHetVerschil()
    {
        var r = LegalityTemplate.Build(
            [
                new("Viktor", Banned: true, "constructed", null, "Origins", new DateOnly(2025, 10, 1)),
                new("Teemo", Banned: false, null, null, "Origins", new DateOnly(2025, 10, 1)),
            ],
            Vandaag, "Rules Hub", "https://playriftbound.com/hub");

        Assert.Contains("Viktor is niet toegestaan", r.Answer);
        Assert.Contains("Teemo is toegestaan.", r.Answer);
        Assert.Contains("Niet alle genoemde kaarten hebben dezelfde status", r.Answer);
    }

    [Fact]
    public void Build_ZonderKaarten_Weigert() =>
        Assert.Throws<ArgumentException>(() =>
            LegalityTemplate.Build([], Vandaag, "Rules Hub", "https://x"));
}
