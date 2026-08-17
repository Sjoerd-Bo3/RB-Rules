using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Correction.Provenance → graph-"kind" (#191): onderscheidt
/// clarify-gemined van in-chat-rulings zonder het rauwe Provenance-formaat
/// elders te hoeven kennen.</summary>
public class RulingKindTests
{
    [Theory]
    [InlineData("clarify-mining:riftbound-rules-hub", "clarify")]
    [InlineData("clarify-mining:x", "clarify")]
    [InlineData("chat-ruling:admin", "chat")]
    [InlineData("chat-ruling:user", "chat")]
    [InlineData("review-notitie", "review-note")]
    [InlineData("iets-onbekends", "other")]
    [InlineData(null, "other")]
    public void FromProvenance_MapsToGraphKind(string? provenance, string expected) =>
        Assert.Equal(expected, RulingKind.FromProvenance(provenance));
}

/// <summary>Correction (verified ruling) → ABOUT-doel (#191): dezelfde
/// resolutie als Claim, via het gedeelde topic-vocabulaire zodat Scope
/// "rule_section" eerst "section" wordt. Fixture spiegelt
/// ClaimTopicMapperTests zodat kaart/mechaniek/sectie/concept-namen
/// herkenbaar zijn.</summary>
public class RulingTopicMapperTests
{
    private static ClaimTopicMapper CreateMapper() => ClaimTopicMapper.Create(
        cards: [("ogn-011-298", "Viktor", null)],
        mechanics: ["Accelerate"],
        sections: [("core-rules-pdf", "101.2")],
        concepts: [("turn-structure", "The turn structure")]);

    [Fact]
    public void Resolve_CardScope_MapsToCard() =>
        Assert.Equal("card:ogn-011-298",
            RulingTopicMapper.Resolve(CreateMapper(), "card", "Viktor")?.Format());

    [Fact]
    public void Resolve_RuleSectionScope_UsesStorageFormat_MapsToSection() =>
        // "rule_section" is het Correction.Scope-opslagformaat (niet "section").
        Assert.Equal("section:core-rules-pdf/101.2",
            RulingTopicMapper.Resolve(CreateMapper(), "rule_section", "101.2")?.Format());

    [Fact]
    public void Resolve_MechanicScope_MapsToMechanic() =>
        Assert.Equal("mechanic:Accelerate",
            RulingTopicMapper.Resolve(CreateMapper(), "mechanic", "accelerate")?.Format());

    [Fact]
    public void Resolve_ConceptScope_MapsToConcept() =>
        Assert.Equal("concept:turn-structure",
            RulingTopicMapper.Resolve(CreateMapper(), "concept", "turn-structure")?.Format());

    [Theory]
    [InlineData("answer")]  // chat-ruling zonder anker
    [InlineData("claim")]   // review-notitie-promotie (#124)
    [InlineData("relation")] // idem
    [InlineData("iets-onbekends")]
    [InlineData(null)]
    public void Resolve_NoAnchorScope_ReturnsNull_NoAboutEdge(string? scope) =>
        Assert.Null(RulingTopicMapper.Resolve(CreateMapper(), scope, "wat dan ook"));

    [Fact]
    public void Resolve_UnknownReference_ReturnsNull() =>
        Assert.Null(RulingTopicMapper.Resolve(CreateMapper(), "card", "Onbekende Kaart"));
}

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
