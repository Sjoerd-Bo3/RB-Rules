namespace RbRules.Domain;

/// <summary>Levenscyclus van een <see cref="EvalCase"/> (#231, Kritiek B4/C).
/// <see cref="Shadow"/> = de case scoort en wordt gerapporteerd, maar telt
/// NIET mee voor de CI-gate (cold-start: bij een set-launch bestaan de
/// gold-ids nog niet of zijn ze niet door een mens gecureerd — een
/// half-gereviewde set mag de CI van <c>main</c> niet breken). <see
/// cref="Active"/> = volwaardig gegate. <see cref="Retired"/> = uitgezet
/// (bv. door een erratum achterhaald) en volledig genegeerd.</summary>
public enum EvalStatus
{
    /// <summary>Scoort en rapporteert, maar blokkeert de gate nooit (cold-start).</summary>
    Shadow,
    /// <summary>Volwaardig gegate: telt mee voor pass/fail.</summary>
    Active,
    /// <summary>Uitgezet — nooit gescoord, nooit gegate.</summary>
    Retired,
}

/// <summary>Vraag-taxonomie in MultiHop-RAG-stijl (spec §7 — de gouden set is
/// MultiHop-RAG-achtig). Los van de heuristische <see cref="QuestionType"/>
/// router-takken: dít is de as waarlangs de eval-baseline-diff per klasse
/// wordt gerapporteerd (sluipende degradatie op één klasse verbergt zich in
/// het gemiddelde).</summary>
public enum EvalQueryType
{
    /// <summary>Eén feit uit één knoop ("wat is de energiekost van X?").</summary>
    Factoid,
    /// <summary>Meerdere hops / afgeleide interactie ("werkt Deflect tijdens een showdown?").</summary>
    Inference,
    /// <summary>Twee of meer entiteiten tegen elkaar ("verschil tussen Stun en Exhaust?").</summary>
    Comparison,
    /// <summary>Tijd-/versie-gevoelig ("gold dit vóór het erratum van maart?").</summary>
    Temporal,
}

/// <summary>Eén claim die NIET in het antwoord mag voorkomen (een bekende
/// hallucinatie). Errata-invalidatie op claim-niveau (#231, Kritiek C): een
/// <see cref="ForbiddenClaim"/> kan ná een erratum juist WAAR worden — dan
/// wordt <see cref="SupersededByErratum"/> gezet (de errata-flow uit #230
/// triggert dat) en telt het produceren van deze claim niet langer als fout.
/// De rest van de case blijft gewoon scoren; alleen deze claim vervalt.
/// <see cref="Id"/> is het deterministisch te matchen token tegen de
/// (geabstraheerde) geproduceerde claim-ids van een run.</summary>
public sealed record ForbiddenClaim(
    string Id,
    string Text,
    string? SupersededByErratum = null)
{
    /// <summary>Actief (telt mee) zolang geen erratum deze claim heeft omgekeerd.</summary>
    public bool IsActive => SupersededByErratum is null;
}

/// <summary>De centrale meeteenheid (spec §7, Postgres <c>eval_case</c> in de
/// gebedrade versie — hier puur in-memory, nog niet bedraad). Draagt de
/// gouden verwachtingen (<see cref="GoldSupport"/> = recall-noemer, <see
/// cref="ExpectedCitations"/>, <see cref="ForbiddenClaims"/>) plus de
/// levenscyclus-velden voor cold-start-shadow en errata-verval.</summary>
public sealed record EvalCase
{
    public required string Id { get; init; }

    /// <summary>De vraag in het Nederlands (UI-taal, werkafspraak 1).</summary>
    public required string Question { get; init; }

    public required EvalQueryType QueryType { get; init; }

    /// <summary>Node/edge/§-ids die de opgehaalde subgraaf MÓÉT bevatten — de
    /// noemer van recall. Voor gekwalificeerde interacties hoort hier ook het
    /// conditie-dragende id in (bv. de <c>window=showdown</c>-knoop), zodat
    /// path-recall structuurverlies meet (spec §7, faalmodus 3).</summary>
    public IReadOnlyList<string> GoldSupport { get; init; } = [];

