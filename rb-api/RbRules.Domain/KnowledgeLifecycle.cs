namespace RbRules.Domain;

/// <summary>De kennis-levenscyclus (fase 6, #230, §6) — één canoniek vocabulaire dat
/// de tot nu toe verspreide toestand-woorden (fase 1 <c>merged</c>, fase 2
/// <c>rejected</c>/promoted, ruling-deprecatie) overkoepelt. Een feit stroomt van
/// <see cref="Active"/> via her-verificatie-triggers naar <see cref="Stale"/>, en via
/// errata/merge/intrekking naar <see cref="Deprecated"/>/<see cref="Superseded"/>/
/// <see cref="Tombstoned"/> — nooit hard-delete. <see cref="Restored"/> is het
/// expliciete herstelpad terug naar <see cref="Active"/>.</summary>
public static class LifecycleState
{
    public const string Active = "active";
    public const string Stale = "stale";
    public const string Deprecated = "deprecated";
    public const string Superseded = "superseded";
    public const string Tombstoned = "tombstoned";
    public const string Restored = "restored";

    public static readonly IReadOnlyList<string> All =
        [Active, Stale, Deprecated, Superseded, Tombstoned, Restored];

    public static bool IsValid(string? s) => s is not null && All.Contains(s);

    /// <summary>Toegestane transities. Bewust géén hard-delete; en een getombsteend
    /// of superseded feit kan alléén via een expliciete <see cref="Restored"/>-actie
    /// terug — nooit stil heropend (rode draad #236).</summary>
    public static bool CanTransition(string? from, string? to)
    {
        if (!IsValid(from) || !IsValid(to)) return false;
        return (from, to) switch
        {
            (Active, Stale) => true,
            (Active, Deprecated) => true,
            (Active, Superseded) => true,
            (Active, Tombstoned) => true,
            (Stale, Active) => true,          // her-geverifieerd
            (Stale, Deprecated) => true,
            (Stale, Superseded) => true,
            (Stale, Tombstoned) => true,
            // Herstelpad: elk "einde" mag expliciet terug naar Restored (→ Active).
            (Deprecated, Restored) => true,
            (Superseded, Restored) => true,
            (Tombstoned, Restored) => true,
            (Restored, Active) => true,
            _ => false,
        };
    }
}

/// <summary>De reden dat een feit her-verificatie nodig heeft (fase 6, §6). Meerdere
/// kunnen tegelijk vuren; de service verzamelt ze als memo.</summary>
public enum RecheckTrigger
{
    /// <summary>Leeftijd overschreed de tier-drempel (recency-verval, §6).</summary>
    AgeThreshold,
    /// <summary>Het rb-ai-model is opgewaardeerd sinds de mining (§6 model-bump).</summary>
    ModelUpgrade,
    /// <summary>Het embedding-model/-rev is opgewaardeerd (bge-m3-opvolger).</summary>
    EmbeddingUpgrade,
    /// <summary>De corroboratie zakte onder de baseline (bronnen vielen weg).</summary>
    CorroborationDrop,
    /// <summary>Nieuwe errata op de betrokken RuleSection.</summary>
    NewErrataOnSection,
    /// <summary>Negatieve /ask-signalen boven de drempel (het feit misleidt).</summary>
    NegativeAskSignal,
}

/// <summary>Puur, tier-bewust oordeel of een feit her-geverifieerd moet worden (fase
/// 6, §6). λ per tier: officieel vervalt niet op leeftijd (alleen via SUPERSEDES/
/// errata), community/meta agressief. Geen IO — de aanroeper levert de tellingen.</summary>
public static class StalenessEvaluator
{
    /// <summary>De leeftijd-drempel (dagen) per authority-tier waarboven de
    /// <see cref="RecheckTrigger.AgeThreshold"/> vuurt. <c>null</c> = vervalt niet op
    /// leeftijd (officieel). Community half-life ~6–9mnd, meta in weken (§6).</summary>
    public static int? AgeThresholdDays(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "official" => null,          // alleen via SUPERSEDES/errata
        "verified_ruling" => 730,    // ruling: klein verval
        "primer" => 365,             // primer: gemiddeld
        "community" => 210,          // ~7 maanden
        "meta" => 30,                // agressief (weken)
        _ => 365,                    // onbekend tier: behoudend
    };

