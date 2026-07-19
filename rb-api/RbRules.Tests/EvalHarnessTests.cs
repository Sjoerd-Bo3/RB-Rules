using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De harness die de scorers per ring bindt en case-runs naar
/// <see cref="ClassifiedSample"/>s vertaalt voor de baseline-diff-gate (#231, spec
/// §7). Kern: Ring A meet géén judge-metrieken (€0), Ring B/C wél; niet-van-kracht-
/// cases leveren geen samples; shadow-cases leveren niet-meetellende samples.</summary>
public class EvalHarnessTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 1);

    private static EvalCase Case(
        string id = "c",
        EvalStatus status = EvalStatus.Active,
        EvalQueryType qt = EvalQueryType.Inference,
        IReadOnlyList<string>? gold = null,
        string? supersededByErratum = null) => new()
    {
        Id = id,
        Question = "vraag?",
        QueryType = qt,
        Status = status,
        GoldSupport = gold ?? [],
        SupersededByErratum = supersededByErratum,
    };

    [Fact]
    public void MetricsFor_RingA_BevatGeenJudgeMetrieken()
    {
        var a = EvalHarness.MetricsFor(EvalRing.A);
        Assert.DoesNotContain(EvalMetricNames.Faithfulness, a);
        Assert.DoesNotContain(EvalMetricNames.Consistency, a);
        Assert.Contains(EvalMetricNames.Recall, a);
        Assert.Contains(EvalMetricNames.PathRecall, a);
    }

    [Fact]
    public void MetricsFor_RingB_VoegtFaithfulnessToe_MaarGeenConsistency()
    {
        var b = EvalHarness.MetricsFor(EvalRing.B);
        Assert.Contains(EvalMetricNames.Faithfulness, b);
        Assert.Contains(EvalMetricNames.ContradictionRecall, b);
        Assert.DoesNotContain(EvalMetricNames.Consistency, b);
    }

    [Fact]
    public void MetricsFor_RingC_VoegtConsistencyToe()
    {
        Assert.Contains(EvalMetricNames.Consistency, EvalHarness.MetricsFor(EvalRing.C));
    }

    [Fact]
    public void Score_ZonderJudge_FaithfulnessValtTerugOpVacuum()
    {
        var caseRun = new EvalCaseRun(
            Case(gold: ["g"]),
            new EvalRunResult { RetrievedSupport = ["g"] });
        var scores = EvalHarness.Score(caseRun, EvalRing.B);
        Assert.Equal(1.0, scores[EvalMetricNames.Faithfulness]); // geen judge → 1.0
        Assert.Equal(1.0, scores[EvalMetricNames.Recall]);
    }

    [Fact]
    public void Samples_SlaatNietVanKrachtCasesOver()
    {
        var runs = new[]
        {
            new EvalCaseRun(Case("live"), new EvalRunResult()),
            new EvalCaseRun(
                Case("dood", status: EvalStatus.Retired), new EvalRunResult()),
            new EvalCaseRun(
                Case("weg", supersededByErratum: "err:1"), new EvalRunResult()),
        };

        var samples = EvalHarness.Samples(runs, EvalRing.A, AsOf);

        // Alleen de levende case levert samples (één per ring-A-metriek).
        Assert.All(samples, s => Assert.True(s.CountedTowardGate));
        Assert.Equal(EvalHarness.MetricsFor(EvalRing.A).Count, samples.Count);
    }

    [Fact]
    public void Samples_ShadowCase_LevertNietMeetellendeSamples()
    {
        var runs = new[]
        {
            new EvalCaseRun(Case("s", status: EvalStatus.Shadow), new EvalRunResult()),
        };
        var samples = EvalHarness.Samples(runs, EvalRing.A, AsOf);
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.False(s.CountedTowardGate));
    }

    [Fact]
    public void EndToEnd_HarnessNaarBaselineDiffGate()
    {
        // Bouw een baseline uit een goede run, draai een regressie-run erdoorheen.
        var goodRuns = new[]
        {
            new EvalCaseRun(
                Case("c1", qt: EvalQueryType.Factoid, gold: ["g"]),
                new EvalRunResult { RetrievedSupport = ["g"] }),
        };
        var baselineSamples = EvalHarness.Samples(goodRuns, EvalRing.A, AsOf);
        var baseline = EvalBaseline.FromSamples(EvalRing.A, baselineSamples);

        var badRuns = new[]
        {
            new EvalCaseRun(
                Case("c1", qt: EvalQueryType.Factoid, gold: ["g"]),
                new EvalRunResult { RetrievedSupport = [] }), // recall stort in naar 0
        };
        var badSamples = EvalHarness.Samples(badRuns, EvalRing.A, AsOf);
        var report = BaselineDiffGate.Evaluate(EvalRing.A, baseline, badSamples);

        Assert.False(report.Passed);
        Assert.Contains(report.GatingRegressions, r => r.Metric == EvalMetricNames.Recall);
    }
}
