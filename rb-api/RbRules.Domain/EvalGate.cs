namespace RbRules.Domain;

/// <summary>Het scoringsresultaat van één case binnen een gate-run.
/// <see cref="CountedTowardGate"/> is false voor shadow-cases: ze scoren en
/// worden gerapporteerd, maar hun <see cref="Violations"/> blokkeren de gate
/// nooit (cold-start, #231 Kritiek B4).</summary>
public sealed record EvalCaseResult(
    string CaseId,
    EvalStatus Status,
    bool CountedTowardGate,
    EvalMetrics Metrics,
    IReadOnlyList<string> Violations);

/// <summary>De uitkomst van een hele gate-run. <see cref="Passed"/> is alleen
/// false door harde overtredingen op cases die meetellen (Active). Skipped
/// cases (retired / door-erratum-achterhaald / buiten geldigheidsvenster) staan
/// apart, puur ter transparantie.</summary>
public sealed record EvalGateReport(
    bool Passed,
    IReadOnlyList<EvalCaseResult> Results,
    IReadOnlyList<string> SkippedCaseIds)
{
    /// <summary>Cases die de gate lieten falen (meetellend én met overtreding).</summary>
    public IReadOnlyList<EvalCaseResult> GatingFailures =>
        [.. Results.Where(r => r.CountedTowardGate && r.Violations.Count > 0)];

    /// <summary>Shadow-observaties: gescoord, gerapporteerd, niet gegate.</summary>
    public IReadOnlyList<EvalCaseResult> ShadowObservations =>
        [.. Results.Where(r => !r.CountedTowardGate)];
}

/// <summary>Bepaalt of een harness-run de CI-gate haalt (spec §7, Ring A —
/// deterministisch, €0). Twee harde poorten in het scaffold: citation-validity
/// (100% — een geciteerd id buiten de verwachte set is een verzonnen citatie)
/// en de forbidden-claim-poort (nul geproduceerde actieve forbidden claims).
/// Optioneel een <c>minRecall</c>-drempel (in de gebedrade versie vervangen
/// door de baseline-diff-per-klasse; hier bewust een expliciete drempel i.p.v.
/// een verzonnen baseline).
///
/// Cold-start-shadow (Kritiek B4): een case in <see cref="EvalStatus.Shadow"/>
/// scoort en wordt gerapporteerd, maar telt nooit mee voor pass/fail — een
/// half-gereviewde nieuwe set breekt de CI van <c>main</c> dus niet. Errata-
/// verval (Kritiek C): een achterhaalde/retired case wordt overgeslagen en een
/// door-erratum-omgekeerde forbidden claim telt niet als overtreding (die
/// logica zit in <see cref="EvalScoringService.ViolatedForbiddenClaims"/> en
/// <see cref="EvalCase.IsInEffect"/>).</summary>
public static class EvalGateEvaluator
{
    public static EvalGateReport Evaluate(
        IEnumerable<(EvalCase Case, EvalRunResult Run)> runs,
        DateOnly asOf,
        double? minRecall = null)
    {
        var results = new List<EvalCaseResult>();
        var skipped = new List<string>();
        var passed = true;

        foreach (var (@case, run) in runs)
        {
            if (!@case.IsInEffect(asOf))
            {
                skipped.Add(@case.Id);
                continue;
            }

            var metrics = EvalScoringService.Score(@case, run);
            var violations = CollectViolations(@case, run, metrics, minRecall);
            var counted = @case.Status == EvalStatus.Active;

            results.Add(new EvalCaseResult(
                @case.Id, @case.Status, counted, metrics, violations));

            if (counted && violations.Count > 0) passed = false;
        }

        return new EvalGateReport(passed, results, skipped);
    }

    private static IReadOnlyList<string> CollectViolations(
        EvalCase @case, EvalRunResult run, EvalMetrics metrics, double? minRecall)
    {
        var violations = new List<string>();

        // Harde poort 1 — citation-validity (100%): elk geciteerd id moet in de
        // verwachte set zitten; alles daarbuiten is een verzonnen citatie.
        if (metrics.CitationPrecision < 1.0)
        {
            var expected = new HashSet<string>(@case.ExpectedCitations, StringComparer.Ordinal);
            foreach (var invalid in run.Citations.Where(c => !expected.Contains(c)))
                violations.Add($"citation-validity: verzonnen citatie '{invalid}'");
        }

        // Harde poort 2 — forbidden-claim: nul geproduceerde ACTIEVE forbidden
        // claims (door-erratum-vervallen claims tellen bewust niet mee).
        foreach (var fc in EvalScoringService.ViolatedForbiddenClaims(@case, run))
            violations.Add($"forbidden-claim geproduceerd: '{fc.Id}'");

        // Optionele recall-drempel (placeholder voor de baseline-diff-per-klasse).
        if (minRecall is { } floor && metrics.Recall < floor)
            violations.Add($"path-recall {metrics.Recall:0.###} < drempel {floor:0.###}");

        return violations;
    }
}
