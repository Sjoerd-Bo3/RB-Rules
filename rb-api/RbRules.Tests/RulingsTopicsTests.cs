using RbRules.Domain;

namespace RbRules.Tests;

public class RulingsTopicsTests
{
    [Theory]
    [InlineData("card", "card")]
    [InlineData("rule_section", "section")] // het opslagformaat van Correction.Scope
    [InlineData("section", "section")]
    [InlineData("mechanic", "mechanic")] // #177: ClarificationMiningService
    [InlineData("concept", "concept")] // #177: ClarificationMiningService
    [InlineData("answer", "answer")]
    [InlineData("  CARD  ", "card")]
    [InlineData("  MECHANIC  ", "mechanic")]
    [InlineData("claim", "answer")] // review-notitie-promotie (#124) bucket als answer
    [InlineData("relation", "answer")] // idem
    [InlineData("iets-onbekends", "answer")] // web-feedback is answer-scoped
    [InlineData(null, "answer")]
    public void FromCorrectionScope_MapsToSharedVocabulary(string? scope, string expected)
        => Assert.Equal(expected, RulingsTopics.FromCorrectionScope(scope));

    [Theory]
    [InlineData("card", "card")]
    [InlineData("mechanic", "mechanic")]
    [InlineData("section", "section")]
    [InlineData("concept", "concept")]
    [InlineData("  Mechanic ", "mechanic")]
    [InlineData("iets-onbekends", "concept")] // items mogen nooit uit de databank verdwijnen
    [InlineData(null, "concept")]
    public void FromClaimTopicType_MapsToSharedVocabulary(string? topicType, string expected)
        => Assert.Equal(expected, RulingsTopics.FromClaimTopicType(topicType));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseFilter_Empty_MeansNoFilter(string? raw)
    {
        Assert.True(RulingsTopics.TryParseFilter(raw, out var topic, out _));
        Assert.Null(topic);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("MECHANIC")]
    [InlineData(" section ")]
    [InlineData("concept")]
    [InlineData("answer")]
    public void TryParseFilter_KnownTopics_Normalized(string raw)
    {
        Assert.True(RulingsTopics.TryParseFilter(raw, out var topic, out _));
        Assert.Equal(raw.Trim().ToLowerInvariant(), topic);
    }

    [Fact]
    public void TryParseFilter_UnknownTopic_FailsWithReadableError()
    {
        Assert.False(RulingsTopics.TryParseFilter("deck", out var topic, out var fout));
        Assert.Null(topic);
        Assert.Contains("deck", fout);
        Assert.Contains("card", fout); // de foutmelding somt de geldige waarden op
    }
}

public class ClaimTrustTests
{
    [Fact]
    public void Label_AcceptedConfirmed_MentionsOfficialConfirmation()
    {
        var label = ClaimTrust.Label(3, 0.72, "accepted", "confirmed");
        Assert.Equal("community (3 bronnen, trust 0.72, officieel bevestigd)", label);
    }

    [Fact]
    public void Label_SingleSource_UsesSingular()
    {
        var label = ClaimTrust.Label(1, 0.4, "accepted", "unchecked");
        Assert.Equal("community (1 bron, trust 0.40)", label);
    }

    [Fact]
    public void Label_RejectedClaim_IsExplicitlyInvalidKnowledge()
    {
        var label = ClaimTrust.Label(2, 0.5, "rejected", "contradicted");
        Assert.Contains("officieel tegengesproken", label);
        Assert.Contains("géén geldige kennis", label);
    }

    [Fact]
    public void Label_Unreviewed_IsLabeled()
        => Assert.Contains("nog niet gereviewd", ClaimTrust.Label(1, 0.3, "unreviewed", "unchecked"));

    [Fact]
    public void Label_TrustScore_UsesInvariantDecimalPoint()
        // NL-runtime-culture zou "0,72" opleveren — het label is contract-tekst.
        => Assert.Contains("trust 0.72", ClaimTrust.Label(2, 0.72, "accepted", "unchecked"));
}