    public sealed record Input(
        string Tier,
        DateTimeOffset AssertedAt,
        DateTimeOffset Now,
        string? MinedModel,
        string? CurrentModel,
        string? MinedEmbeddingRev,
        string? CurrentEmbeddingRev,
        double? CorroborationNow,
        double? CorroborationBaseline,
        bool HasNewErrataOnSection,
        int NegativeAskSignals,
        int NegativeAskThreshold = 3);

    public sealed record Verdict(IReadOnlyList<RecheckTrigger> Triggers)
    {
        public bool IsStale => Triggers.Count > 0;
        public string Memo => IsStale
            ? "her-verificatie: " + string.Join(", ", Triggers.Select(t => t.ToString()))
            : "vers";
    }

    public static Verdict Evaluate(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var triggers = new List<RecheckTrigger>();

        var threshold = AgeThresholdDays(input.Tier);
        if (threshold is { } days)
        {
            var age = input.Now - input.AssertedAt;
            if (age.TotalDays > days) triggers.Add(RecheckTrigger.AgeThreshold);
        }

        if (!string.IsNullOrWhiteSpace(input.CurrentModel) &&
            !string.Equals(input.MinedModel, input.CurrentModel, StringComparison.Ordinal))
            triggers.Add(RecheckTrigger.ModelUpgrade);

        if (!string.IsNullOrWhiteSpace(input.CurrentEmbeddingRev) &&
            !string.Equals(input.MinedEmbeddingRev, input.CurrentEmbeddingRev, StringComparison.Ordinal))
            triggers.Add(RecheckTrigger.EmbeddingUpgrade);

        if (input.CorroborationNow is { } now && input.CorroborationBaseline is { } baseline &&
            now < baseline - 0.15)   // merkbare daling, niet ruis
            triggers.Add(RecheckTrigger.CorroborationDrop);

        if (input.HasNewErrataOnSection)
            triggers.Add(RecheckTrigger.NewErrataOnSection);

        if (input.NegativeAskSignals >= input.NegativeAskThreshold)
            triggers.Add(RecheckTrigger.NegativeAskSignal);

        return new Verdict(triggers);
    }
}

/// <summary>Eén levenscyclus-transitie als first-class rij (fase 6, #230, §6/#236) —
/// het geconsolideerde tombstone-/deprecatie-/her-verificatie-spoor. Vervangt niet de
/// bestaande, specifieke tombstones (fase 1 <c>merge_decision</c>, fase 2
/// <c>rejection_tombstone</c>) maar overkoepelt ze met één auditeerbaar
/// gebeurtenis-log: WELK feit, van WELKE naar WELKE toestand, WAAROM, DOOR wie, met
/// WELK herstelpad. Niets levert onzichtbare state; niets wordt hard-deleted.</summary>
public class LifecycleEvent
{
    public long Id { get; set; }

    /// <summary>BrainRef van het geraakte feit (<see cref="BrainRef.Format"/>), bv.
    /// "ruling:42", "assertion:01J…", "relation:7".</summary>
    public required string SubjectRef { get; set; }

    /// <summary>relation | card_interaction | ruling | assertion | eval_case | … —
    /// het soort feit (spiegelt <see cref="Assertion.FactKind"/>).</summary>
    public required string FactKind { get; set; }

    /// <summary><see cref="LifecycleState"/> vóór en ná de transitie.</summary>
    public required string FromState { get; set; }
    public required string ToState { get; set; }

    /// <summary>De aanleiding: een <see cref="RecheckTrigger"/>-memo, een
    /// SUPERSEDES-oorzaak ("erratum 12 supersedes ruling 42"), of een merge/intrekking.</summary>
    public required string Reason { get; set; }

    /// <summary>Wie/wat de transitie deed: "gate" | "admin" | "model_upgrade" |
    /// "errata" | "staleness".</summary>
    public required string Actor { get; set; }

    /// <summary>Bij een SUPERSEDES-transitie: de BrainRef van de vervangende bron
    /// (het erratum/de nieuwe ruling). Null voor niet-supersede-transities.</summary>
    public string? SupersededByRef { get; set; }

    /// <summary>Het expliciete herstelpad (tekstueel, want de heropening is bewust
    /// handmatig — geen automatische terugdraai). Bv. "admin:restore-ruling".</summary>
    public string RestorePath { get; set; } = "admin:restore";

    /// <summary>0a-provenance (#233): de run die de transitie vastlegde.</summary>
    public required string RunId { get; set; }

    /// <summary>Opgeheven (herstel): de gebeurtenis blijft als audit-spoor bestaan,
    /// maar de <see cref="ToState"/> geldt niet langer.</summary>
    public bool Reverted { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
