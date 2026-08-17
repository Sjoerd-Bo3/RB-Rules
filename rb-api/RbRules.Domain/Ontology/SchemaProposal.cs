namespace RbRules.Domain.Ontology;

/// <summary>De staging-namespace voor schema-evolutie (fase 6, #230, §6). Mining mag
/// een nieuw, nog-niet-gehard type als <c>:Proposed</c> in de graaf neerzetten:
/// retrieval-zichtbaar maar met een LAGE weging, en het kan NIETS harden. Zo breekt
/// een nieuwe set niets — een onbekend keyword/relatietype landt hier, niet in de
/// frozen core, tot een deterministisch-onderbouwde, gereviewde, versioned migratie
/// het promoveert.</summary>
public static class StagingNamespace
{
    /// <summary>Neo4j-label-prefix voor een nog-niet-gehard type (<c>:Proposed</c>).
    /// De projectie zet dit label naast het kandidaat-type; de brein-API mag er nooit
    /// een bindend citaat op baseren.</summary>
    public const string ProposedLabel = "Proposed";

    /// <summary>Zeer lage retrieval-weging (§6: "lage weging, kan niets harden").
    /// Ver onder elke piramidelaag zodat een voorgesteld type nooit een gehard feit
    /// verdringt.</summary>
    public const double ProposedTrustWeight = 0.1;
}

/// <summary>Wat er voorgesteld wordt (fase 6, #230). Een nieuw relatietype of een
/// nieuwe subklasse is een schema-evolutie-EVENT — geen instantie — en gaat daarom
/// altijd door de reviewqueue (§6). Nieuwe instanties (een keyword-WOORD, kaarten)
/// zijn géén proposal maar gewone data (patch-bump).</summary>
public enum SchemaProposalKind
{
    /// <summary>Additief nieuw relatietype (bv. <c>REDIRECTS</c>) → minor-bump.</summary>
    RelationType,
    /// <summary>Nieuwe subklasse onder een bestaande klasse (bv. een
    /// mechanic-subclass) → minor-bump.</summary>
    Subclass,
    /// <summary>Nieuwe top-level klasse → minor-bump.</summary>
    Class,
    /// <summary>Wijziging aan de disjointness-assen of een klasse-split → major-bump
    /// (her-validatie van de hele graaf).</summary>
    Disjointness,
}

/// <summary>Levensfase van een schema-voorstel. Een afgewezen voorstel BLIJFT als rij
/// bestaan (audit-spoor, tombstone-discipline) — nooit hard-delete.</summary>
public static class SchemaProposalStatus
{
    /// <summary>In de staging-namespace, wacht op deterministisch bewijs + review.</summary>
    public const string Proposed = "proposed";
    /// <summary>Gereviewd en goedgekeurd; klaar voor een versioned migratie.</summary>
    public const string Approved = "approved";
    /// <summary>Gereviewd en afgewezen (blijft als tombstone bestaan).</summary>
    public const string Rejected = "rejected";
    /// <summary>De versioned migratie is uitgevoerd; het type is nu gehard.</summary>
    public const string Migrated = "migrated";

    public static readonly IReadOnlyList<string> All = [Proposed, Approved, Rejected, Migrated];
    public static bool IsValid(string? s) => s is not null && All.Contains(s);
}

/// <summary>Eén schema-evolutie-voorstel (fase 6, #230) als first-class rij: WELK
/// type, met WELK deterministisch bewijs, in WELKE fase, DOOR welke run — de
/// provenance-tak van de schema-poort. Postgres = SoT; de <c>:Proposed</c>-projectie
/// is idempotent herbouwbaar.</summary>
public class SchemaProposal
{
    public long Id { get; set; }

    /// <summary><see cref="SchemaProposalKind"/> als string.</summary>
    public required string Kind { get; set; }

    /// <summary>De voorgestelde canonieke naam — een relatie-edge-naam
    /// (SCREAMING_SNAKE_CASE, bv. <c>REDIRECTS</c>) of een klasse-/subklasse-naam
    /// (PascalCase, bv. <c>Overload</c>).</summary>
    public required string ProposedName { get; set; }

    /// <summary>Voor een subklasse-voorstel: de bestaande superklasse-naam waaronder
    /// het valt (bv. <c>Mechanic</c>). Null voor een relatietype/top-level klasse.</summary>
    public string? ParentType { get; set; }

    /// <summary>Deterministisch bewijs #1 (§6): het aantal OFFICIËLE kaarten dat het
    /// voorgestelde type gebruikt. Een fictief keyword <c>Overload</c> vereist ≥N
    /// zulke kaarten — een LLM-vermoeden alléén hardt nooit (rode draad #236).</summary>
    public int OfficialCardCount { get; set; }

    /// <summary>Deterministisch bewijs #2 (§6): draagt een officiële Core-Rules-/
    /// glossary-sectie dit type? Zonder deze verankering blijft het voorstel in
    /// staging, hoe vaak een model het ook noemt.</summary>
    public bool HasRuleSectionEvidence { get; set; }

    /// <summary>BrainRef van de verankerende sectie (bv. "section:core-rules-pdf/9.2");
    /// null zolang <see cref="HasRuleSectionEvidence"/> false is.</summary>
    public string? RuleSectionRef { get; set; }

