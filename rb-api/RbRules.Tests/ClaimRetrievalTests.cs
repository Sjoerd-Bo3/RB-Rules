using RbRules.Domain;

namespace RbRules.Tests;

public class ClaimRetrievalTests
{
    private static RetrievedClaim Claim(
        int corroboration = 4, double trust = 0.94, string officialStatus = "unchecked") =>
        new("mechanic", "Deflect", "Deflect beschermt alleen tegen gekozen targets.",
            corroboration, trust, officialStatus);

    [Fact]
    public void TakeFor_EveryType_TakesAtLeastOne()
    {
        foreach (var t in Enum.GetValues<QuestionType>())
            Assert.True(ClaimRetrieval.TakeFor(t) >= 1);
    }

    [Fact]
    public void TakeFor_RulingWeighsLighterThanListMeta()
    {
        // Router-gewicht (#51 / docs/KNOWLEDGE.md): normatieve vragen krijgen
        // weinig interpretatie mee, lijst-/meta-vragen het meest.
        Assert.True(ClaimRetrieval.TakeFor(QuestionType.Ruling)
            < ClaimRetrieval.TakeFor(QuestionType.Lijst));
        Assert.True(ClaimRetrieval.TakeFor(QuestionType.Toernooi)
            <= ClaimRetrieval.TakeFor(QuestionType.Definitie));
    }

    [Fact]
    public void PromptLabel_MatchesKnowledgeDocFormat()
    {
        // Het label uit docs/KNOWLEDGE.md: "[community, 4 bronnen, trust 0.94]"
        // — invariant genoteerd (punt), ongeacht de server-culture.
        Assert.Equal("[community, 4 bronnen, trust 0.94]",
            ClaimRetrieval.PromptLabel(Claim()));
    }

    [Fact]
    public void PromptLabel_SingleSource_UsesSingular()
    {
        Assert.StartsWith("[community, 1 bron, trust 0.50]",
            ClaimRetrieval.PromptLabel(Claim(corroboration: 1, trust: 0.5)));
    }

    [Fact]
    public void PromptLabel_OfficiallyConfirmed_AddsSignal()
    {
        Assert.Equal(
            "[community, 2 bronnen, trust 0.75] (door de officiële regels bevestigd)",
            ClaimRetrieval.PromptLabel(Claim(corroboration: 2, trust: 0.75,
                officialStatus: "confirmed")));
    }

    [Fact]
    public void PromptBlock_Empty_WhenNoClaims() =>
        Assert.Equal("", ClaimRetrieval.PromptBlock([]));

    [Fact]
    public void PromptBlock_LabelsEveryClaim_AndKeepsLayeringRules()
    {
        var block = ClaimRetrieval.PromptBlock([Claim(), Claim(1, 0.5, "confirmed") with
        {
            TopicRef = "mulligan",
            Statement = "Een mulligan gaat altijd naar exact dezelfde handgrootte.",
        }]);

        // Elke claim staat gelabeld in het blok, met topic en bewering.
        Assert.Contains("[community, 4 bronnen, trust 0.94] Deflect:", block);
        Assert.Contains("[community, 1 bron, trust 0.50] (door de officiële regels bevestigd) mulligan:", block);
        // De laag is expliciet gelabeld en draagt nooit het oordeel.
        Assert.Contains("COMMUNITY-INTERPRETATIE", block);
        Assert.Contains("nooit dragen", block);
        // Het antwoordformat: apart Community-consensus-blok + uitgebreid
        // zekerheidslabel (issue #51).
        Assert.Contains("### Community-consensus", block);
        Assert.Contains("Community-consensus (N bronnen)", block);
        Assert.Contains("Bevestigd (officieel)", block);
    }

    [Fact]
    public void PromptBlock_NeverAsksForOwnRuleBasisSection()
    {
        // Citatencontract van #69 geldt ook hier: de instructies mogen het
        // model nooit een eigen "Regelbasis"-blok laten bouwen.
        Assert.DoesNotContain("Regelbasis", ClaimRetrieval.PromptBlock([Claim()]));
    }
}
