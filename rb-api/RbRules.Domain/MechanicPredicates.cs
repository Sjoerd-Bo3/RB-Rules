namespace RbRules.Domain;

/// <summary>Fase 5 (#229, §5) — een GETYPEERD mechanic-eigenschap-predicaat: het
/// structurele signaal waarop de <see cref="HypothesisEngine"/> abductief redeneert
/// (i.p.v. blind N² over alle kaartparen). Waar de fase-1 <see cref="CanonicalEntity"/>
/// een mechanic/keyword IDENTIFICEERT, zegt dit predicaat wat die mechanic DOET:
/// <c>triggers_on(Exhaust-payoff, exhaust)</c>, <c>prevents(Accelerate, exhaust)</c>,
/// <c>grants(Bastion, tank)</c>, <c>requires_target(Snipe, unit)</c>. Zo kan de motor
/// property-antagonisme herkennen (X triggert op exhaust ∧ Y voorkomt exhaust ⇒
/// kandidaat-nonbo) zónder de tekst opnieuw te minen.
///
/// De predicaten worden gemined+gereviewd (extractie-vorm: <see cref="MechanicPredicateExtraction"/>;
/// de live rb-ai-call is een integratie-follow-up, net als bij fase 2). Postgres is
/// de bron van waarheid; het is een eigen tabel naast <see cref="CanonicalEntity"/>
/// zodat elk predicaat zijn eigen provenance (<see cref="CreatedByRunId"/>) en
/// review-status draagt en afzonderlijk weerlegbaar is — één slecht gemined predicaat
/// sleept de canonieke entiteit niet mee.</summary>
public class MechanicPredicateAssertion
{
    public long Id { get; set; }

    /// <summary>De <see cref="CanonicalEntity"/> (mechanic/keyword) die deze
    /// eigenschap DRAAGT — de subject-kant van het predicaat. FK naar de fase-1-
    /// laag zodat het predicaat mee-resolvet met merges/aliassen.</summary>
    public required long SubjectEntityId { get; set; }
    public CanonicalEntity? SubjectEntity { get; set; }

    /// <summary>Het getypeerde predicaat (<see cref="MechanicPredicateKinds"/>):
    /// triggers_on | prevents | grants | requires_target.</summary>
    public required string Predicate { get; set; }

    /// <summary>Het genormaliseerde object-token (§5): waarop/wat het predicaat
    /// slaat. Betekenis hangt van de <see cref="Predicate"/> af — triggers_on/prevents
    /// dragen een event-/status-token ("exhaust"), grants een keyword-token ("tank"),
    /// requires_target een doel-type-token ("unit"). Genormaliseerd (lowercase,
    /// getrimd) zodat <c>triggers_on(X,exhaust)</c> en <c>prevents(Y,exhaust)</c>
    /// op precies hetzelfde token joinen.</summary>
    public required string ObjectToken { get; set; }

    /// <summary>Levenscyclus (<see cref="MechanicPredicateStatus"/>): candidate =
    /// gemined, wacht op review; reviewed = door de beheerder bevestigd (mag de
    /// hypothese-motor voeden); rejected = afgekeurd (tombstone-achtig, blijft als
    /// audit-spoor bestaan, voedt de motor niet).</summary>
    public string Status { get; set; } = MechanicPredicateStatus.Candidate;

    /// <summary>Optionele review-memo (waarom bevestigd/afgekeurd).</summary>
    public string? StatusReason { get; set; }

    /// <summary>0a-provenance (#233): de <see cref="MiningRun"/> die dit predicaat
    /// aandroeg. Elk predicaat is herleidbaar tot run/model/vocabulaire.</summary>
    public required string CreatedByRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Genormaliseerde dedupe-sleutel <c>subject|predicate|object</c> —
    /// dezelfde subject/predicate/object-combinatie mag maar één keer bestaan
    /// (de service dedupet, de unieke index borgt het hard).</summary>
    public string DedupeKey => MechanicPredicateDedupe.Key(SubjectEntityId, Predicate, ObjectToken);
}

/// <summary>De ENIGE toegestane <see cref="MechanicPredicateAssertion.Predicate"/>-
/// waarden (§5). Eén bron zodat extractie-vorm, engine, service en tests niet
/// uiteenlopen. Bewust een gesloten set van vier: het zijn de structurele assen
/// waarop property-antagonisme/-synergie herkenbaar is — geen open vrije-tekst-
/// relatie (dat zou de abductie weer naar lexicale ruis trekken).</summary>
public static class MechanicPredicateKinds
{
    /// <summary>De mechanic vuurt/reageert op een event of status
    /// (<c>triggers_on(Fury-payoff, exhaust)</c>).</summary>
    public const string TriggersOn = "triggers_on";

