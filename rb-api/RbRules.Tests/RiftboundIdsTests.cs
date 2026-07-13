using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Id-vormen zoals ze écht in de bronnen staan (#144/#145): de
/// Riot-gallery levert basis/a/star/tokens/runes/sp-reeks, riftcodex codeert
/// signature-printings met een ster ("sfd-239*-221").</summary>
public class RiftboundIdsTests
{
    [Theory]
    [InlineData("sfd-239*-221", "sfd-239-star-221")]
    [InlineData("sfd-227*-221", "sfd-227-star-221")]
    // Al-canonieke vormen blijven byte-voor-byte intact (nulpadding incluis).
    [InlineData("sfd-239-star-221", "sfd-239-star-221")]
    [InlineData("ogn-074-298", "ogn-074-298")]
    [InlineData("ogn-119a-298", "ogn-119a-298")]
    [InlineData("unl-t04", "unl-t04")]
    [InlineData("ven-sp3-006", "ven-sp3-006")]
    // Onbekende vormen zijn data — nooit gokken.
    [InlineData("VEN", "VEN")]
    public void Normalize_RewritesOnlyTheRiftcodexStarForm(string input, string expected) =>
        Assert.Equal(expected, RiftboundIds.Normalize(input));

    [Theory]
    [InlineData("ogn-074-298", "OGN", 74, "", 298, false)]
    [InlineData("ogn-119a-298", "OGN", 119, "a", 298, false)]
    [InlineData("sfd-239-star-221", "SFD", 239, "star", 221, false)]
    // De riftcodex-ster parseert via de normalisatie naar dezelfde delen.
    [InlineData("sfd-239*-221", "SFD", 239, "star", 221, false)]
    // Overnumbered: nummer boven het settotaal, gewoon parseerbaar.
    [InlineData("sfd-249-221", "SFD", 249, "", 221, false)]
    public void TryParse_ParsesCardForms(
        string id, string set, int number, string suffix, int total, bool special)
    {
        Assert.True(RiftboundIds.TryParse(id, out var parts));
        Assert.Equal(new RiftboundIdParts(set, number, suffix, total, special), parts);
    }

    [Theory]
    // Tokens en runes hebben geen settotaal in het id.
    [InlineData("unl-t04", "UNL", 4, "t", null)]
    [InlineData("ven-r01", "VEN", 1, "r", null)]
    [InlineData("sfd-t03", "SFD", 3, "t", null)]
    // De sp-reeks draagt een eigen subtotaal ("ven-sp3-006" = 3 van 6).
    [InlineData("ven-sp3-006", "VEN", 3, "sp", 6)]
    public void TryParse_ParsesSpecialSeries(
        string id, string set, int number, string series, int? total)
    {
        Assert.True(RiftboundIds.TryParse(id, out var parts));
        Assert.Equal(new RiftboundIdParts(set, number, series, total, IsSpecial: true), parts);
    }

    [Theory]
    [InlineData("VEN")]          // set-facet, geen kaart (regressie: facet-import)
    [InlineData("")]
    [InlineData("ogn")]
    [InlineData("ogn-abc-xyz")]
    public void TryParse_RejectsUnknownForms(string id) =>
        Assert.False(RiftboundIds.TryParse(id, out _));
}
