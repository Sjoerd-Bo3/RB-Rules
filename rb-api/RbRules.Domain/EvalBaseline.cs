namespace RbRules.Domain;

/// <summary>Eén gemeten metriek-waarde van één case-run, geclassificeerd naar
/// question_class en of hij meetelt voor de gate (Active) of enkel observeert
/// (Shadow, cold-start). De baseline-diff aggregeert deze per (question_class ×
/// metric)-cel; shadow-samples tellen wél in de rapportage maar nooit in de
/// pass/fail (#231, spec §7 — cold-start-shadow).</summary>
public sealed record ClassifiedSample(
    EvalQueryType QueryType,
    string Metric,
    double Value,
    bool CountedTowardGate);

/// <summary>Eén baseline-cel: de vastgelegde verdeling (gemiddelde + standaard-
/// deviatie over <see cref="SampleCount"/> cases) van één metriek binnen één
/// question_class, voor één ring. De baseline-diff-gate vergelijkt de huidige run
/// tegen <c>Mean − k·StdDev</c> — een PER-KLASSE-drempel, geen absolute (sluipende
/// degradatie op één klasse verbergt zich in het gemiddelde, spec §7).</summary>
public sealed record BaselineCell(
    EvalRing Ring,
    EvalQueryType QueryType,
    string Metric,
    double Mean,
    double StdDev,
    int SampleCount);

/// <summary>De vastgelegde baseline (Postgres <c>eval_baseline</c> in de bedrade
/// versie; hier de pure waarde-vorm). Eén cel per (ring × question_class × metric).
/// De baseline groeit mee: een nieuwe question_class/metric zonder cel kan niet
/// gaten (niets om tegen te diffen) — dat is de cold-start op metriek-niveau, het
/// spiegelbeeld van de shadow-status op case-niveau.</summary>
public sealed class EvalBaseline
{
    private readonly Dictionary<(EvalRing, EvalQueryType, string), BaselineCell> _cells;

    public EvalBaseline(IEnumerable<BaselineCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = cells.ToDictionary(c => (c.Ring, c.QueryType, c.Metric));
    }

    public IReadOnlyCollection<BaselineCell> Cells => _cells.Values;

    public BaselineCell? Cell(EvalRing ring, EvalQueryType queryType, string metric) =>
        _cells.GetValueOrDefault((ring, queryType, metric));

    /// <summary>Bouw een verse baseline uit een run met bekend-goede samples (bv. na
    /// een geaccordeerde gouden set of een release). Per (question_class × metric)
    /// wordt gemiddelde en (populatie-)standaarddeviatie berekend; enkel
    /// meetellende (Active) samples doen mee — een shadow-run legt geen baseline vast.
    /// Dit is de "record baseline"-tegenhanger van <see cref="BaselineDiffGate"/>.</summary>
    public static EvalBaseline FromSamples(EvalRing ring, IEnumerable<ClassifiedSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var cells = samples
            .Where(s => s.CountedTowardGate)
            .GroupBy(s => (s.QueryType, s.Metric))
            .Select(g =>
            {
                var values = g.Select(s => s.Value).ToList();
                var mean = values.Average();
                var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
                return new BaselineCell(
                    ring, g.Key.QueryType, g.Key.Metric, mean, Math.Sqrt(variance), values.Count);
            });
        return new EvalBaseline(cells);
    }
}

/// <summary>De uitkomst van de baseline-diff voor één (question_class × metric)-cel
/// binnen een gate-run. <see cref="Regression"/> is true wanneer de huidige mean
/// onder <c>BaselineMean − k·BaselineStdDev</c> zakt. Bij een deterministische
/// metriek (StdDev 0) is elke daling onder de baseline een regressie — precies de
/// harde citation-validity-gate uitgedrukt als diff. Zonder baseline (<see
/// cref="BaselineMean"/> null) is er niets om tegen te diffen: nooit een regressie
/// (cold-start op metriek-niveau).</summary>
public sealed record BaselineDiffCell(
    EvalQueryType QueryType,
    string Metric,
    double CurrentMean,
    int SampleCount,
    bool CountedTowardGate,
    double? BaselineMean,
    double? BaselineStdDev,
    double? AllowedFloor,
    bool Regression);

