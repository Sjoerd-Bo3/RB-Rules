namespace RbRules.Domain;

/// <summary>Eén claim uit de decompositie van een antwoord, met het judge-verdict
/// (Ring B, spec §7 — faithfulness). <see cref="CitedSupport"/> zijn de support-ids
/// waarop de claim beweert te leunen; het deterministische vangnet toetst die tegen
/// de daadwerkelijk opgehaalde subgraaf (een claim die citeert naar iets buiten de
/// context KAN niet SUPPORTED zijn, wat de judge ook zegt).</summary>
public enum ClaimVerdict
{
    /// <summary>Gedekt door de context.</summary>
    Supported,
    /// <summary>Weersproken door de context.</summary>
    Contradicted,
    /// <summary>Niet in de context terug te vinden (hallucinatie-risico).</summary>
    NotInContext,
}

/// <summary>Eén beoordeelde claim (judge-uitvoer, geabstraheerd — geen LLM in het
/// scaffold). De judge draait op temperature 0 met een gehasht promptcontract; hier
/// telt alleen zijn geabstraheerde verdict + de geciteerde support.</summary>
public sealed record JudgedClaim(
    string ClaimId,
    ClaimVerdict Verdict,
    IReadOnlyList<string> CitedSupport)
{
    public JudgedClaim(string claimId, ClaimVerdict verdict)
        : this(claimId, verdict, []) { }
}

/// <summary>Ring-B/C-scoring (#231, spec §7): retrieval-kwaliteit voorbij de kale
/// set-recall — path-recall op gekwalificeerde interacties (structuurverlies),
/// citation-support/groundedness, answer-faithfulness (judge MÉT deterministisch
/// vangnet) en answer-consistency onder parafrase. Puur: géén DB, graaf of LLM. De
/// judge-verdicten komen als geabstraheerde <see cref="JudgedClaim"/>-invoer binnen,
/// zodat de scoring €0 en volledig testbaar blijft en de LLM-judge zelf een
/// integratie-follow-up is (KRITIEK: rb-ai niet in CI).
///
/// Vacuüm-conventie (lege noemer → 1.0) volgt <see cref="EvalScoringService"/>:
/// niets te meten = niets fout.</summary>
public static class RetrievalQualityScoring
{
    /// <summary>Path-recall op gekwalificeerde interacties (faalmodus 3): van de
    /// conditie-dragende gold-knopen (<see cref="EvalCase.GoldConditionSupport"/>),
    /// hoeveel bracht de retrieval op. Een pad dat de interactie wél maar de
    /// <c>window=showdown</c>-conditie níet ophaalt, plat de kwalificatie en scoort
    /// &lt; 1.0. Géén conditie-knopen → 1.0 (niet-gekwalificeerde case, niets
    /// structureels te missen).</summary>
    public static double PathRecall(EvalCase @case, EvalRunResult run)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(run);
        if (@case.GoldConditionSupport.Count == 0) return 1.0;
        var retrieved = ToSet(run.RetrievedSupport);
        var hit = @case.GoldConditionSupport.Count(retrieved.Contains);
        return (double)hit / @case.GoldConditionSupport.Count;
    }

    /// <summary>Citation-support (groundedness, deterministisch): van de geciteerde
    /// ids, hoeveel zaten daadwerkelijk in de OPGEHAALDE subgraaf. Onderscheidt zich
    /// van <see cref="EvalScoringService.CitationPrecision"/> (die toetst tegen de
    /// gold-verwachting): dit vangt het antwoord dat een id citeert dat de retrieval
    /// nooit bracht — een verzonnen/ongegronde citatie, óók als het id toevallig
    /// "geldig" is. Niets geciteerd → 1.0.</summary>
    public static double CitationSupport(EvalRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Citations.Count == 0) return 1.0;
        var retrieved = ToSet(run.RetrievedSupport);
        var grounded = run.Citations.Count(retrieved.Contains);
        return (double)grounded / run.Citations.Count;
    }

    /// <summary>Answer-faithfulness: van de gedecomponeerde claims, hoeveel zijn
    /// SUPPORTED — mét het deterministische vangnet uit spec §7. Een claim die de
    /// judge SUPPORTED noemt maar die citeert naar support buiten de opgehaalde
    /// subgraaf, wordt hard teruggezet naar niet-faithful: de structurele check wint
    /// van de judge (goedkoop, niet-omkoopbaar, vangt de judge-hallucinatie). Een
    /// claim die niets citeert wordt op zijn verdict vertrouwd (niet elke ware claim
    /// citeert expliciet). Geen claims → 1.0.</summary>
    public static double Faithfulness(
        IReadOnlyList<JudgedClaim> judged, IReadOnlyList<string> retrievedSupport)
    {
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(retrievedSupport);
        if (judged.Count == 0) return 1.0;
        var retrieved = ToSet(retrievedSupport);
        var faithful = judged.Count(c => IsFaithful(c, retrieved));
        return (double)faithful / judged.Count;
    }

    /// <summary>Is deze beoordeelde claim faithful? SUPPORTED én — als hij support
    /// citeert — elke geciteerde id in de opgehaalde subgraaf. Citeert hij niets, dan
    /// draagt het judge-verdict alleen.</summary>
    public static bool IsFaithful(JudgedClaim claim, IReadOnlySet<string> retrievedSupport)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(retrievedSupport);
        if (claim.Verdict != ClaimVerdict.Supported) return false;
        // Deterministisch vangnet: geciteerde-maar-niet-opgehaalde support ⇒ ongegrond.
        return claim.CitedSupport.All(retrievedSupport.Contains);
    }

    /// <summary>Answer-consistency onder parafrase (Ring C): draai dezelfde vraag in
    /// meerdere parafrases en meet of het antwoord dezelfde claim-verzameling
    /// produceert. Score = gemiddelde paarsgewijze Jaccard-overeenkomst over de
    /// claim-sets. Eén run (niets te vergelijken) → 1.0; volstrekt disjuncte
    /// antwoorden → 0.0. Een instabiel antwoord (parafrase kantelt de conclusie) valt
    /// zo op zonder een gouden antwoord nodig te hebben.</summary>
    public static double AnswerConsistency(IReadOnlyList<EvalRunResult> paraphraseRuns)
    {
        ArgumentNullException.ThrowIfNull(paraphraseRuns);
        if (paraphraseRuns.Count <= 1) return 1.0;

        var sets = paraphraseRuns.Select(r => ToSet(r.ProducedClaims)).ToList();
        double sum = 0;
        int pairs = 0;
        for (var i = 0; i < sets.Count; i++)
            for (var j = i + 1; j < sets.Count; j++)
            {
                sum += Jaccard(sets[i], sets[j]);
                pairs++;
            }
        return pairs == 0 ? 1.0 : sum / pairs;
    }

    /// <summary>Jaccard-overeenkomst van twee claim-sets. Beide leeg → 1.0 (beide
    /// antwoorden claimden niets — consistent leeg).</summary>
    private static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 1.0 : (double)inter / union;
    }

    private static HashSet<string> ToSet(IReadOnlyList<string> ids) =>
        new(ids, StringComparer.Ordinal);
}
