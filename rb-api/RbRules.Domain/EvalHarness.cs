namespace RbRules.Domain;

/// <summary>Eén complete case-run als invoer voor de harness: de gouden case, de
/// geabstraheerde retrieval/generatie-uitkomst, en — voor de Ring-B/C-metrieken —
/// optioneel de judge-verdicten (faithfulness) en de parafrase-herhalingen
/// (consistency). De LLM-afhankelijke velden zijn nullable zodat een Ring-A-run
/// (€0, deterministisch) ze gewoon weglaat (#231, KRITIEK: rb-ai niet in CI).</summary>
public sealed record EvalCaseRun(
    EvalCase Case,
    EvalRunResult Run,
    IReadOnlyList<JudgedClaim>? JudgedClaims = null,
    IReadOnlyList<EvalRunResult>? ParaphraseRuns = null);

/// <summary>Bindt de scorers (<see cref="EvalScoringService"/>,
/// <see cref="RetrievalQualityScoring"/>) samen tot de per-ring metriek-suite en
/// vertaalt case-runs naar <see cref="ClassifiedSample"/>s voor de
/// <see cref="BaselineDiffGate"/>. Puur en getest: het scheidt WELKE metrieken een
/// ring meet van HÓE de gate ze diff't. Cases die niet van kracht zijn (retired,
/// door-erratum-achterhaald, buiten venster) leveren geen samples; shadow-cases
/// leveren samples met <see cref="ClassifiedSample.CountedTowardGate"/> = false.</summary>
public static class EvalHarness
{
    /// <summary>De metrieken die een ring meet, in rapportage-volgorde. Ring A is
    /// deterministisch (€0); B voegt de judge-metrieken toe; C voegt parafrase-
    /// consistency toe. Cumulatief: een hogere ring meet alles van de lagere plus
    /// extra.</summary>
    public static IReadOnlyList<string> MetricsFor(EvalRing ring) => ring switch
    {
        EvalRing.A =>
        [
            EvalMetricNames.Recall, EvalMetricNames.Relevancy,
            EvalMetricNames.PathRecall, EvalMetricNames.CitationPrecision,
            EvalMetricNames.CitationSupport,
        ],
        EvalRing.B =>
        [
            EvalMetricNames.Recall, EvalMetricNames.Relevancy,
            EvalMetricNames.PathRecall, EvalMetricNames.CitationPrecision,
            EvalMetricNames.CitationSupport, EvalMetricNames.Faithfulness,
            EvalMetricNames.ContradictionRecall,
        ],
        EvalRing.C =>
        [
            EvalMetricNames.Recall, EvalMetricNames.Relevancy,
            EvalMetricNames.PathRecall, EvalMetricNames.CitationPrecision,
            EvalMetricNames.CitationSupport, EvalMetricNames.Faithfulness,
            EvalMetricNames.ContradictionRecall, EvalMetricNames.Consistency,
        ],
        _ => [],
    };

    /// <summary>Scoor alle voor <paramref name="ring"/> relevante metrieken van één
    /// case-run. Metrieken die invoer missen (faithfulness zonder judge-verdicten,
    /// consistency zonder parafrases) vallen terug op de vacuüm-conventie (1.0 —
    /// niets te meten) i.p.v. een verzonnen straf.</summary>
    public static IReadOnlyDictionary<string, double> Score(EvalCaseRun caseRun, EvalRing ring)
    {
        ArgumentNullException.ThrowIfNull(caseRun);
        var @case = caseRun.Case;
        var run = caseRun.Run;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var metric in MetricsFor(ring))
            result[metric] = metric switch
            {
                EvalMetricNames.Recall => EvalScoringService.Recall(@case, run),
                EvalMetricNames.Relevancy => EvalScoringService.Relevancy(@case, run),
                EvalMetricNames.PathRecall => RetrievalQualityScoring.PathRecall(@case, run),
                EvalMetricNames.CitationPrecision => EvalScoringService.CitationPrecision(@case, run),
                EvalMetricNames.CitationSupport => RetrievalQualityScoring.CitationSupport(run),
                EvalMetricNames.ContradictionRecall => EvalScoringService.ContradictionRecall(@case, run),
                EvalMetricNames.Faithfulness => RetrievalQualityScoring.Faithfulness(
                    caseRun.JudgedClaims ?? [], run.RetrievedSupport),
                EvalMetricNames.Consistency => RetrievalQualityScoring.AnswerConsistency(
                    caseRun.ParaphraseRuns ?? [run]),
                _ => 1.0,
            };
        return result;
    }

    /// <summary>Vertaal case-runs naar de <see cref="ClassifiedSample"/>-stroom voor
    /// de <see cref="BaselineDiffGate"/>. Niet-van-kracht-cases worden overgeslagen;
    /// elke van-kracht-case levert één sample per ring-metriek, met
    /// <see cref="ClassifiedSample.CountedTowardGate"/> = <see cref="EvalStatus.Active"/>
    /// (shadow scoort maar gate niet).</summary>
    public static IReadOnlyList<ClassifiedSample> Samples(
        IEnumerable<EvalCaseRun> caseRuns, EvalRing ring, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(caseRuns);
        var samples = new List<ClassifiedSample>();
        foreach (var caseRun in caseRuns)
        {
            var @case = caseRun.Case;
            if (!@case.IsInEffect(asOf)) continue;
            var counted = @case.Status == EvalStatus.Active;
            foreach (var (metric, value) in Score(caseRun, ring))
                samples.Add(new ClassifiedSample(@case.QueryType, metric, value, counted));
        }
        return samples;
    }
}
