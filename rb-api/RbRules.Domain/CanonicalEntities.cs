using Pgvector;

namespace RbRules.Domain;

/// <summary>Fase 1 (#225) — de canonieke entiteit: één rij per mechanic/keyword/
/// concept, de blauwdruk "project canoniek, resolve op alles". Versla faalmodus #1
/// (duplicatie): mechanics/keywords zijn niet langer losse strings op
/// <c>Card.Mechanics[]</c> maar entiteiten met één <see cref="CanonicalLabel"/> en
/// een alias-lexicon (<see cref="AltLabels"/>) waartegen nieuwe mining eerst
/// resolvet vóór er een nieuwe kandidaat ontstaat (versla #2, synoniem-
/// proliferatie). De laag is ADDITIEF: bestaande <c>Card.Mechanics</c>-strings
/// blijven ongemoeid; entiteiten worden hier los geregistreerd en alleen via de
/// review-poort samengevoegd.
///
/// Merge is NOOIT een hard-delete: een samengevoegde entiteit blijft als tombstone
/// bestaan (<see cref="Status"/> = merged, <see cref="MergedIntoId"/> gezet) en is
/// via <c>EntityResolutionService.UnconsolidateAsync</c> omkeerbaar. Postgres is de
/// bron van waarheid; de Neo4j-projectie is idempotent herbouwbaar (MERGE op de
/// canonieke id).</summary>
public class CanonicalEntity : IEmbeddable
{
    public long Id { get; set; }

    /// <summary>mechanic | keyword | concept — de Concept-tak van de ontologie
    /// (<see cref="CanonicalEntityKinds"/>). Bepaalt het Neo4j-label bij projectie
    /// en het BrainRef-domein.</summary>
    public required string Kind { get; set; }

    /// <summary>De canonieke, gedrukte naam (EN, brontaal — CONVENTIONS #187),
    /// magnitude-vrij: de FAMILIE-naam. 'Assault 2' en 'Assault 3' delen de
    /// canonieke entiteit 'Assault'; de magnitude rijdt als parameter mee op
    /// HAS_MECHANIC (kritiek Risico 2a), zij wordt NOOIT in het label weggestript
    /// tot een aparte entiteit.</summary>
    public required string CanonicalLabel { get; set; }

    /// <summary>Het alias-lexicon: synoniemen/community-varianten die naar deze
    /// entiteit resolven (case/whitespace-genormaliseerd bij het opzoeken). 'Death
    /// Knell' en 'Deflecting' landen hier zodat mining ze niet als nieuwe entiteit
    /// aanmaakt.</summary>
    public string[] AltLabels { get; set; } = [];

    /// <summary>Korte definitie (EN) — de tekst waarover de embedding wordt
    /// berekend (§3.2 stap 3: cosine over de Definition, niet het label).</summary>
    public string? Definition { get; set; }

    /// <summary>candidate | canonical | merged. candidate = geregistreerd maar nog
    /// niet als gezaghebbend bevestigd; canonical = bevestigde canonieke rij;
    /// merged = tombstone (zie <see cref="MergedIntoId"/>).</summary>
    public string Status { get; set; } = CanonicalEntityStatus.Candidate;

    /// <summary>Tombstone-doel: gezet zodra deze entiteit in een andere is
    /// samengevoegd. De rij verdwijnt niet — resolutie volgt de keten naar de
    /// levende entiteit en unconsolidate kan hem herstellen.</summary>
    public long? MergedIntoId { get; set; }

    /// <summary>0a-provenance (#233): de <see cref="MiningRun"/> die deze entiteit
    /// registreerde. WAS_GENERATED_BY op entiteit-niveau — elke canonieke rij is
    /// herleidbaar tot de run/het model/het vocabulaire dat haar aandroeg.</summary>
    public required string CreatedByRunId { get; set; }

    // Embedding-provenance (heilig, #233): model + dim + content-hash per rij.
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDim { get; set; }
    public string? EmbeddingContentHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? MergedAt { get; set; }

    public BrainRef Ref => Kind switch
    {
        CanonicalEntityKinds.Mechanic => BrainRef.Mechanic(CanonicalLabel),
        CanonicalEntityKinds.Concept => BrainRef.Concept(CanonicalLabel),
        // Keyword heeft (nog) geen eigen BrainRefKind: canoniek-id-anker.
        _ => new BrainRef(BrainRefKind.Tag, CanonicalLabel),
    };
}