    /// <summary>De memo: waarom dit voorstel, met de bewijs-telling ("gebruikt op 7
    /// officiële kaarten; verankerd in §9.2"). Rode draad #236.</summary>
    public required string Memo { get; set; }

    /// <summary><see cref="SchemaProposalStatus"/> — start op <c>proposed</c>.</summary>
    public string Status { get; set; } = SchemaProposalStatus.Proposed;

    /// <summary>De bump-soort die de migratie zou dragen (<see cref="OntologyBumpKind"/>
    /// als string); afgeleid uit <see cref="Kind"/> — RelationType/Subclass/Class →
    /// minor, Disjointness → major.</summary>
    public required string BumpKind { get; set; }

    /// <summary>De ontologie-versie waarin het voorstel geland is (gezet bij migratie);
    /// null zolang niet gemigreerd.</summary>
    public string? MigratedInVersion { get; set; }

    /// <summary>0a-provenance (#233): de mining-/resolutie-run die dit voorstel opwierp.</summary>
    public required string RunId { get; set; }

    /// <summary>De review-motivatie (goedkeuring/afwijzing) — blijft als audit-spoor.</summary>
    public string? ReviewNote { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public OntologyBumpKind BumpKindEnum => Kind == SchemaProposalKind.Disjointness.ToString()
        ? OntologyBumpKind.Major
        : OntologyBumpKind.Minor;
}

/// <summary>De deterministische promotie-poort voor schema-evolutie (fase 6, #230,
/// §6) — puur, €0. Een <c>:Proposed</c>-type mag NOOIT stil harden: promotie vereist
/// deterministisch bewijs (≥N officiële kaarten ÉN een Core-Rules/glossary-sectie) én
/// daarna menselijke review (elke schema-wijziging gaat door de reviewqueue). De
/// poort beslist alléén of een voorstel de review-drempel haalt — nooit of het
/// definitief gemigreerd wordt (dat blijft een expliciete beheerdersactie).</summary>
public static class SchemaProposalGate
{
    /// <summary>Standaard-drempel: hoeveel officiële kaarten een voorgesteld type
    /// minstens moet gebruiken (§6 "≥N officiële kaarten"). Conservatief; een set
    /// introduceert een echt keyword typisch op meerdere kaarten.</summary>
    public const int DefaultMinOfficialCards = 3;

    public enum Code { InsufficientOfficialCards, MissingRuleSection, EmptyName }

    public sealed record Violation(Code Code, string Message);

    public sealed record Result(bool EligibleForReview, IReadOnlyList<Violation> Violations)
    {
        public string? Reason => Violations.Count == 0 ? null : string.Join("; ", Violations.Select(v => v.Message));
        public static readonly Result Eligible = new(true, []);
    }

    /// <summary>Haalt dit voorstel de review-drempel? Ontbreekt het deterministische
    /// bewijs, dan blijft het in staging (retrieval-zichtbaar, lage weging) — het
    /// wordt niet weggegooid, alleen niet ter promotie voorgelegd.</summary>
    public static Result Evaluate(SchemaProposal proposal, int minOfficialCards = DefaultMinOfficialCards)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var v = new List<Violation>();

        if (string.IsNullOrWhiteSpace(proposal.ProposedName))
            v.Add(new(Code.EmptyName, "Voorstel mist een naam."));

        if (proposal.OfficialCardCount < minOfficialCards)
            v.Add(new(Code.InsufficientOfficialCards,
                $"Slechts {proposal.OfficialCardCount} officiële kaart(en); ≥{minOfficialCards} vereist voor promotie."));

        if (!proposal.HasRuleSectionEvidence || string.IsNullOrWhiteSpace(proposal.RuleSectionRef))
            v.Add(new(Code.MissingRuleSection,
                "Geen verankerende Core-Rules/glossary-sectie; blijft in staging."));

        return v.Count == 0 ? Result.Eligible : new(false, v);
    }
}

/// <summary>Beleid voor een nieuw GEKWALIFICEERD relatie-voorstel (fase 6, §6):
/// default REÏFICEREN (als <c>:Interaction {kind:…}</c>), een first-class edge-type
/// (bv. <c>REDIRECTS</c>) alléén bij HOGE frequentie én aantoonbare retrieval-waarde,
/// via review. Puur; houdt de graaf schoon (faalmodus #2/#3) i.p.v. elk vermoeden
/// tot een kale edge te promoveren.</summary>
public static class RelationProposalPolicy
{
    /// <summary>Frequentie-drempel waarboven een gereïficeerde interactie-soort een
    /// eigen first-class edge-type mag worden — mits óók retrieval-waarde.</summary>
    public const int FirstClassEdgeFrequencyThreshold = 25;

    /// <summary>Blijft dit gekwalificeerde relatie-voorstel default gereïficeerd, of
    /// verdient het een eigen edge-type? true = reïficeren (de veilige default).</summary>
    public static bool ShouldReifyByDefault(int observedFrequency, bool hasRetrievalValue) =>
        observedFrequency < FirstClassEdgeFrequencyThreshold || !hasRetrievalValue;
}