    /// <summary>De mechanic voorkomt/annuleert een event of status
    /// (<c>prevents(Accelerate, exhaust)</c>).</summary>
    public const string Prevents = "prevents";

    /// <summary>De mechanic verleent een keyword/eigenschap
    /// (<c>grants(Bastion, tank)</c>).</summary>
    public const string Grants = "grants";

    /// <summary>De mechanic vereist een doel van een bepaald type
    /// (<c>requires_target(Snipe, unit)</c>).</summary>
    public const string RequiresTarget = "requires_target";

    public static readonly IReadOnlyList<string> All =
        [TriggersOn, Prevents, Grants, RequiresTarget];

    /// <summary>Canoniek (lowercase, getrimd, underscore-genormaliseerd) predicaat,
    /// of <c>null</c> als het geen erkend predicaat is — de extractie-poort weigert
    /// dan i.p.v. te gokken (dezelfde tolerante-maar-strikte lijn als
    /// <see cref="InteractionKinds.Canonicalize"/>). Accepteert zowel spaties als
    /// koppeltekens als scheidingsteken ("Triggers On" → triggers_on).</summary>
    public static string? Canonicalize(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return null;
        var norm = predicate.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return All.Contains(norm) ? norm : null;
    }

    public static bool IsValid(string? predicate) => Canonicalize(predicate) is not null;
}

/// <summary>Canonieke <see cref="MechanicPredicateAssertion.Status"/>-waarden.</summary>
public static class MechanicPredicateStatus
{
    public const string Candidate = "candidate";
    public const string Reviewed = "reviewed";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [Candidate, Reviewed, Rejected];

    public static bool IsValid(string? status) => status is not null && All.Contains(status);
}

/// <summary>Advisieve object-token-lexica per predicaat (§5). Zoals bij
/// <see cref="MechanicMiner"/> is dit een SEED — niet-gelijste maar duidelijke
/// tokens mogen ook (de extractie-parser normaliseert, review cureert), zodat een
/// nieuwe set nieuwe events/keywords kan introduceren zonder code-wijziging
/// (CLAUDE.md: de kennisbank moet mee-evolueren). De engine matcht op de
/// genormaliseerde vorm; het lexicon dient alleen de extractie-poort en de review.</summary>
public static class MechanicPredicateLexicon
{
    /// <summary>Event-/status-tokens voor triggers_on/prevents.</summary>
    public static readonly IReadOnlyList<string> Events =
        ["exhaust", "ready", "damage", "death", "conquer", "play", "move", "draw", "recycle", "stun"];

    /// <summary>Doel-type-tokens voor requires_target (de Object-kaarttak + "any").</summary>
    public static readonly IReadOnlyList<string> TargetTypes =
        ["unit", "legend", "gear", "battlefield", "rune", "token", "object", "spell", "any"];

    /// <summary>Keyword-tokens voor grants — de mechanic-seed, genormaliseerd.</summary>
    public static IReadOnlyList<string> Keywords() =>
        [.. MechanicMiner.SeedVocabulary.Select(Normalize)];

    /// <summary>De advisieve seed voor een predicaat (voor de extractie-enum en de
    /// review-hint). Leeg = geen seed (het predicaat kent geen gesloten lexicon).</summary>
    public static IReadOnlyList<string> SeedFor(string? predicate) =>
        MechanicPredicateKinds.Canonicalize(predicate) switch
        {
            MechanicPredicateKinds.TriggersOn or MechanicPredicateKinds.Prevents => Events,
            MechanicPredicateKinds.Grants => Keywords(),
            MechanicPredicateKinds.RequiresTarget => TargetTypes,
            _ => [],
        };

    /// <summary>De canonieke token-vorm: lowercase, getrimd, interne witruimte
    /// gecollabsed op één spatie. Zo joint "Exhaust" ↔ "exhaust" en botst
    /// "on exhaust" niet met "exhaust" per ongeluk (dat is een ander token).</summary>
    public static string Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var parts = token.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts);
    }
}

/// <summary>Genormaliseerde dedupe-sleutel voor mechanic-predicaten:
/// <c>subjectEntityId|predicate|objectToken</c>. Eén bron zodat de service-dedupe
/// en de unieke index nooit uiteenlopen; predicaat en token hoofdletter-/witruimte-
/// genormaliseerd zodat casing-varianten niet als aparte rijen binnenkomen.</summary>
public static class MechanicPredicateDedupe
{
    public static string Key(long subjectEntityId, string predicate, string objectToken) =>
        $"{subjectEntityId}|{MechanicPredicateKinds.Canonicalize(predicate) ?? (predicate ?? "").Trim().ToLowerInvariant()}|{MechanicPredicateLexicon.Normalize(objectToken)}";
}
