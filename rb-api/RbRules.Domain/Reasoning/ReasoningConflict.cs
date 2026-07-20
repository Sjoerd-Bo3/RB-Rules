namespace RbRules.Domain.Reasoning;

/// <summary>Waarheen een gedetecteerde tegenspraak wordt gerouteerd (fase 3, #227,
/// §5 + inzicht #236). Elke tegenspraak wordt zichtbaar afgehandeld — nooit
/// onzichtbare state.</summary>
public enum ConflictChannel
{
    /// <summary>Community-claim spreekt de officiële regel tegen → het
    /// misvattingen-kanaal (het antwoord waarschuwt de gebruiker actief).</summary>
    Misconception,
    /// <summary>Structurele/data-integriteits-tegenspraak (disjointness-schending) →
    /// de reviewqueue voor een beheerder.</summary>
    ReviewQueue,
    /// <summary>Botsende officiële rulings → menselijke escalatie (twee normatieve
    /// bronnen tegelijk, geen deterministische winnaar).</summary>
    Escalation,
}

/// <summary>De soorten tegenspraak die de <see cref="ContradictionDetector"/> vindt
/// (fase 3, #227, §5). Losse constanten zodat detector, router, service en tests
/// niet uiteenlopen.</summary>
public static class ReasoningConflictKind
{
    /// <summary>Een community-Claim spreekt een officiële RuleSection tegen zonder
    /// eigen officiële dekking.</summary>
    public const string ClaimContradictsOfficial = "claim-contradicts-official";
    /// <summary>Twee geverifieerde rulings over hetzelfde anker met botsende tekst.</summary>
    public const string RulingCollision = "ruling-collision";
    /// <summary>Eén knoop draagt twee (effectief) disjuncte labels — bv. Unit én
    /// Spell (vangt kaart-sync-schade à la #150).</summary>
    public const string DisjointnessViolation = "disjointness-violation";

    /// <summary>De steekproef-audit (#255) betwist een gepromoveerde interactie:
    /// het sterkere model oordeelde "onjuist" of "niet gedragen door het bewijs".
    /// NIET uit de ContradictionDetector maar uit BreinInteractionAuditService —
    /// het is de zichtbare-kanaal-route van de harde regel dat een LLM-oordeel
    /// nooit zelfstandig een tier verandert: een beheerder beslist.</summary>
    public const string AuditDisputesInteraction = "audit-disputes-interaction";

    public static readonly IReadOnlyList<string> All =
        [ClaimContradictsOfficial, RulingCollision, DisjointnessViolation,
         AuditDisputesInteraction];
}

/// <summary>Levenscyclus van een tegenspraak-rij.</summary>
public static class ReasoningConflictStatus
{
    public const string Open = "open";
    public const string Reviewed = "reviewed";
    public const string Resolved = "resolved";
    /// <summary>Beoordeeld als geen echte tegenspraak (herstelpad: niet stil
    /// weggooien, wél afsluiten).</summary>
    public const string Dismissed = "dismissed";

    public static readonly IReadOnlyList<string> All = [Open, Reviewed, Resolved, Dismissed];
}

/// <summary>De ENIGE routerings-bron: welk kanaal hoort bij welke tegenspraak-soort
/// (fase 3, #227). Detector-patronen halen hun kanaal hiervandaan zodat routering
/// op één plek getest wordt.</summary>
public static class ConflictRouter
{
    public static ConflictChannel Route(string kind) => kind switch
    {
        ReasoningConflictKind.ClaimContradictsOfficial => ConflictChannel.Misconception,
        ReasoningConflictKind.RulingCollision => ConflictChannel.Escalation,
        ReasoningConflictKind.DisjointnessViolation => ConflictChannel.ReviewQueue,
        // Een betwiste interactie (#255) gaat naar de reviewqueue: het oordeel is
        // van een LLM en draagt geen actie alleen — de beheerder beslist.
        ReasoningConflictKind.AuditDisputesInteraction => ConflictChannel.ReviewQueue,
        // Onbekend/nieuw soort: nooit stil laten vallen — altijd naar menselijke ogen.
        _ => ConflictChannel.ReviewQueue,
    };

    /// <summary>Kanaal als opslag-string (lowercase enum-naam).</summary>
    public static string ChannelString(ConflictChannel channel) =>
        channel.ToString().ToLowerInvariant();
}

/// <summary>Genormaliseerde dedupe-sleutel <c>patternId|subject|counter</c> zodat een
/// reasoner-run idempotent is: dezelfde tegenspraak opnieuw detecteren maakt geen
/// tweede rij. Eén bron voor <see cref="ReasoningConflict.DedupeKey"/> en de
/// service.</summary>
public static class ReasoningConflictDedupe
{
    public static string Key(string patternId, string subjectRef, string? counterRef) =>
        $"{(patternId ?? "").Trim()}|{(subjectRef ?? "").Trim()}|{(counterRef ?? "").Trim()}";
}

/// <summary>Een door de reasoner gedetecteerde tegenspraak als Postgres-rij (fase 3,
/// #227, §5). Postgres = SoT ook voor tegenspraken: de detectie draait tegen de
/// Neo4j-projectie, maar het RESULTAAT leeft hier (herbouwbaar bij een volgende
/// run). Bewust een EIGEN model naast de bron-niveau <see cref="RbRules.Domain.Conflict"/>
/// (die draagt FK's naar <c>Source</c> en detecteert bron↔bron-strijd bij de
/// ingest): een redeneer-tegenspraak verwijst naar graf-knopen via BrainRefs
/// (claim/section/card), niet naar bron-rijen. Elke rij draagt zijn herkomst
/// (<see cref="RunId"/>) en een memo met beide bron-ids — een beslissing levert
/// nooit onzichtbare state (inzicht #236).</summary>
public class ReasoningConflict
{
    public long Id { get; set; }

    /// <summary>Id van het detector-patroon dat vuurde (<see cref="ContradictionPattern.Id"/>).</summary>
    public required string PatternId { get; set; }

    /// <summary>De tegenspraak-soort (<see cref="ReasoningConflictKind"/>).</summary>
    public required string Kind { get; set; }

    /// <summary>Het kanaal waarheen deze rij is gerouteerd
    /// (<see cref="ConflictRouter"/>): misconception | reviewqueue | escalation.</summary>
    public required string Channel { get; set; }

    /// <summary>BrainRef van de betrokken knoop (bv. "claim:17" of
    /// "card:ogn-011-298").</summary>
    public required string SubjectRef { get; set; }

    /// <summary>BrainRef van de tegensprekende knoop (bv. "section:core-rules-pdf/7.4"
    /// of de tweede ruling); null voor een enkel-knoop-tegenspraak zoals een
    /// disjointness-schending.</summary>
    public string? CounterRef { get; set; }

    /// <summary>Menselijk leesbaar bewijs/memo — de claim-tekst, de botsende
    /// ruling-teksten, of het disjuncte labelpaar.</summary>
    public string? Memo { get; set; }

    /// <summary>Genormaliseerde idempotentie-sleutel (patternId|subject|counter).</summary>
    public required string DedupeKey { get; set; }

    /// <summary>Provenance (#233/#236): de ULID van de reasoner-<see cref="MiningRun"/>
    /// die deze tegenspraak vaststelde.</summary>
    public required string RunId { get; set; }

    public string Status { get; set; } = ReasoningConflictStatus.Open;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}
