namespace RbRules.Domain.Ontology;

/// <summary>Het ontologie-schema v0 van het Poracle-brein als onveranderlijk,
/// machine-leesbaar register (kritiek A3: dit is de ÉNE bron waaruit later
/// prompt-enums, de parser-poort en Neo4j-constraints gegenereerd worden —
/// geen losse constanten elders). Bevat de klassenhiërarchie (SUBCLASS_OF,
/// transitief + acyclisch), de kern-relaties met domain/range/kardinaliteit/
/// logische eigenschappen, en de disjointness-assen. Alles pure data + pure
/// afleidingen (geen IO); <see cref="OntologyValidationService"/> gebruikt dit
/// als deterministische poort.
///
/// Bewuste modelleer-keuze t.o.v. de kale ASCII-boom in §2.1: <c>Card</c> hangt
/// NIET onder <c>Object</c>. De object-kaarttypes (Unit/Legend/Gear/Battlefield/
/// Rune/Token) erven van zowel <c>Card</c> als <c>Object</c> (multi-parent →
/// het multi-label <c>:Object:Card:Unit</c> dat §2.1 noemt), terwijl <c>Spell</c>
/// enkel een <c>Card</c> is. Zo blijft <c>MATCH (c:Card)</c> polymorf over álle
/// kaarten én blijft de bindende disjointness <c>Spell ⟂ Object</c> vervulbaar —
/// bij <c>Card ⊑ Object</c> zou elke Spell tegelijk Object én niet-Object zijn,
/// een lege (onvervulbare) klasse.</summary>
public static class OntologySchema
{
    // ── Klassenhiërarchie (SUBCLASS_OF-doelen per klasse) ────────────────────
    private static readonly OntologyClass[] ClassList =
    [
        new(EntityType.Thing, [], "Wortel van de hiërarchie."),

        new(EntityType.Object, [EntityType.Thing], "Bestaat fysiek in het spel."),
        new(EntityType.Card, [EntityType.Thing], "Gedrukte kaart {riotCardId, name, setId, printedText}."),
        // Object-kaarttypes: multi-parent Card + Object (multi-label :Object:Card:X).
        new(EntityType.Unit, [EntityType.Card, EntityType.Object], "Unit-kaart."),
        new(EntityType.Legend, [EntityType.Card, EntityType.Object], "Legend-kaart."),
        new(EntityType.Gear, [EntityType.Card, EntityType.Object], "Gear-kaart."),
        new(EntityType.Battlefield, [EntityType.Card, EntityType.Object], "Battlefield-kaart."),
        new(EntityType.Rune, [EntityType.Card, EntityType.Object], "Rune-kaart."),
        new(EntityType.Token, [EntityType.Card, EntityType.Object], "Token-kaart."),
        // Spell is Card maar DISJUNCT van Object (resolvet en verlaat het spel).
        new(EntityType.Spell, [EntityType.Card], "Spell-kaart; disjunct van Object."),

        new(EntityType.Concept, [EntityType.Thing], "Spel-abstractie."),
        new(EntityType.Mechanic, [EntityType.Concept], "Spelprocedure (bv. showdown, conquer)."),
        new(EntityType.Keyword, [EntityType.Concept], "Gedrukte kaarttekst met regel-betekenis (Deflect, Tank…)."),
        new(EntityType.Status, [EntityType.Concept], "Toestand die een Object heeft (Exhausted, Stunned…)."),
        new(EntityType.Zone, [EntityType.Concept], "Spelzone."),
        new(EntityType.Phase, [EntityType.Concept], "Spelfase."),
        new(EntityType.Window, [EntityType.Concept], "Timing-window."),
        new(EntityType.Trigger, [EntityType.Concept], "Trigger-conditie."),
        new(EntityType.Effect, [EntityType.Concept], "Effect."),
        new(EntityType.Cost, [EntityType.Concept], "Kosten."),
        new(EntityType.Domain, [EntityType.Concept], "Kleur/domein van een kaart."),

        new(EntityType.NormativeSource, [EntityType.Thing], "Normatieve bron; draagt authorityRank."),
        new(EntityType.RuleSection, [EntityType.NormativeSource], "Sectie uit de officiële regels."),
        new(EntityType.Ruling, [EntityType.NormativeSource], "Officiële ruling."),
        new(EntityType.Erratum, [EntityType.NormativeSource], "Erratum op een kaart/keyword."),

        new(EntityType.Claim, [EntityType.Thing], "Community-lezing; NOOIT authorityRank."),

        new(EntityType.Interaction, [EntityType.Thing], "Gereïficeerde n-aire relatie."),
        new(EntityType.Condition, [EntityType.Thing], "Gereïficeerde voorwaarde op een Interaction."),
        new(EntityType.Assertion, [EntityType.Thing], "Gereïficeerd feit-met-provenance (PROV-O)."),
        new(EntityType.Set, [EntityType.Thing], "Set / ontologie-versie-anker."),
    ];

