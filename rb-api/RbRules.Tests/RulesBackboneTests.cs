using RbRules.Domain;

namespace RbRules.Tests;

public class RuleSectionParserTests
{
    [Fact]
    public void Parse_SplitsNumberedSections()
    {
        var text = """
            601. Combat
            601.1. Combat begint met een showdown.
            601.2. Reacties mogen gespeeld worden.
            601.2.d. Hidden units onthullen eerst.
            602. Schade
            602.1. Schade is permanent tot einde beurt.
            """;
        var sections = RuleSectionParser.Parse(text);
        var codes = sections.Select(s => s.Code).ToList();
        Assert.Contains("601", codes);
        Assert.Contains("601.1", codes);
        Assert.Contains("601.2.d", codes);
        Assert.Contains("602.1", codes);
        var s601_2d = sections.First(s => s.Code == "601.2.d");
        Assert.Contains("Hidden units onthullen eerst", s601_2d.Text);
    }

    [Fact]
    public void Parse_NormalizesAttachedLetterSubsections()
    {
        // PDF-extractie levert soms "601.2d." zonder punt vóór de letter —
        // dit was precies de kapotte regex uit de PoP-audit.
        var sections = RuleSectionParser.Parse("601.2d. Subsectie met letter.\n700. Volgende.");
        Assert.Contains(sections, s => s.Code == "601.2.d");
    }

    [Fact]
    public void Parse_KeepsPreambleAsIntro()
    {
        var sections = RuleSectionParser.Parse(
            "Riftbound Core Rules — dit document beschrijft alle spelregels en definities van het spel.\n" +
            "100. Eerste regel.");
        Assert.Equal("intro", sections[0].Code);
        Assert.Equal("100", sections[1].Code);
    }

    [Fact]
    public void Parse_SplitsOversizedSectionsKeepingCode()
    {
        var big = "700. " + string.Join(" ", Enumerable.Repeat("Dit is een lange zin over regels.", 200));
        var sections = RuleSectionParser.Parse(big);
        Assert.True(sections.Count > 1);
        Assert.All(sections, s => Assert.Equal("700", s.Code));
    }

    [Fact]
    public void Parse_PlainTextWithoutHeaders_StillChunks()
    {
        var sections = RuleSectionParser.Parse("Gewoon lopende tekst zonder nummering, maar wel inhoud.");
        Assert.Single(sections);
        Assert.Equal("", sections[0].Code);
    }

    [Theory]
    [InlineData("601.2d", "601.2.d")]
    [InlineData("601.2.d", "601.2.d")]
    [InlineData("601", "601")]
    public void NormalizeCode_Works(string input, string expected) =>
        Assert.Equal(expected, RuleSectionParser.NormalizeCode(input));
}

public class PdfDiscoveryTests
{
    private static readonly Uri Hub = new("https://riftbound.leagueoflegends.com/en-us/rules-hub/");

    [Fact]
    public void FindPdfUrl_MatchesKeywordAndResolvesRelative()
    {
        var html = """
            <a href="/dam/riftbound-core-rules-v1.2.pdf">Core Rules</a>
            <a href="/dam/riftbound-tournament-rules.pdf">Tournament Rules</a>
            """;
        Assert.Equal(
            "https://riftbound.leagueoflegends.com/dam/riftbound-core-rules-v1.2.pdf",
            PdfDiscovery.FindPdfUrl(html, "core", Hub));
        Assert.Contains("tournament", PdfDiscovery.FindPdfUrl(html, "tournament", Hub)!);
    }

    [Fact]
    public void FindPdfUrl_NullWhenNoMatchAmongMultiple()
    {
        var html = """<a href="/a.pdf">A</a><a href="/b.pdf">B</a>""";
        Assert.Null(PdfDiscovery.FindPdfUrl(html, "core", Hub));
    }

    [Fact]
    public void FindPdfUrl_FallsBackToSingleCandidate()
    {
        var html = """<a href="/only-rules-doc.pdf">Rules</a>""";
        Assert.NotNull(PdfDiscovery.FindPdfUrl(html, "core", Hub));
    }
}

public class BanErrataExtractorTests
{
    [Fact]
    public void ParseBans_ExtractsCardsAndBattlefields()
    {
        var bans = BanErrataExtractor.ParseBans("""
            [{"name": "Draven Vanquisher", "kind": "card"},
             {"name": "Dreaming Tree", "kind": "battlefield"}]
            """);
        Assert.Equal(2, bans.Count);
        Assert.Equal("card", bans[0].Kind);
        Assert.Equal("battlefield", bans[1].Kind);
    }

    [Fact]
    public void ParseBans_ClampsUnknownKindToCard()
    {
        var bans = BanErrataExtractor.ParseBans("""[{"name": "X", "kind": "planeswalker"}]""");
        Assert.Equal("card", bans[0].Kind);
    }

    [Fact]
    public void ParseErrata_SkipsIncompleteItems()
    {
        var errata = BanErrataExtractor.ParseErrata("""
            [{"cardName": "Adaptatron", "newText": "Nieuwe tekst."},
             {"cardName": "Zonder tekst"}]
            """);
        Assert.Single(errata);
    }

    [Fact]
    public void Parse_ReturnsEmptyOnGarbage()
    {
        Assert.Empty(BanErrataExtractor.ParseBans("geen json"));
        Assert.Empty(BanErrataExtractor.ParseErrata("{}"));
    }
}
