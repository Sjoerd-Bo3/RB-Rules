namespace RbRules.Domain.Ontology;

/// <summary>Klassen (TBox-types) van het Poracle-brein — de knopen in de
/// klassenhiërarchie (docs/ARCHITECTURE brein-epic §2.1). Elk type wordt in
/// Neo4j een label; overerving is multi-label (een Unit-kaart draagt
/// <c>:Object:Card:Unit</c>). Dit is de ÉNE machine-leesbare bron (kritiek A3):
/// prompt-enums, de parser-poort en Neo4j-constraints worden hier later uit
/// gegenereerd — voeg dus nooit een los "magic string"-type toe naast deze
/// enum, breid altijd hier uit.</summary>
public enum EntityType
{
    // Wortel
    Thing,

    // Object-tak (bestaat fysiek in het spel) + de gedeelde Card-umbrella.
    // Card staat bewust NIET onder Object: de object-kaarttypes erven van
    // zowel Card als Object (multi-parent → :Object:Card:Unit), terwijl Spell
    // wél een Card is maar DISJUNCT van Object (spells resolven en verlaten
    // het spel). Card ⊑ Object zou Spell ⊑ Object afdwingen en de
    // disjointness onvervulbaar maken — zie OntologySchema.
    Object,
    Card,
    Unit,
    Legend,
    Gear,
    Battlefield,
    Rune,
    Token,
    Spell,

    // Concept-tak (spel-abstracties)
    Concept,
    Mechanic,
    Keyword,
    Status,
    Zone,
    Phase,
    Window,
    Trigger,
    Effect,
    Cost,
    Domain,

    // Normatieve tak (draagt authorityRank — kennispiramide type-afdwingbaar)
    NormativeSource,
    RuleSection,
    Ruling,
    Erratum,

    // Community-lezing — bewust BUITEN NormativeSource, dus nooit authorityRank
    Claim,

    // Reïficatie- en provenance-knopen
    Interaction,
    Condition,
    Assertion,

    // Versie-anker
    Set,
}

/// <summary>Logische eigenschappen van een relatietype, als vlaggen zodat de
/// TBox-export ze één-op-één kan uitschrijven. Reïficatie-dwang
/// (<see cref="RequiresReification"/>) hoort hier bij: het is een
/// schema-eigenschap van de relatie, niet van een losse triple.</summary>
[Flags]
public enum RelationTraits
{
    None = 0,
    Transitive = 1 << 0,
    Symmetric = 1 << 1,
    Functional = 1 << 2,
    Acyclic = 1 << 3,

    /// <summary>Gekwalificeerde relatie (COUNTERS/MODIFIES/GRANTS/REQUIRES):
    /// draagt altijd condities (window, status, cost-floor) en is daarom
    /// VERBODEN als kale edge — moet via een <see cref="EntityType.Interaction"/>
    /// gereïficeerd worden. De deterministische poort keurt een kale triple met
    /// deze vlag af.</summary>
    RequiresReification = 1 << 4,
}

/// <summary>Relatietypes (de kanten in de graaf). De enum-namen zijn PascalCase;
/// <see cref="OntologyRelation.EdgeName"/> draagt de canonieke Neo4j-edge-naam
/// (SCREAMING_SNAKE_CASE) waarmee de LLM-output en de graaf-writes matchen.</summary>
public enum RelationType
{
    SubclassOf,
    HasDomain,
    HasMechanic,
    Invokes,
    HasStatus,
    GovernedBy,
    ErrataOf,
    Supersedes,
    Corroborates,
    Contradicts,
    IntroducedIn,
    Counters,
    Modifies,
    Grants,
    Requires,
    InteractsWith,
    RelatesTo,
}

/// <summary>Eén klasse in de hiërarchie: haar directe superklassen
/// (SUBCLASS_OF-doelen) en een korte omschrijving. Transitieve overerving en
/// acycliciteit worden door <see cref="OntologySchema"/> afgeleid/bewaakt, niet
/// hier opgeslagen.</summary>
public sealed record OntologyClass(
    EntityType Type,
    IReadOnlyList<EntityType> DirectParents,
    string Description);

/// <summary>Eén relatietype met domain/range, kardinaliteit, logische
/// eigenschappen en toegestane edge-parameters (qualifier-cache). Domain/range
/// zijn subclass-gesloten: een lid van een subklasse van het domein voldoet
/// (een Unit voldoet aan een Object-domein). Een lege <see cref="Domain"/>/
/// <see cref="Range"/> betekent "niet als kale edge toepasbaar" — dat geldt
/// uitsluitend voor de gereïficeerde (<see cref="RelationTraits.RequiresReification"/>)
/// relaties.</summary>
public sealed record OntologyRelation(
    RelationType Type,
    string EdgeName,
    IReadOnlyList<EntityType> Domain,
    IReadOnlyList<EntityType> Range,
    int MinCardinality,
    int? MaxCardinality,
    RelationTraits Traits,
    IReadOnlyList<string> Parameters)
{
    /// <summary>Draagt deze relatie de reïficatie-dwang (gekwalificeerd,
    /// verboden als kale edge)?</summary>
    public bool MustReify => Traits.HasFlag(RelationTraits.RequiresReification);

    /// <summary>Functioneel = ten hoogste één uitgaande edge per subject.
    /// Zowel de expliciete <see cref="RelationTraits.Functional"/>-vlag als een
    /// <see cref="MaxCardinality"/> van 1 leggen dat op.</summary>
    public bool IsFunctional => Traits.HasFlag(RelationTraits.Functional) || MaxCardinality == 1;
}
