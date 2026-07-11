using RbRules.Domain;

namespace RbRules.Tests;

public class ClaimMinerTests
{
    [Fact]
    public void ParseClaims_ValidJson_ReturnsClaims()
    {
        var raw = """
            {"claims": [
              {"topicType": "mechanic", "topicRef": "Deflect",
               "statement": "Deflect verhoogt de kosten van vijandelijke spells die de unit als doel kiezen.",
               "quote": "Deflect makes your opponent pay more to target this unit."},
              {"topicType": "concept", "topicRef": "mulligan",
               "statement": "Bij de mulligan mag je één keer je volledige starthand omruilen.",
               "quote": "you may shuffle your hand back once"}
            ]}
            """;
        var r = ClaimMiner.ParseClaims(raw);
        Assert.NotNull(r);
        Assert.Equal(2, r.Count);
        Assert.Equal("mechanic", r[0].TopicType);
        Assert.Equal("Deflect", r[0].TopicRef);
        Assert.StartsWith("Deflect verhoogt", r[0].Statement);
        Assert.Equal("you may shuffle your hand back once", r[1].Quote);
    }

    [Fact]
    public void ParseClaims_TextAroundJson_IsTolerated()
    {
        var raw = """
            Hier zijn de claims:
            {"claims": [{"topicType": "card", "topicRef": "Viktor", "statement": "Viktor's effect werkt ook tijdens de showdown."}]}
            Dat was alles.
            """;
        var r = ClaimMiner.ParseClaims(raw);
        Assert.NotNull(r);
        var c = Assert.Single(r);
        Assert.Equal("card", c.TopicType);
        Assert.Null(c.Quote); // quote is optioneel
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ik zie geen bruikbare claims in deze tekst.")]
    [InlineData("{kapotte json}")]
    [InlineData("""{"iets_anders": true}""")]
    public void ParseClaims_GarbageOutput_ReturnsNull(string raw) =>
        // null = onbruikbaar antwoord (degradatiepad, document blijft staan
        // voor een volgende run); dat is iets anders dan een geldige lege lijst.
        Assert.Null(ClaimMiner.ParseClaims(raw));

    [Fact]
    public void ParseClaims_EmptyList_ReturnsEmpty()
    {
        var r = ClaimMiner.ParseClaims("""{"claims": []}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void ParseClaims_BareArray_IsTolerated()
    {
        var r = ClaimMiner.ParseClaims(
            """[{"topicType": "concept", "topicRef": "scoren", "statement": "Je scoort een punt door een battlefield te houden."}]""");
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("Card", "card")]        // hoofdletters normaliseren
    [InlineData("keyword", "concept")]  // onbekend type degradeert
    [InlineData(null, "concept")]       // ontbrekend type ook
    public void ParseClaims_TopicTypeClampsToVocabulary(string? type, string expected)
    {
        var typeJson = type is null ? "" : $""" "topicType": "{type}", """;
        var r = ClaimMiner.ParseClaims(
            $$"""{"claims": [{{{typeJson}} "topicRef": "x", "statement": "een bewering"}]}""");
        Assert.NotNull(r);
        Assert.Equal(expected, Assert.Single(r).TopicType);
    }

    [Theory]
    [InlineData("""{"topicType": "card", "topicRef": "Viktor"}""")]        // geen statement
    [InlineData("""{"topicType": "card", "statement": "een bewering"}""")] // geen topicRef
    [InlineData("""{"topicType": "card", "topicRef": " ", "statement": "x"}""")]
    public void ParseClaims_ItemsWithoutStatementOrTopic_AreDropped(string item)
    {
        var r = ClaimMiner.ParseClaims($$"""{"claims": [{{item}}]}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void ParseClaims_DuplicateStatements_AreDedupedOnNormalizedText()
    {
        var raw = """
            {"claims": [
              {"topicType": "concept", "topicRef": "mulligan", "statement": "Je mag één keer mulliganen."},
              {"topicType": "concept", "topicRef": "mulligan", "statement": "  je mag één  keer mulliganen  "}
            ]}
            """;
        var r = ClaimMiner.ParseClaims(raw);
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Fact]
    public void ParseClaims_CapsAtMaxClaims()
    {
        var items = string.Join(",", Enumerable.Range(1, 40).Select(i =>
            $$"""{"topicType": "concept", "topicRef": "t{{i}}", "statement": "bewering nummer {{i}}"}"""));
        var r = ClaimMiner.ParseClaims($$"""{"claims": [{{items}}]}""");
        Assert.NotNull(r);
        Assert.Equal(ClaimMiner.MaxClaims, r.Count);
    }

    [Fact]
    public void ParseClaims_TruncatesRunawayFields()
    {
        var r = ClaimMiner.ParseClaims(
            $$"""{"claims": [{"topicType": "concept", "topicRef": "{{new string('t', 500)}}", "statement": "{{new string('s', 900)}}", "quote": "{{new string('q', 800)}}"}]}""");
        Assert.NotNull(r);
        var c = Assert.Single(r);
        Assert.Equal(ClaimMiner.MaxTopicRefLength, c.TopicRef.Length);
        Assert.Equal(ClaimMiner.MaxStatementLength, c.Statement.Length);
        // Auteursrecht: citaten blijven kort.
        Assert.Equal(ClaimMiner.MaxQuoteLength, c.Quote!.Length);
    }

    [Fact]
    public void ParseClaims_NonObjectItems_AreSkipped()
    {
        var r = ClaimMiner.ParseClaims(
            """{"claims": ["kale string", 42, {"topicType": "concept", "topicRef": "x", "statement": "geldig"}]}""");
        Assert.NotNull(r);
        Assert.Equal("geldig", Assert.Single(r).Statement);
    }

    [Theory]
    [InlineData("Je mag één keer mulliganen.", "  je  mag één keer\nmulliganen ")]
    [InlineData("Deflect werkt tegen spells!", "deflect werkt tegen spells")]
    public void NormalizeStatement_MatchesEquivalentPhrasing(string a, string b) =>
        Assert.Equal(ClaimMiner.NormalizeStatement(a), ClaimMiner.NormalizeStatement(b));

    [Fact]
    public void NormalizeStatement_DifferentClaims_StayDifferent() =>
        Assert.NotEqual(
            ClaimMiner.NormalizeStatement("Je mag één keer mulliganen."),
            ClaimMiner.NormalizeStatement("Je mag twee keer mulliganen."));
}

public class ClaimJudgeTests
{
    [Fact]
    public void Parse_Same_ReturnsMatch()
    {
        var j = ClaimJudge.Parse("""{"verdict": "same", "match": 2}""", candidateCount: 3);
        Assert.NotNull(j);
        Assert.Equal("same", j.Verdict);
        Assert.Equal(2, j.Match);
    }

    [Fact]
    public void Parse_Contradicts_ReturnsMatch()
    {
        var j = ClaimJudge.Parse("""{"verdict": "contradicts", "match": 1}""", candidateCount: 1);
        Assert.NotNull(j);
        Assert.Equal("contradicts", j.Verdict);
        Assert.Equal(1, j.Match);
    }

    [Fact]
    public void Parse_Different_NeedsNoMatch()
    {
        var j = ClaimJudge.Parse("""{"verdict": "different"}""", candidateCount: 3);
        Assert.NotNull(j);
        Assert.Equal("different", j.Verdict);
        Assert.Null(j.Match);
    }

    [Theory]
    [InlineData("""{"verdict": "same"}""")]              // same zonder match
    [InlineData("""{"verdict": "same", "match": 0}""")]  // buiten bereik (1-based)
    [InlineData("""{"verdict": "same", "match": 4}""")]  // buiten bereik
    [InlineData("""{"verdict": "misschien", "match": 1}""")]
    [InlineData("geen json")]
    [InlineData("")]
    public void Parse_UnusableOutput_ReturnsNull(string raw) =>
        // null ⇒ de aanroeper behandelt de claim als nieuw (veilige kant).
        Assert.Null(ClaimJudge.Parse(raw, candidateCount: 3));

    [Fact]
    public void BuildPrompt_NumbersCandidatesOneBased()
    {
        var p = ClaimJudge.BuildPrompt("nieuw", ["eerste", "tweede"]);
        Assert.Contains("1. eerste", p);
        Assert.Contains("2. tweede", p);
    }
}

public class OfficialCheckTests
{
    [Fact]
    public void Parse_Contradicted_CarriesReason()
    {
        var v = OfficialCheck.Parse(
            """{"verdict": "contradicted", "reason": "§534.1 zegt dat de aanvaller kiest."}""");
        Assert.NotNull(v);
        Assert.Equal("contradicted", v.Verdict);
        Assert.Equal("§534.1 zegt dat de aanvaller kiest.", v.Reason);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("unclear")]
    public void Parse_OtherVerdicts_AreValid(string verdict)
    {
        var v = OfficialCheck.Parse($$"""{"verdict": "{{verdict}}"}""");
        Assert.NotNull(v);
        Assert.Equal(verdict, v.Verdict);
        Assert.Null(v.Reason);
    }

    [Theory]
    [InlineData("""{"verdict": "waarschijnlijk"}""")]
    [InlineData("de regels zeggen hier niets over")]
    [InlineData("")]
    public void Parse_UnusableOutput_ReturnsNull(string raw) =>
        // null ⇒ claim blijft "unchecked" en komt bij een volgende run terug.
        Assert.Null(OfficialCheck.Parse(raw));

    [Fact]
    public void Parse_TruncatesRunawayReason()
    {
        var v = OfficialCheck.Parse(
            $$"""{"verdict": "contradicted", "reason": "{{new string('r', 900)}}"}""");
        Assert.NotNull(v);
        Assert.Equal(OfficialCheck.MaxReasonLength, v.Reason!.Length);
    }

    [Fact]
    public void BuildPrompt_IncludesSectionCodes()
    {
        var p = OfficialCheck.BuildPrompt("bewering", [("534.1", "De aanvaller kiest.")]);
        Assert.Contains("§534.1: De aanvaller kiest.", p);
    }
}

/// <summary>Corroboratie-regels (#50): één bron = ongecorroboreerd; elke extra
/// onafhankelijke bron versterkt; officieel weegt zwaarder dan community.</summary>
public class ClaimScoringTests
{
    [Fact]
    public void Corroboration_SingleSource_IsOne() =>
        Assert.Equal(1, ClaimScoring.Corroboration(["beginners-guide-riftboundgg"]));

    [Fact]
    public void Corroboration_SameSourceTwice_CountsOnce() =>
        // "Één bron telt één keer": dezelfde bron die het twee keer zegt
        // maakt een claim níet gecorroboreerd.
        Assert.Equal(1, ClaimScoring.Corroboration(
            ["beginners-guide-riftboundgg", "Beginners-Guide-Riftboundgg"]));

    [Fact]
    public void Corroboration_IndependentSources_CountEach() =>
        Assert.Equal(2, ClaimScoring.Corroboration(
            ["beginners-guide-riftboundgg", "beginners-guide-fanfinity"]));

    [Fact]
    public void TrustScore_SingleCommunitySource_IsBaseline() =>
        Assert.Equal(0.5, ClaimScoring.TrustScore([3]));

    [Fact]
    public void TrustScore_TwoCommunitySources_CorroborationRaisesScore() =>
        // 1 − (1−0.5)² = 0.75: twee onafhankelijke community-bronnen zijn
        // samen sterker dan één, maar niet "dubbel zo waar".
        Assert.Equal(0.75, ClaimScoring.TrustScore([3, 3]));

    [Fact]
    public void TrustScore_FourCommunitySources_ApproachesButNeverReachesOne()
    {
        var score = ClaimScoring.TrustScore([3, 3, 3, 3]);
        Assert.Equal(0.94, score); // het "[community, 4 bronnen]"-voorbeeld
        Assert.True(score < 1.0);
    }

    [Fact]
    public void TrustScore_MoreSources_IsMonotonicallyStronger()
    {
        var one = ClaimScoring.TrustScore([3]);
        var two = ClaimScoring.TrustScore([3, 3]);
        var three = ClaimScoring.TrustScore([3, 3, 3]);
        Assert.True(one < two);
        Assert.True(two < three);
    }

    [Fact]
    public void TrustScore_HigherTrustSource_WeighsHeavier() =>
        // Eén partner-bron (tier 2) zegt meer dan één community-bron (tier 3).
        Assert.True(ClaimScoring.TrustScore([2]) > ClaimScoring.TrustScore([3]));

    [Fact]
    public void TrustScore_NoSources_IsZero() =>
        Assert.Equal(0.0, ClaimScoring.TrustScore([]));

    [Theory]
    [InlineData(1, 0.95)]
    [InlineData(2, 0.75)]
    [InlineData(3, 0.5)]
    [InlineData(4, 0.3)]
    [InlineData(9, 0.3)] // onbekend-lage trust degradeert naar de bodem
    public void TierWeight_FollowsRegisterTrustTiers(short tier, double expected) =>
        Assert.Equal(expected, ClaimScoring.TierWeight(tier));
}
