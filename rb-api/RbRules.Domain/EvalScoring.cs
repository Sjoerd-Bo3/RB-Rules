namespace RbRules.Domain;

/// <summary>Pure scoring voor de eval-harness (#231). Géén DB, graaf of LLM —
/// enkel set-rekenkunde over een <see cref="EvalCase"/> en een geabstraheerde
/// <see cref="EvalRunResult"/>, zodat retrieval-fout en generatie-fout
/// scheidbaar en €0 in CI meetbaar zijn (spec §7, Ring A). De metrieken zijn
/// bewust dun (Ragas-concepten, geen runtime-dependency).
///
/// Vacuüm-conventie (lege noemer): een metriek zonder iets om te missen scoort
/// 1.0 — er is niets fout gegaan. Dat is gate-vriendelijk (een case zonder
/// verwachte citaties straft een leeg-citerend antwoord niet) en wordt per
/// metriek toegelicht.</summary>
public static class EvalScoringService
{
    /// <summary>Subgraph-recall: van de vereiste gold-support, hoeveel bracht
    /// de retrieval op. Noemer = <see cref="EvalCase.GoldSupport"/>. Lege gold
    /// → 1.0 (niets te missen).</summary>
    public static double Recall(EvalCase @case, EvalRunResult run)
    {
        if (@case.GoldSupport.Count == 0) return 1.0;
        var retrieved = ToSet(run.RetrievedSupport);
        var hit = @case.GoldSupport.Count(retrieved.Contains);
        return (double)hit / @case.GoldSupport.Count;
    }

    /// <summary>Relevancy = retrieval-precisie: van wat de retrieval ophaalde,
    /// hoeveel was daadwerkelijk gold (de rest is ruis). Noemer = opgehaalde
    /// support. Leeg opgehaald → 1.0 (geen ruis).</summary>
    public static double Relevancy(EvalCase @case, EvalRunResult run)
    {
        if (run.RetrievedSupport.Count == 0) return 1.0;
        var gold = ToSet(@case.GoldSupport);
        var hit = run.RetrievedSupport.Count(gold.Contains);
        return (double)hit / run.RetrievedSupport.Count;
    }

    /// <summary>F1 = harmonisch gemiddelde van <see cref="Relevancy"/>
    /// (precisie) en <see cref="Recall"/>. Beide 0 → 0 (geen deling door 0).</summary>
    public static double F1(double relevancy, double recall)
    {
        var sum = relevancy + recall;
        return sum == 0 ? 0.0 : 2 * relevancy * recall / sum;
    }

    /// <summary>Citation-precisie: van de geciteerde ids, hoeveel zaten in de
    /// verwachte set. Een geciteerd id buiten de verwachting = een verzonnen
    /// citatie (harde-gate-signaal, spec §7). Niets geciteerd → 1.0 (geen
    /// verzonnen citaties; completeness is een aparte, hier niet-gemeten as).</summary>
    public static double CitationPrecision(EvalCase @case, EvalRunResult run)
    {
        if (run.Citations.Count == 0) return 1.0;
        var expected = ToSet(@case.ExpectedCitations);
        var valid = run.Citations.Count(expected.Contains);
        return (double)valid / run.Citations.Count;
    }

    /// <summary>De ACTIEVE forbidden claims (niet door een erratum omgekeerd)
    /// die het antwoord tóch produceerde — de daadwerkelijke contradicties.
    /// Door-erratum-vervallen claims blijven hier bewust buiten (#231, Kritiek
    /// C): ze zijn inmiddels waar, dus geen fout meer.</summary>
    public static IReadOnlyList<ForbiddenClaim> ViolatedForbiddenClaims(
        EvalCase @case, EvalRunResult run)
    {
        var produced = ToSet(run.ProducedClaims);
        return [.. @case.ActiveForbiddenClaims.Where(c => produced.Contains(c.Id))];
    }

    /// <summary>Contradiction-recall: van de actieve forbidden claims, hoeveel
    /// heeft het antwoord correct VERMEDEN. Een geproduceerde actieve claim
    /// verlaagt de recall; een door-erratum-vervallen claim telt niet mee in de
    /// noemer. Geen actieve forbidden claims → 1.0 (niets te vermijden).</summary>
    public static double ContradictionRecall(EvalCase @case, EvalRunResult run)
    {
        var active = @case.ActiveForbiddenClaims;
        if (active.Count == 0) return 1.0;
        var violated = ViolatedForbiddenClaims(@case, run).Count;
        return (double)(active.Count - violated) / active.Count;
    }

    /// <summary>Alle vier de meetlagen samengevat voor één case-run.</summary>
    public static EvalMetrics Score(EvalCase @case, EvalRunResult run)
    {
        var relevancy = Relevancy(@case, run);
        var recall = Recall(@case, run);
        return new EvalMetrics(
            Relevancy: relevancy,
            Recall: recall,
            F1: F1(relevancy, recall),
            CitationPrecision: CitationPrecision(@case, run),
            ContradictionRecall: ContradictionRecall(@case, run));
    }

    private static HashSet<string> ToSet(IReadOnlyList<string> ids) =>
        new(ids, StringComparer.Ordinal);
}
