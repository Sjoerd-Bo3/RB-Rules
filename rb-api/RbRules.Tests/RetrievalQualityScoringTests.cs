using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Ring-B/C-scoring van de eval-harness (#231, spec §7): path-recall op
/// gekwalificeerde interacties (structuurverlies), citation-support/groundedness,
/// answer-faithfulness met het deterministische vangnet, en answer-consistency onder
/// parafrase. Kern: het vangnet wint van de judge (een SUPPORTED claim die naar
/// ongehaalde support citeert is niet faithful).</summary>
public class RetrievalQualityScoringTests
{
    private static EvalCase Case(
        IReadOnlyList<string>? gold = null,
        IReadOnlyList<string>? condition = null) => new()
    {
        Id = "c",
        Question = "werkt Deflect tijdens een showdown?",
        QueryType = EvalQueryType.Inference,
        GoldSupport = gold ?? [],
        GoldConditionSupport = condition ?? [],
    };

    private static EvalRunResult Run(
        IReadOnlyList<string>? retrieved = null,
        IReadOnlyList<string>? cited = null,
        IReadOnlyList<string>? claims = null) => new()
    {
        RetrievedSupport = retrieved ?? [],
        Citations = cited ?? [],
        ProducedClaims = claims ?? [],
    };

    // --- Path-recall (faalmodus 3: structuurverlies) ---

    [Fact]
    public void PathRecall_GeenConditieKnopen_IsEen()
    {
        // Niet-gekwalificeerde case: niets structureels te missen.
        Assert.Equal(1.0, RetrievalQualityScoring.PathRecall(Case(), Run()));
    }

    [Fact]
    public void PathRecall_ConditieOpgehaald_IsEen()
    {
        var @case = Case(gold: ["ix", "window:showdown"], condition: ["window:showdown"]);
        var run = Run(retrieved: ["ix", "window:showdown"]);
        Assert.Equal(1.0, RetrievalQualityScoring.PathRecall(@case, run));
    }

    [Fact]
    public void PathRecall_ConditieGemist_StraftDeKwalificatie()
    {
        // Het pad haalt de interactie wél maar de window-conditie níet → structuurverlies.
        var @case = Case(gold: ["ix", "window:showdown"], condition: ["window:showdown"]);
        var run = Run(retrieved: ["ix"]);
        Assert.Equal(0.0, RetrievalQualityScoring.PathRecall(@case, run));
    }

    [Fact]
    public void PathRecall_HalfOpgehaald_IsFractie()
    {
        var @case = Case(condition: ["window:showdown", "limit:1"]);
        var run = Run(retrieved: ["window:showdown"]);
        Assert.Equal(0.5, RetrievalQualityScoring.PathRecall(@case, run));
    }

    // --- Citation-support (groundedness) ---

    [Fact]
    public void CitationSupport_NietsGeciteerd_IsEen()
    {
        Assert.Equal(1.0, RetrievalQualityScoring.CitationSupport(Run()));
    }

    [Fact]
    public void CitationSupport_GeciteerdBuitenSubgraaf_IsOngegrond()
    {
        // Twee citaties, één zit niet in de opgehaalde subgraaf → 0.5 grounded.
        var run = Run(retrieved: ["§7.4"], cited: ["§7.4", "§9.9"]);
        Assert.Equal(0.5, RetrievalQualityScoring.CitationSupport(run));
    }

    // --- Faithfulness met deterministisch vangnet ---

    [Fact]
    public void Faithfulness_GeenClaims_IsEen()
    {
        Assert.Equal(1.0, RetrievalQualityScoring.Faithfulness([], []));
    }

    [Fact]
    public void Faithfulness_SupportedEnGegrond_IsEen()
    {
        var judged = new[]
        {
            new JudgedClaim("a", ClaimVerdict.Supported, ["§7.4"]),
        };
        Assert.Equal(1.0, RetrievalQualityScoring.Faithfulness(judged, ["§7.4"]));
    }

    [Fact]
    public void Faithfulness_Vangnet_SupportedMaarOngehaaldeCitatie_IsNietFaithful()
    {
        // De judge zegt SUPPORTED, maar de claim citeert naar iets buiten de subgraaf:
        // de structurele check wint (spec §7).
        var judged = new[]
        {
            new JudgedClaim("a", ClaimVerdict.Supported, ["§9.9"]),
        };
        Assert.Equal(0.0, RetrievalQualityScoring.Faithfulness(judged, ["§7.4"]));
    }

    [Fact]
    public void Faithfulness_ContradictedEnNotInContext_TellenNietMee()
    {
        var judged = new[]
        {
            new JudgedClaim("a", ClaimVerdict.Supported, ["§7.4"]),
            new JudgedClaim("b", ClaimVerdict.Contradicted, ["§7.4"]),
            new JudgedClaim("c", ClaimVerdict.NotInContext, []),
        };
        // 1 van 3 faithful.
        Assert.Equal(1.0 / 3, RetrievalQualityScoring.Faithfulness(judged, ["§7.4"]), 6);
    }

    [Fact]
    public void Faithfulness_SupportedZonderCitatie_VertrouwtDeJudge()
    {
        // Een SUPPORTED claim die niets citeert draagt op zijn verdict.
        var judged = new[] { new JudgedClaim("a", ClaimVerdict.Supported) };
        Assert.Equal(1.0, RetrievalQualityScoring.Faithfulness(judged, []));
    }

    // --- Answer-consistency onder parafrase (Ring C) ---

    [Fact]
    public void Consistency_EenRun_IsEen()
    {
        Assert.Equal(1.0, RetrievalQualityScoring.AnswerConsistency([Run(claims: ["x"])]));
    }

    [Fact]
    public void Consistency_IdentiekeClaims_IsEen()
    {
        var runs = new[] { Run(claims: ["x", "y"]), Run(claims: ["y", "x"]) };
        Assert.Equal(1.0, RetrievalQualityScoring.AnswerConsistency(runs));
    }

    [Fact]
    public void Consistency_DisjuncteClaims_IsNul()
    {
        var runs = new[] { Run(claims: ["x"]), Run(claims: ["y"]) };
        Assert.Equal(0.0, RetrievalQualityScoring.AnswerConsistency(runs));
    }

    [Fact]
    public void Consistency_HalveOverlap_IsJaccard()
    {
        // {x,y} vs {y,z}: inter 1, union 3 → 1/3.
        var runs = new[] { Run(claims: ["x", "y"]), Run(claims: ["y", "z"]) };
        Assert.Equal(1.0 / 3, RetrievalQualityScoring.AnswerConsistency(runs), 6);
    }

    [Fact]
    public void Consistency_BeideLeeg_IsEen()
    {
        var runs = new[] { Run(), Run() };
        Assert.Equal(1.0, RetrievalQualityScoring.AnswerConsistency(runs));
    }
}