    /// <summary>De §-/bron-ids die het antwoord hoort te citeren. Citation-
    /// validity is een harde gate: een geciteerd id buiten deze verwachting
    /// telt in het scaffold als een verzonnen citatie.</summary>
    public IReadOnlyList<string> ExpectedCitations { get; init; } = [];

    public IReadOnlyList<ForbiddenClaim> ForbiddenClaims { get; init; } = [];

    // --- Levenscyclus (Kritiek B4/C) ---

    public EvalStatus Status { get; init; } = EvalStatus.Shadow;

    /// <summary>Vanaf wanneer de case geldig is (bitemporeel valid_time-anker).</summary>
    public DateOnly ValidFrom { get; init; } = DateOnly.MinValue;

    /// <summary>Tot wanneer de case geldig is; null = open einde.</summary>
    public DateOnly? ValidUntil { get; init; }

    /// <summary>Gezet wanneer een erratum de héle case achterhaalt (case-niveau
    /// verval, naast het claim-niveau van <see cref="ForbiddenClaim"/>). Een
    /// achterhaalde case wordt overgeslagen tot een mens hem herziet — CI faalt
    /// dus niet op claims die inmiddels kloppen (#231, Kritiek C).</summary>
    public string? SupersededByErratum { get; init; }

    /// <summary>Is de case op de peildatum van kracht? Retired, door-erratum-
    /// achterhaald, nog-niet-geldig of verlopen → false (overslaan). Shadow
    /// telt hier als "van kracht" — shadow scoort wél, alleen de gate niet.</summary>
    public bool IsInEffect(DateOnly asOf)
    {
        if (Status == EvalStatus.Retired) return false;
        if (SupersededByErratum is not null) return false;
        if (asOf < ValidFrom) return false;
        if (ValidUntil is { } until && asOf > until) return false;
        return true;
    }

    /// <summary>De forbidden claims die op de peildatum nog echt fout zijn —
    /// dat wil zeggen: niet door een erratum omgekeerd. Dit is de noemer van
    /// contradiction-recall en de basis van de forbidden-claim-gate.</summary>
    public IReadOnlyList<ForbiddenClaim> ActiveForbiddenClaims =>
        [.. ForbiddenClaims.Where(c => c.IsActive)];
}

/// <summary>De geabstraheerde uitkomst van één harness-run tegen een <see
/// cref="EvalCase"/>. Bewust ONafhankelijk van de (nog niet bestaande)
/// retrieval/graaf: enkel ids. Zo is de scoring puur testbaar zonder DB, graaf
/// of LLM (spec §7: retrieval-fout ≠ generatie-fout, scheidbaar gemeten).</summary>
public sealed record EvalRunResult
{
    /// <summary>De support-ids die de retrieval daadwerkelijk in de subgraaf
    /// bracht (∩ met <see cref="EvalCase.GoldSupport"/> → recall & relevancy).</summary>
    public IReadOnlyList<string> RetrievedSupport { get; init; } = [];

    /// <summary>De ids die het antwoord citeerde.</summary>
    public IReadOnlyList<string> Citations { get; init; } = [];

    /// <summary>De claim-ids die het antwoord produceerde — gematcht tegen de
    /// forbidden-claim-ids voor contradiction-recall.</summary>
    public IReadOnlyList<string> ProducedClaims { get; init; } = [];
}

/// <summary>De vier meetlagen samengevat tot getallen voor één case-run
/// (spec §7). Alle waarden in [0,1]; vacuüm-gevallen (lege noemer) zijn per
/// metriek gedocumenteerd in <see cref="EvalScoringService"/>.</summary>
public sealed record EvalMetrics(
    double Relevancy,
    double Recall,
    double F1,
    double CitationPrecision,
    double ContradictionRecall);