/// <summary>Het diff-rapport van een hele gate-run. <see cref="Passed"/> is alleen
/// false door een regressie op een MEETELLENDE (Active) cel — shadow-regressies
/// staan apart, puur ter observatie (#231, spec §7).</summary>
public sealed record BaselineDiffReport(
    EvalRing Ring,
    bool Passed,
    IReadOnlyList<BaselineDiffCell> Cells)
{
    /// <summary>Meetellende cellen die onder de baseline zakten — de gate-brekers.</summary>
    public IReadOnlyList<BaselineDiffCell> GatingRegressions =>
        [.. Cells.Where(c => c.CountedTowardGate && c.Regression)];

    /// <summary>Shadow-cellen (gescoord, gerapporteerd, niet gegate).</summary>
    public IReadOnlyList<BaselineDiffCell> ShadowCells =>
        [.. Cells.Where(c => !c.CountedTowardGate)];
}

/// <summary>De baseline-diff-per-klasse-gate (#231, spec §7 — "PR faalt bij
/// faithfulness/path-recall &gt; kσ onder baseline op ENIGE klasse"). Aggregeert de
/// samples van een run per (question_class × metric), vergelijkt het huidige
/// gemiddelde tegen de vastgelegde baseline en blokkeert bij een regressie op een
/// meetellende cel. Shadow-samples worden apart geaggregeerd en gerapporteerd maar
/// gaten nooit — een half-gereviewde nieuwe set breekt de CI van <c>main</c> dus
/// niet. Puur: alle IO (baseline laden, samples bouwen uit harness-runs) zit
/// eromheen; live-retrieval is een integratie-follow-up.</summary>
public static class BaselineDiffGate
{
    /// <summary>Aantal standaarddeviaties dat een klasse mag zakken vóór het regressie
    /// heet (spec §7: 2σ). Bij StdDev 0 (deterministische metriek) reduceert dit tot
    /// "elke daling is regressie".</summary>
    public const double DefaultSigma = 2.0;

    // Tolerantie tegen drijvende-komma-ruis op een gelijk-blijvende metriek.
    private const double Epsilon = 1e-9;

    public static BaselineDiffReport Evaluate(
        EvalRing ring,
        EvalBaseline baseline,
        IEnumerable<ClassifiedSample> samples,
        double sigma = DefaultSigma)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(samples);

        // Aggregeer per (question_class × metric); meetellend en shadow gescheiden,
        // want een klasse kan tegelijk Active- en Shadow-cases hebben (verschillende
        // cases, dezelfde klasse) en die mogen elkaars gemiddelde niet vervuilen.
        var cells = samples
            .GroupBy(s => (s.QueryType, s.Metric, s.CountedTowardGate))
            .Select(g =>
            {
                var currentMean = g.Average(s => s.Value);
                var cell = baseline.Cell(ring, g.Key.QueryType, g.Key.Metric);
                double? floor = cell is null ? null : cell.Mean - sigma * cell.StdDev;
                var regression = cell is not null && currentMean < floor!.Value - Epsilon;
                return new BaselineDiffCell(
                    g.Key.QueryType, g.Key.Metric, currentMean, g.Count(),
                    g.Key.CountedTowardGate, cell?.Mean, cell?.StdDev, floor, regression);
            })
            .OrderBy(c => c.QueryType)
            .ThenBy(c => c.Metric, StringComparer.Ordinal)
            .ToList();

        var passed = !cells.Any(c => c.CountedTowardGate && c.Regression);
        return new BaselineDiffReport(ring, passed, cells);
    }
}