    /// <summary>Alle klassen, geïndexeerd op type.</summary>
    public static readonly IReadOnlyDictionary<EntityType, OntologyClass> Classes =
        ClassList.ToDictionary(c => c.Type);

    // ── Disjointness-assen (ongeordende paren) ───────────────────────────────
    // Keyword ⟂ Mechanic ⟂ Status (drie disjuncte assen, paarsgewijs) en
    // Spell ⟂ Object. Overerving telt mee: Unit ⊑ Object, dus Unit ⟂ Spell.
    private static readonly (EntityType, EntityType)[] DisjointPairsRaw =
    [
        (EntityType.Keyword, EntityType.Mechanic),
        (EntityType.Mechanic, EntityType.Status),
        (EntityType.Keyword, EntityType.Status),
        (EntityType.Spell, EntityType.Object),
    ];

    /// <summary>De gedeclareerde disjuncte klassenparen (ongeordend). De
    /// effectieve disjointness houdt via <see cref="AreDisjoint"/> ook rekening
    /// met overerving.</summary>
    public static readonly IReadOnlyList<(EntityType A, EntityType B)> DisjointPairs = DisjointPairsRaw;

    // ── Kern-relaties (§2.2) ─────────────────────────────────────────────────
    private static readonly EntityType[] AnyCard =
        [EntityType.Card]; // subclass-gesloten: dekt Unit/Legend/…/Spell

    private static readonly OntologyRelation[] RelationList =
    [
        // Meta-relatie tussen klassen (TBox): transitief + acyclisch.
        new(RelationType.SubclassOf, "SUBCLASS_OF",
            Domain: [EntityType.Thing], Range: [EntityType.Thing],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Transitive | RelationTraits.Acyclic,
            Parameters: []),

        new(RelationType.InDomain, "IN_DOMAIN",
            Domain: AnyCard, Range: [EntityType.Domain],
            MinCardinality: 1, MaxCardinality: null,          // 1..* (Colorless = 1)
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.HasKeyword, "HAS_KEYWORD",
            Domain: AnyCard, Range: [EntityType.Keyword],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None,
            Parameters: ["magnitude"]),                        // Tank N, Accelerate N — niet wegstrippen

        new(RelationType.Invokes, "INVOKES",
            Domain: [EntityType.Keyword], Range: [EntityType.Mechanic],
            MinCardinality: 1, MaxCardinality: null,          // 1..*
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.HasStatus, "HAS_STATUS",
            Domain: [EntityType.Object], Range: [EntityType.Status],
            MinCardinality: 0, MaxCardinality: null,          // ABox spelstaat
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.GovernedBy, "GOVERNED_BY",
            Domain: [EntityType.Concept, EntityType.Card, EntityType.Interaction],
            Range: [EntityType.RuleSection],
            MinCardinality: 1, MaxCardinality: null,          // 1..*, vaak afgeleid
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.ErrataOf, "ERRATA_OF",
            Domain: [EntityType.Erratum], Range: [EntityType.Card, EntityType.Keyword],
            MinCardinality: 1, MaxCardinality: 1,             // precies 1, functioneel
            Traits: RelationTraits.Functional, Parameters: []),

        new(RelationType.Supersedes, "SUPERSEDES",
            Domain: [EntityType.NormativeSource], Range: [EntityType.NormativeSource],
            MinCardinality: 0, MaxCardinality: 1,             // 0..1
            Traits: RelationTraits.Transitive | RelationTraits.Acyclic, Parameters: []),

        new(RelationType.Corroborates, "CORROBORATES",
            Domain: [EntityType.Claim], Range: [EntityType.Claim, EntityType.NormativeSource],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.Contradicts, "CONTRADICTS",
            Domain: [EntityType.Claim], Range: [EntityType.Claim, EntityType.NormativeSource],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Symmetric, Parameters: []),

        new(RelationType.IntroducedIn, "INTRODUCED_IN",
            Domain: [EntityType.Card, EntityType.Keyword, EntityType.Mechanic],
            Range: [EntityType.Set],
            MinCardinality: 1, MaxCardinality: 1,             // precies 1, functioneel
            Traits: RelationTraits.Functional, Parameters: []),

        // Gekwalificeerde relaties: verboden als kale edge → altijd via Interaction.
        new(RelationType.Counters, "COUNTERS",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Modifies, "MODIFIES",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Grants, "GRANTS",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Requires, "REQUIRES",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),

        // Pre-ontologische hint (bestaande codebase): geen kennis, wel provenance.
        new(RelationType.InteractsWith, "INTERACTS_WITH",
            Domain: AnyCard, Range: AnyCard,
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Symmetric,
            Parameters: ["status"]),                           // candidate|verified|promoted

        // Gedenormaliseerde retrieval-projectie (nooit bron van waarheid).
        new(RelationType.RelatesTo, "RELATES_TO",
            Domain: [EntityType.Concept, EntityType.Card],
            Range: [EntityType.Concept, EntityType.Card],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None,
            Parameters: ["kind", "window", "actor_status", "cost_delta", "tier"]),
    ];

