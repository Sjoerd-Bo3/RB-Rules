using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De baseline-diff-per-klasse-gate (#231, spec §7). Kern: een regressie op
/// ÉÉN question_class blokkeert (ook al houdt het gemiddelde over de klassen stand);
/// shadow-samples scoren en rapporteren maar gaten nooit (cold-start); een
/// deterministische metriek (StdDev 0) mag niet zakken; een nieuwe klasse zonder
/// baseline kan niet gaten.</summary>
public class BaselineDiffGateTests
{
    private static ClassifiedSample S(
        EvalQueryType qt, string metric, double value, bool counted = true) =>
        new(qt, metric, value, counted);

    private static EvalBaseline Baseline(params BaselineCell[] cells) => new(cells);

    private static BaselineCell Cell(
        EvalQueryType qt, string metric, double mean, double std, int n = 10) =>
        new(EvalRing.B, qt, metric, mean, std, n);

    [Fact]
    public void GeenRegressie_BovenDrempel_Passt()
    {
        var baseline = Baseline(Cell(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.9, 0.05));
        var samples = new[] { S(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.88) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.True(report.Passed);
        Assert.Empty(report.GatingRegressions);
    }

    [Fact]
    public void Regressie_OnderTweeSigma_Blokkeert()
    {
        // baseline mean 0.9, σ 0.05 → floor 0.8; huidige 0.75 < 0.8 → regressie.
        var baseline = Baseline(Cell(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.9, 0.05));
        var samples = new[] { S(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.75) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.False(report.Passed);
        var reg = Assert.Single(report.GatingRegressions);
        Assert.Equal(EvalQueryType.Inference, reg.QueryType);
        Assert.Equal(EvalMetricNames.Faithfulness, reg.Metric);
        Assert.Equal(0.8, reg.AllowedFloor!.Value, 6);
    }

    [Fact]
    public void RegressieOpEenKlasse_VerbergtZichNietInHetGemiddelde()
    {
        // Twee klassen; Factoid houdt stand, Inference stort in. Het gemiddelde over
        // beide (0.9 + 0.6)/2 = 0.75 zou "ok" lijken — maar per-klasse vangt het.
        var baseline = Baseline(
            Cell(EvalQueryType.Factoid, EvalMetricNames.Recall, 0.9, 0.02),
            Cell(EvalQueryType.Inference, EvalMetricNames.Recall, 0.9, 0.02));
        var samples = new[]
        {
            S(EvalQueryType.Factoid, EvalMetricNames.Recall, 0.9),
            S(EvalQueryType.Inference, EvalMetricNames.Recall, 0.6),
        };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.False(report.Passed);
        var reg = Assert.Single(report.GatingRegressions);
        Assert.Equal(EvalQueryType.Inference, reg.QueryType);
    }

    [Fact]
    public void ShadowSample_Regresseert_MaarBlokkeertNiet()
    {
        var baseline = Baseline(Cell(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.9, 0.05));
        var samples = new[] { S(EvalQueryType.Inference, EvalMetricNames.Faithfulness, 0.1, counted: false) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.True(report.Passed);
        Assert.Empty(report.GatingRegressions);
        var shadow = Assert.Single(report.ShadowCells);
        Assert.True(shadow.Regression);          // hij regresseert wél zichtbaar ...
        Assert.False(shadow.CountedTowardGate);  // ... maar telt niet.
    }

    [Fact]
    public void DeterministischeMetriek_MagNietZakken()
    {
        // StdDev 0 (citation-validity is altijd 1.0) → floor == mean → elke daling regresseert.
        var baseline = Baseline(Cell(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 1.0, 0.0));
        var samples = new[] { S(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 0.99) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.False(report.Passed);
    }

    [Fact]
    public void DeterministischeMetriek_GelijkBlijvend_Passt()
    {
        var baseline = Baseline(Cell(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 1.0, 0.0));
        var samples = new[] { S(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 1.0) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.True(report.Passed);
    }

    [Fact]
    public void NieuweKlasseZonderBaseline_KanNietGaten()
    {
        // Cold-start op metriek-niveau: geen baseline-cel → geen diff → nooit regressie.
        var baseline = Baseline();
        var samples = new[] { S(EvalQueryType.Temporal, EvalMetricNames.Recall, 0.0) };

        var report = BaselineDiffGate.Evaluate(EvalRing.B, baseline, samples);

        Assert.True(report.Passed);
        var cell = Assert.Single(report.Cells);
        Assert.Null(cell.BaselineMean);
        Assert.False(cell.Regression);
    }

    [Fact]
    public void FromSamples_LegtGemiddeldeEnStdDevVast_ShadowUitgesloten()
    {
        var samples = new[]
        {
            S(EvalQueryType.Factoid, EvalMetricNames.Recall, 0.8),
            S(EvalQueryType.Factoid, EvalMetricNames.Recall, 1.0),
            S(EvalQueryType.Factoid, EvalMetricNames.Recall, 0.0, counted: false), // shadow: uitgesloten
        };

        var baseline = EvalBaseline.FromSamples(EvalRing.A, samples);

        var cell = baseline.Cell(EvalRing.A, EvalQueryType.Factoid, EvalMetricNames.Recall);
        Assert.NotNull(cell);
        Assert.Equal(0.9, cell!.Mean, 6);       // (0.8+1.0)/2, shadow buiten
        Assert.Equal(0.1, cell.StdDev, 6);      // populatie-σ van {0.8,1.0}
        Assert.Equal(2, cell.SampleCount);
    }
}
