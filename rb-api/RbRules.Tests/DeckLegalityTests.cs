using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Deck-legaliteit (#15 fase 3, spoor A): een deck is legaal als al
/// zijn kaarten in een legale set zitten en geen enkele op de banlijst staat;
/// onbekende/niet-gekoppelde kaarten en sets zonder bekende releasedatum
/// (Announced) maken een deck nooit "illegaal" — alleen "onvolledig". Puur,
/// zonder database (zelfde patroon als SetLegalityTests).</summary>
public class DeckLegalityTests
{
    private static readonly DateOnly Today = new(2026, 7, 14);

    private static DeckLegalityCard Legal(string code = "ogn-001-298") =>
        new(code, "Body Rune", "ogn-001-298", new DateOnly(2026, 1, 1), Banned: false);

    [Fact]
    public void Evaluate_AlleKaartenLegaalEnOngeband_IsLegaal()
    {
        var cards = new[]
        {
            Legal("ogn-001-298"),
            Legal("ogn-002-298") with { SetPublishedOn = new DateOnly(2025, 6, 1) },
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Legal, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.UnknownCount);
        Assert.Equal("legal", DeckLegalityResult.Key(result.Status));
    }

    [Fact]
    public void Evaluate_GebandeKaart_IsIllegaalMetBanReden()
    {
        var cards = new[]
        {
            Legal(),
            Legal("ogn-666-298") with { CardName = "Verboden Kaart", Banned = true },
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Illegal, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("ogn-666-298", issue.CardCode);
        Assert.Equal("Verboden Kaart", issue.CardName);
        Assert.Equal(DeckLegalityIssue.Banned, issue.Reason);
        Assert.Equal("illegal", DeckLegalityResult.Key(result.Status));
    }

    [Fact]
    public void Evaluate_KaartUitNogNietVerschenenSet_IsIllegaalMetNotYetLegalReden()
    {
        var cards = new[]
        {
            Legal(),
            Legal("van-001-298") with
            {
                CardName = "Toekomstkaart", SetPublishedOn = Today.AddDays(30),
            },
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Illegal, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("van-001-298", issue.CardCode);
        Assert.Equal(DeckLegalityIssue.NotYetLegal, issue.Reason);
    }

    [Fact]
    public void Evaluate_OpReleasedagZelf_IsLegaal()
    {
        // SetLegality.StatusFor: publishedOn <= today is al legaal.
        var cards = new[] { Legal() with { SetPublishedOn = Today } };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Legal, result.Status);
    }

    [Fact]
    public void Evaluate_NietGekoppeldeKaart_IsOnvolledigNietIllegaal()
    {
        var cards = new[]
        {
            Legal(),
            new DeckLegalityCard("unl-t07", null, null, null, Banned: false),
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Incomplete, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(1, result.UnknownCount);
        Assert.Equal("incomplete", DeckLegalityResult.Key(result.Status));
    }

    [Fact]
    public void Evaluate_SetZonderBekendeReleasedatum_IsOnvolledigNietIllegaal()
    {
        // Announced (PublishedOn null) mag nooit een harde "niet legaal"-claim
        // worden — dat kan net zo goed een oude set zonder datum zijn.
        var cards = new[]
        {
            Legal(),
            Legal("ann-001-298") with { CardName = "Aangekondigde Kaart", SetPublishedOn = null },
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Incomplete, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(1, result.UnknownCount);
    }

    [Fact]
    public void Evaluate_GebandeKaartWintVanOnbekende_BlijftIllegaal()
    {
        var cards = new[]
        {
            Legal("ogn-666-298") with { Banned = true },
            new DeckLegalityCard("unl-t07", null, null, null, Banned: false),
        };

        var result = DeckLegality.Evaluate(cards, Today);

        Assert.Equal(DeckLegalityStatus.Illegal, result.Status);
        Assert.Single(result.Issues);
        Assert.Equal(1, result.UnknownCount);
    }

    [Fact]
    public void Evaluate_LeegDeck_IsLegaal()
    {
        var result = DeckLegality.Evaluate([], Today);

        Assert.Equal(DeckLegalityStatus.Legal, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.UnknownCount);
    }
}