    /// <summary>Alle relatietypes, geïndexeerd op type.</summary>
    public static readonly IReadOnlyDictionary<RelationType, OntologyRelation> Relations =
        RelationList.ToDictionary(r => r.Type);

    private static readonly IReadOnlyDictionary<string, OntologyRelation> RelationsByEdgeName =
        RelationList.ToDictionary(r => r.EdgeName, StringComparer.OrdinalIgnoreCase);

    // ── Afleidingen over de hiërarchie ───────────────────────────────────────

    /// <summary>Alle strikte voorouders (transitief, exclusief het type zelf).
    /// De hiërarchie is per constructie acyclisch, dus de doorloop termineert.</summary>
    public static IReadOnlyCollection<EntityType> Ancestors(EntityType type)
    {
        var acc = new HashSet<EntityType>();
        void Walk(EntityType t)
        {
            if (!Classes.TryGetValue(t, out var def)) return;
            foreach (var p in def.DirectParents)
                if (acc.Add(p)) Walk(p);
        }
        Walk(type);
        return acc;
    }

    /// <summary>Reflexief-transitieve subklasse-toets: is <paramref name="sub"/>
    /// hetzelfde als of een (transitieve) subklasse van <paramref name="super"/>?
    /// Dit maakt domain/range subclass-polymorf — een Unit voldoet aan een
    /// Object-domein.</summary>
    public static bool IsA(EntityType sub, EntityType super) =>
        sub == super || Ancestors(sub).Contains(super);

    /// <summary>Strikte subklasse-toets (exclusief gelijkheid).</summary>
    public static bool IsStrictSubclassOf(EntityType sub, EntityType super) =>
        sub != super && Ancestors(sub).Contains(super);

    /// <summary>Zijn twee klassen (effectief) disjunct? Twee typen zijn disjunct
    /// zodra een voorouder-of-zichzelf van de één met een voorouder-of-zichzelf
    /// van de ander een gedeclareerd disjunct paar vormt. Zo erft Unit
    /// (⊑ Object) de disjointness met Spell.</summary>
    public static bool AreDisjoint(EntityType a, EntityType b)
    {
        var closureA = SelfAndAncestors(a);
        var closureB = SelfAndAncestors(b);
        foreach (var (x, y) in DisjointPairsRaw)
            if ((closureA.Contains(x) && closureB.Contains(y)) ||
                (closureA.Contains(y) && closureB.Contains(x)))
                return true;
        return false;
    }

    private static HashSet<EntityType> SelfAndAncestors(EntityType type)
    {
        var set = new HashSet<EntityType>(Ancestors(type)) { type };
        return set;
    }

    /// <summary>Zoek een relatie op canonieke edge-naam (SCREAMING_SNAKE_CASE),
    /// hoofdletterongevoelig — de vorm waarin de LLM-output binnenkomt.</summary>
    public static OntologyRelation? RelationByEdgeName(string edgeName) =>
        edgeName is not null && RelationsByEdgeName.TryGetValue(edgeName.Trim(), out var r) ? r : null;

    /// <summary>Parse een klassenaam (enum-naam, hoofdletterongevoelig).
    /// Retourneert <c>null</c> bij een onbekende naam — de deterministische
    /// poort geeft dan een UnknownEntityType-schending in plaats van te gokken.
    /// Accepteert UITSLUITEND een exacte enum-naam: <see cref="Enum.TryParse{T}(string,bool,out T)"/>
    /// slikt óók kale getallen ("5" → Gear) en OR-combinaties ("Card,Unit" →
    /// Unit), wat malformed rb-ai-output (gelekte index/id, aan-elkaar-geplakte
    /// labels) stil zou laten passeren; de naam-gelijkheids-guard verwerpt dat.</summary>
    public static EntityType? ParseEntityType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return Enum.TryParse<EntityType>(trimmed, ignoreCase: true, out var t)
            && Classes.ContainsKey(t)
            && t.ToString().Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            ? t : null;
    }
}
