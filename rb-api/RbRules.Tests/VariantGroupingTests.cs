using RbRules.Domain;

namespace RbRules.Tests;

public class VariantGroupingTests
{
    private static Card Make(
        string id, string name, string? variantOf = null, string? rarity = null) => new()
    {
        RiftboundId = id, Name = name, VariantOf = variantOf, Rarity = rarity,
    };

    [Fact]
    public void ChooseCanonical_PrefersBaseNamePrinting()
    {
        var alt = Make("ogn-001a-298", "Viktor (Alternate Art)");
        var basis = Make("ogn-001-298", "Viktor");
        Assert.Same(basis, VariantGrouping.ChooseCanonical([alt, basis]));
    }

    [Fact]
    public void ChooseCanonical_PinnedCanonicalSurvivesNewReprint()
    {
        // Set 7-scenario (#57): 'aaa-…' zou op ordinale rangorde van 'ogn-…'
        // winnen, maar de bestaande canonieke printing is gepind via de
        // variant-verwijzing — geen churn in graph/interacties/embeddings.
        var canonical = Make("ogn-001-298", "Viktor");
        var variant = Make("ogn-001a-298", "Viktor (Alternate Art)", variantOf: "ogn-001-298");
        var reprint = Make("aaa-010-100", "Viktor");
        Assert.Same(canonical, VariantGrouping.ChooseCanonical([reprint, variant, canonical]));
    }

    [Fact]
    public void ChooseCanonical_FallsBackToRankingWhenPinIsGone()
    {
        // De gepinde printing is uit de bron verdwenen → gewone rangorde:
        // de naamloze printing wint van de alt-art.
        var variant = Make("ogn-001a-298", "Viktor (Alternate Art)", variantOf: "ogn-verdwenen");
        var reprint = Make("aaa-010-100", "Viktor");
        Assert.Same(reprint, VariantGrouping.ChooseCanonical([variant, reprint]));
    }

    [Fact]
    public void ChooseCanonical_ShowcaseLosesFromRegularRarity()
    {
        var showcase = Make("ogn-001-298", "Viktor", rarity: "Showcase");
        var gewoon = Make("ogn-002-298", "Viktor", rarity: "Epic");
        Assert.Same(gewoon, VariantGrouping.ChooseCanonical([showcase, gewoon]));
    }

    [Theory]
    [InlineData("ogn-119-298", 0)]
    [InlineData("ogn-119a-298", 1)]
    [InlineData("sfd-227-star-221", 1)]
    [InlineData("ven-sp3-006", 1)]
    public void AltPrintingRank_ClassifiesPrintings(string id, int expected)
    {
        Assert.Equal(expected, VariantGrouping.AltPrintingRank(Make(id, "Kaart")));
    }

    private static CardInteraction Interaction(string a, string b, string kind = "combo") =>
        new() { CardAId = a, CardBId = b, Kind = kind, Explanation = "uitleg" };

    [Fact]
    public void InteractionNeighbors_CanonicalizesVariantIds()
    {
        // Rij van vóór de variantgroepering: de interactie hangt aan de alt-art.
        var rows = new[] { Interaction("ogn-001-298", "ogn-050a-298") };
        var others = new Dictionary<string, Card>
        {
            ["ogn-050a-298"] = Make("ogn-050a-298", "Jinx (Alternate Art)", variantOf: "ogn-050-298"),
        };
        var result = VariantGrouping.InteractionNeighbors(
            rows, new HashSet<string> { "ogn-001-298" }, others);
        var n = Assert.Single(result);
        Assert.Equal("ogn-050-298", n.OtherId);
        Assert.Equal("Jinx", n.OtherName);
    }

    [Fact]
    public void InteractionNeighbors_DeduplicatesAfterCanonicalization()
    {
        // Twee oude rijen die na canonicalisatie op dezelfde buur uitkomen.
        var rows = new[]
        {
            Interaction("ogn-001-298", "ogn-050a-298"),
            Interaction("ogn-001a-298", "ogn-050-298", kind: "synergy"),
        };
        var others = new Dictionary<string, Card>
        {
            ["ogn-050a-298"] = Make("ogn-050a-298", "Jinx (Alternate Art)", variantOf: "ogn-050-298"),
            ["ogn-050-298"] = Make("ogn-050-298", "Jinx"),
        };
        var result = VariantGrouping.InteractionNeighbors(
            rows, new HashSet<string> { "ogn-001-298", "ogn-001a-298" }, others);
        var n = Assert.Single(result);
        Assert.Equal("ogn-050-298", n.OtherId);
    }

    [Fact]
    public void InteractionNeighbors_SkipsPairsWithinOwnVariantGroup()
    {
        // Pre-groepering kon een kaart met zijn eigen alt-art gepaard zijn.
        var rows = new[] { Interaction("ogn-001-298", "ogn-001a-298") };
        var others = new Dictionary<string, Card>
        {
            ["ogn-001a-298"] = Make("ogn-001a-298", "Viktor (Alternate Art)", variantOf: "ogn-001-298"),
        };
        var result = VariantGrouping.InteractionNeighbors(
            rows, new HashSet<string> { "ogn-001-298", "ogn-001a-298" }, others);
        Assert.Empty(result);
    }

    [Fact]
    public void InteractionNeighbors_UnknownCardStaysVisible()
    {
        // Dangling id (kaart niet meer in de bron) mag niet stil verdwijnen.
        var rows = new[] { Interaction("ogn-001-298", "ogn-999-298") };
        var result = VariantGrouping.InteractionNeighbors(
            rows, new HashSet<string> { "ogn-001-298" }, new Dictionary<string, Card>());
        var n = Assert.Single(result);
        Assert.Equal("ogn-999-298", n.OtherId);
        Assert.Equal("ogn-999-298", n.OtherName);
    }
}
