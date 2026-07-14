using RbRules.Domain;

namespace RbRules.Tests;

public class ClarificationSourcesTests
{
    [Theory]
    [InlineData("playriftbound-com-unleashed-rules-faq-and-clarifications", "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/", "Unleashed Rules FAQ and Clarifications")]
    [InlineData("some-id", "https://playriftbound.com/en-us/news/rules-and-releases/some-patch-notes/", "Some Patch Notes")]
    [InlineData("some-id", "https://example.com/whatever", "Rules Clarification: Legion")]
    public void IsMatch_FaqOrClarificationSignal_ReturnsTrue(string id, string url, string name) =>
        Assert.True(ClarificationSources.IsMatch(id, url, name));

    [Theory]
    [InlineData("playriftbound-com-core-rules", "https://playriftbound.com/core-rules.pdf", "Core Rules")]
    [InlineData("errata-ogn", "https://playriftbound.com/en-us/news/rules-and-releases/ogn-errata/", "OGN Errata")]
    public void IsMatch_RegularSource_ReturnsFalse(string id, string url, string name) =>
        Assert.False(ClarificationSources.IsMatch(id, url, name));

    [Fact]
    public void IsMatch_CaseInsensitive() =>
        Assert.True(ClarificationSources.IsMatch("id", "https://example.com/UNLEASHED-FAQ", null));
}

public class ClarificationMinerTests
{
    // Realistische fixture (#177): één multi-concept-alinea zoals de echte
    // Unleashed Rules FAQ (Reflection tokens + Arcane Shift + [C]-symbool +
    // Legion door elkaar in dezelfde ~2200-tekens-slab) — de parser moet
    // hier meerdere DISCRETE items uit halen, elk met zijn eigen onderwerp.
    private const string LegionAndOthersAnswer = """
        Hier zijn de concepten:
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion",
           "clarification": "Legion verwijst naar het moment waarop je een item op de chain finalizet — de kaart wordt pas dan daadwerkelijk gespeeld.",
           "sectionRef": "402.3",
           "quote": "Legion means you finalize an item on the chain"},
          {"topicType": "concept", "topicRef": "Reflection tokens",
           "clarification": "Reflection tokens tellen niet mee voor het handlimiet aan het einde van de beurt.",
           "quote": "Reflection tokens do not count toward your hand size limit"},
          {"topicType": "mechanic", "topicRef": "Arcane Shift",
           "clarification": "Arcane Shift mag ook getarget worden op units die al getapt zijn.",
           "quote": "Arcane Shift may target tapped units"}
        ]}
        Dat was alles.
        """;

    [Fact]
    public void Parse_MixedFaqParagraph_SplitsIntoDiscreteConcepts()
    {
        var r = ClarificationMiner.Parse(LegionAndOthersAnswer);

        Assert.NotNull(r);
        Assert.Equal(3, r.Count);

        var legion = Assert.Single(r, c => c.TopicRef == "Legion");
        Assert.Equal("mechanic", legion.TopicType);
        Assert.Contains("finalize", legion.Clarification);
        Assert.Equal("402.3", legion.SectionRef);
        Assert.Equal("Legion means you finalize an item on the chain", legion.Quote);

        var reflection = Assert.Single(r, c => c.TopicRef == "Reflection tokens");
        Assert.Equal("concept", reflection.TopicType);
        Assert.Null(reflection.SectionRef);

        var arcaneShift = Assert.Single(r, c => c.TopicRef == "Arcane Shift");
        Assert.Equal("mechanic", arcaneShift.TopicType);
    }

    [Fact]
    public void Parse_UnknownTopicType_DegradesToConcept()
    {
        var raw = """{"clarifications": [{"topicType": "iets-onbekends", "topicRef": "X", "clarification": "Uitleg over X."}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("concept", Assert.Single(r).TopicType);
    }

    [Fact]
    public void Parse_MissingClarificationOrTopicRef_SkipsItem()
    {
        var raw = """
            {"clarifications": [
              {"topicType": "mechanic", "topicRef": "Legion"},
              {"topicType": "mechanic", "clarification": "Geen onderwerp hier."},
              {"topicType": "mechanic", "topicRef": "Shield", "clarification": "Shield absorbeert de volgende bron schade."}
            ]}
            """;
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        var item = Assert.Single(r);
        Assert.Equal("Shield", item.TopicRef);
    }

    [Fact]
    public void Parse_DuplicateTopicAndText_Dedupes()
    {
        var raw = """
            {"clarifications": [
              {"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion  betekent finalizen."},
              {"topicType": "mechanic", "topicRef": "legion", "clarification": "Legion betekent finalizen. "}
            ]}
            """;
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ik zie hier geen bruikbare concepten.")]
    [InlineData("{kapotte json}")]
    [InlineData("""{"iets_anders": true}""")]
    public void Parse_GarbageOutput_ReturnsNull(string raw) =>
        Assert.Null(ClarificationMiner.Parse(raw));

    [Fact]
    public void Parse_EmptyResult_ReturnsEmptyList_NotNull()
    {
        var r = ClarificationMiner.Parse("""{"clarifications": []}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_LongClarification_IsTruncated()
    {
        var longText = new string('x', ClarificationMiner.MaxClarificationLength + 50);
        var raw = $$"""{"clarifications": [{"topicType": "concept", "topicRef": "Test", "clarification": "{{longText}}"}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(ClarificationMiner.MaxClarificationLength, Assert.Single(r).Clarification.Length);
    }
}
