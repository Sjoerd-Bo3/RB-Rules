using RbRules.Domain;

namespace RbRules.Tests;

public class SourceScoutTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsProposals()
    {
        var raw = """
            {"proposals": [
              {"url": "https://example.com/riftbound-judge-faq",
               "name": "Judge FAQ (example.com)",
               "type": "community",
               "motivation": "Verzameling judge-antwoorden op timing-vragen."},
              {"url": "https://uvsgames.com/riftbound/op-guide",
               "name": "Organized Play Guide (UVS)",
               "type": "partner",
               "motivation": "Toernooiprocedures van de OP-partner."}
            ]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(2, r.Count);
        Assert.Equal("https://example.com/riftbound-judge-faq", r[0].Url);
        Assert.Equal("Judge FAQ (example.com)", r[0].Name);
        Assert.Equal("community", r[0].Type);
        Assert.Equal("partner", r[1].Type);
    }

    [Fact]
    public void Parse_ResearchAnswerWithBronnenBlock_IgnoresTrailingText()
    {
        // Echte antwoordvorm: rb-ai's research-contract dwingt een
        // "Bronnen:"-sectie ná de JSON af (rb-ai/src/ai.ts). Die tekst mag de
        // JSON-extractie niet breken en levert zelf geen voorstellen op.
        var raw = """
            Hier zijn de gevonden bronnen:
            {"proposals": [{"url": "https://example.com/wiki/riftbound", "name": "Riftbound Wiki", "type": "community", "motivation": "Community-wiki met keyword-uitleg."}]}

            Bronnen:
            https://example.com/wiki/riftbound (geraadpleegd 2026-07-11)
            https://andere-site.com/zoekresultaten
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("https://example.com/wiki/riftbound", p.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ik heb geen nieuwe bronnen kunnen vinden.")]
    [InlineData("{not valid json}")]
    [InlineData("""{"iets_anders": true}""")]
    public void Parse_GarbageOutput_ReturnsNull(string raw) =>
        // null = onbruikbaar antwoord (degradatiepad); dat is iets anders dan
        // een geldige lege lijst ("niets nieuws gevonden").
        Assert.Null(SourceScout.Parse(raw));

    [Fact]
    public void Parse_EmptyProposals_ReturnsEmptyList()
    {
        var r = SourceScout.Parse("""{"proposals": []}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_BareArray_IsTolerated()
    {
        var r = SourceScout.Parse(
            """[{"url": "https://example.com/guide", "name": "Guide", "type": "community", "motivation": "x"}]""");
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("http://example.com/guide")]
    [InlineData("ftp://example.com/guide")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com/guide")]
    [InlineData("")]
    public void Parse_NonHttpsUrl_IsDropped(string url)
    {
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "{{url}}", "name": "x", "type": "community", "motivation": "x"}]}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_KnownUrls_AreExcluded_CaseAndSlashInsensitive()
    {
        var raw = """
            {"proposals": [
              {"url": "https://Example.com/Guide/", "name": "al bekend", "type": "community", "motivation": "x"},
              {"url": "https://example.com/nieuw", "name": "nieuw", "type": "community", "motivation": "x"}
            ]}
            """;
        var r = SourceScout.Parse(raw, ["https://example.com/guide"]);
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("https://example.com/nieuw", p.Url);
    }

    [Fact]
    public void Parse_DuplicatesWithinAnswer_AreDeduped()
    {
        var raw = """
            {"proposals": [
              {"url": "https://example.com/guide", "name": "a", "type": "community", "motivation": "x"},
              {"url": "https://example.com/guide/", "name": "b", "type": "community", "motivation": "x"}
            ]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Fact]
    public void Parse_CapsAtMaxProposals()
    {
        var items = string.Join(",", Enumerable.Range(1, 15).Select(i =>
            $$"""{"url": "https://example.com/p{{i}}", "name": "p{{i}}", "type": "community", "motivation": "x"}"""));
        var r = SourceScout.Parse($$"""{"proposals": [{{items}}]}""");
        Assert.NotNull(r);
        Assert.Equal(SourceScout.MaxProposals, r.Count);
    }

    [Theory]
    [InlineData("official", "official")]
    [InlineData("Partner", "partner")]
    [InlineData("fansite", "community")]   // onbekend label degradeert
    [InlineData(null, "community")]        // ontbrekend type ook
    public void Parse_TypeClampsToKnownVocabulary(string? type, string expected)
    {
        var typeJson = type is null ? "" : $""" "type": "{type}", """;
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "https://example.com/guide", "name": "x", {{typeJson}} "motivation": "x"}]}""");
        Assert.NotNull(r);
        Assert.Equal(expected, Assert.Single(r).Type);
    }

    [Fact]
    public void Parse_MissingNameAndMotivation_FallsBackGracefully()
    {
        // Zonder naam blijft de vondst bruikbaar (host als naam); motivatie
        // mag leeg zijn — de beheerder ziet de URL en beslist zelf.
        var r = SourceScout.Parse("""{"proposals": [{"url": "https://example.com/guide"}]}""");
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("example.com", p.Name);
        Assert.Equal("", p.Motivation);
    }

    [Fact]
    public void Parse_TruncatesRunawayFields()
    {
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "https://example.com/guide", "name": "{{new string('n', 500)}}", "type": "community", "motivation": "{{new string('m', 900)}}"}]}""");
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal(120, p.Name.Length);
        Assert.Equal(300, p.Motivation.Length);
    }

    [Fact]
    public void Parse_NonObjectItems_AreSkipped()
    {
        var raw = """
            {"proposals": ["https://example.com/kaal", 42,
              {"url": "https://example.com/goed", "name": "x", "type": "community", "motivation": "x"}]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("https://example.com/goed", Assert.Single(r).Url);
    }
}