/// <summary>Canonieke <see cref="CanonicalEntity.Status"/>-waarden — één bron zodat
/// service, projectie en tests niet uiteenlopen.</summary>
public static class CanonicalEntityStatus
{
    public const string Candidate = "candidate";
    public const string Canonical = "canonical";
    public const string Merged = "merged";
}

/// <summary>Een voorgesteld merge-paar uit <see cref="EntityResolutionClassifier"/>
/// dat naar de reviewqueue gaat (of, bij open precisie-gate + lange labels, direct
/// wordt uitgevoerd). Persistent zodat (a) de review-poort en (b) de drift-snapshot
/// de "duplicatie-schuld" (open kandidaten) kunnen tellen zonder de duur berekening
/// te herhalen. Het paar is ongeordend genormaliseerd opgeslagen (kleinste id als
/// A) zodat er nooit twee spiegelrijen voor hetzelfde paar ontstaan.</summary>
public class MergeCandidate
{
    public long Id { get; set; }
    public required long EntityAId { get; set; }
    public required long EntityBId { get; set; }

    /// <summary>De classifier-uitslag (<see cref="EntityMergeVerdict"/> als string):
    /// review | auto_merge_candidate.</summary>
    public required string Verdict { get; set; }
    public int SignalCount { get; set; }

    /// <summary>De leesbare signaal-reden (memo-bron bij een latere merge).</summary>
    public required string Reason { get; set; }

    /// <summary>open | merged | dismissed. open = wacht op review; merged =
    /// uitgevoerd (auto of admin); dismissed = beheerder wees af (geen tombstone
    /// nodig — er is niets samengevoegd).</summary>
    public string Status { get; set; } = MergeCandidateStatus.Open;

    /// <summary>0a-provenance (#233): de resolutie-run die dit paar voorstelde.</summary>
    public required string RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}

public static class MergeCandidateStatus
{
    public const string Open = "open";
    public const string Merged = "merged";
    public const string Dismissed = "dismissed";
}

/// <summary>Expliciete merge-BESLISSING als first-class knoop (rode draad #236:
/// geen destructieve actie zonder memo + herstelpad). Legt vast WELKE entiteit in
/// WELKE is samengevoegd, DOOR wie/wat (auto vs. admin), MET welke memo en —
/// cruciaal voor het herstelpad — welke alias-labels bij de merge naar de doel-
/// entiteit VERPLAATST zijn, zodat <c>UnconsolidateAsync</c> exact díe labels weer
/// kan terugtrekken zonder legitiem-gedeelde aliassen te beschadigen.</summary>
public class MergeDecision
{
    public long Id { get; set; }

    /// <summary>De entiteit die tombstone werd (bron van de merge).</summary>
    public required long SourceEntityId { get; set; }

    /// <summary>De overlevende entiteit (doel van de merge).</summary>
    public required long TargetEntityId { get; set; }

    /// <summary>0a-provenance (#233): de run die de merge uitvoerde.</summary>
    public required string RunId { get; set; }

    /// <summary>auto | admin — hoe de merge tot stand kwam. auto mag alleen als de
    /// precisie-gate open stond (<see cref="EntityResolutionGate"/>).</summary>
    public required string DecidedBy { get; set; }

    /// <summary>De memo (rode draad): waarom deze merge, met de signaal-uitslag
    /// ("blocking=ja, trigram=0.58≥0.45, embedding=0.90≥0.85 → 3/3 signalen").</summary>
    public required string Memo { get; set; }

    /// <summary>De alias-labels die bij de merge van bron naar doel zijn verplaatst
    /// (het CanonicalLabel van de bron + diens AltLabels die het doel nog niet had).
    /// Het herstelpad: <c>UnconsolidateAsync</c> trekt precies deze terug.</summary>
    public string[] MovedAltLabels { get; set; } = [];

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gezet zodra de merge is teruggedraaid (unconsolidate). De beslissing
    /// blijft als audit-spoor bestaan; niets wordt hard-deleted.</summary>
    public bool Reverted { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
}
