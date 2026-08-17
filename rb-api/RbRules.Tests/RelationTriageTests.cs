using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure delen van de relatie-triage (#199 v1): prompt-opbouw en de
/// tolerante parser, met de inc3-objectvorm-guard als kern-regressie (<see
/// cref="LlmJson.Candidates"/> levert ook array-vormige blokken op — die
/// mogen nooit een InvalidOperationException geven in plaats van een nette
/// Unusable-degradatie).</summary>
public class RelationTriageTests
{
    [Fact]
    public void Parse_GeldigOordeel_LeestRecommendationReasonEnRefs()
    {
        var raw = """
            {"recommendation": "Accept", "reason": "The context confirms Deflect only applies to combat damage.",
             "refs": ["402.3", "mechanic:Deflect"]}
            """;

        var result = RelationTriage.Parse(raw);

        Assert.Equal(RelationTriageOutcome.Judged, result.Outcome);
        Assert.Equal("accept", result.Recommendation); // genormaliseerd kleine letters
        Assert.Equal(
            "The context confirms Deflect only applies to combat damage.", result.Reason);
        Assert.Equal(["402.3", "mechanic:Deflect"], result.Refs);
    }

    [Fact]
    public void Parse_RejectZonderRefs_GeeftLegeRefsLijst()
    {
        var raw = """{"recommendation": "reject", "reason": "The context does not support this relation."}""";

        var result = RelationTriage.Parse(raw);

        Assert.Equal(RelationTriageOutcome.Judged, result.Outcome);
        Assert.Equal("reject", result.Recommendation);
        Assert.Empty(result.Refs!);
    }

    [Fact]
    public void Parse_ProseRondomJson_VindtHetBlokAlsnog()
    {
        // Zelfde bug-vorm als de scout/claims/relations-parsers (#87/#93):
        // prose vóór de JSON en een bracket-achtige marker ("[1]") mogen de
        // extractie niet breken (LlmJson.Candidates, gedeeld).
        var raw = """
            Here is my judgement [1]:
            {"recommendation": "unsure", "reason": "The retrieved context is inconclusive.", "refs": []}
            """;

        var result = RelationTriage.Parse(raw);

        Assert.Equal(RelationTriageOutcome.Judged, result.Outcome);
        Assert.Equal("unsure", result.Recommendation);
    }

    [Fact]
    public void Parse_OnbekendeRecommendationWaarde_IsUnusable()
    {
        // Een niet-vocabulaire waarde ("maybe") is net zo onbruikbaar als
        // géén JSON: de aanroeper slaat het voorstel over (transiënt).
        var raw = """{"recommendation": "maybe", "reason": "not sure"}""";

        var result = RelationTriage.Parse(raw);

        Assert.Equal(RelationTriageOutcome.Unusable, result.Outcome);
    }

    [Fact]
    public void Parse_ArrayVormigBlok_GeeftUnusable_GeenCrash()
    {
        // Kern-regressie (#188 increment 3-les): LlmJson.Candidates levert
        // ook array-vormige blokken op uit toevallige tekst als "[1]" of
        // "[true]" — zonder de objectvorm-guard gooit TryGetProperty op een
        // niet-object root een InvalidOperationException (geen JsonException,
        // dus de catch vangt 'm niet) en de triage-run zou 500'en.
        var raw = "See references [1] and [true] for details.";

        var result = RelationTriage.Parse(raw);

        Assert.Equal(RelationTriageOutcome.Unusable, result.Outcome);
    }

    [Fact]
    public void Parse_GeenJson_IsUnusable()
    {
        var result = RelationTriage.Parse("I cannot judge this relation.");

        Assert.Equal(RelationTriageOutcome.Unusable, result.Outcome);
    }

    [Fact]
    public void Parse_OntbrekendeReason_IsUnusable()
    {
        var result = RelationTriage.Parse("""{"recommendation": "accept"}""");

        Assert.Equal(RelationTriageOutcome.Unusable, result.Outcome);
    }

    [Fact]
    public void BuildPrompt_ToontRelatieEnContext()
    {
        var prompt = RelationTriage.BuildPrompt(
            "mechanic:Deflect", "section:core-rules-pdf/7.4", "is limited by",
            "Deflect only reduces combat damage.",
            ["- section:core-rules-pdf/7.4 — §7.4: Deflect reduces combat damage dealt to this unit."]);

        Assert.Contains(
            "Proposed relation: mechanic:Deflect --[is limited by]--> section:core-rules-pdf/7.4", prompt);
        Assert.Contains("Deflect only reduces combat damage.", prompt);
        Assert.Contains("§7.4", prompt);
    }

    [Fact]
    public void BuildPrompt_GeenContext_ToontExpliciet()
    {
        var prompt = RelationTriage.BuildPrompt(
            "mechanic:X", "mechanic:Y", "counters", "reden", []);

        Assert.Contains("(none found)", prompt);
    }
}
