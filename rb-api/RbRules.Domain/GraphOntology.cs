namespace RbRules.Domain;

/// <summary>Knooptype in de kennisgraaf.</summary>
public enum NodeType
{
    Card, Set, Domain, Tag, Mechanic, RuleSection, Concept, Erratum, BanEntry,
}

/// <summary>Relatietype tussen twee knopen.</summary>
public enum EdgeType
{
    FROM_SET, HAS_DOMAIN, HAS_TAG, HAS_MECHANIC, INTERACTS_WITH,
    PARENT_OF, DEFINES, GOVERNED_BY, EXPLAINS, AMENDS, BANS,
}

public record EdgeSpec(EdgeType Type, NodeType From, NodeType To, string Description);

/// <summary>Expliciete ontologie van de kennisgraaf (#377): welke knooptypen
/// bestaan, welke relaties tussen welke typen geldig zijn, en welke feiten
/// afgeleid worden. Eén bron van waarheid — de sync valideert hiertegen en
/// de brein-API leest hem, in plaats van dat de regels impliciet verspreid
/// zitten over Cypher-strings en C#-code.</summary>
public static class GraphOntology
{
    public static IReadOnlyList<EdgeSpec> Edges { get; } =
    [
        new(EdgeType.FROM_SET, NodeType.Card, NodeType.Set,
            "Kaart is gepubliceerd in deze set"),
        new(EdgeType.HAS_DOMAIN, NodeType.Card, NodeType.Domain,
            "Kaart hoort tot dit domein"),
        new(EdgeType.HAS_TAG, NodeType.Card, NodeType.Tag,
            "Kaart draagt deze factie/tribe-tag (géén spelmechaniek)"),
        new(EdgeType.HAS_MECHANIC, NodeType.Card, NodeType.Mechanic,
            "Kaart gebruikt deze geminede spelmechaniek"),
        new(EdgeType.INTERACTS_WITH, NodeType.Card, NodeType.Card,
            "Geverifieerde interactie tussen twee kaarten"),
        new(EdgeType.PARENT_OF, NodeType.RuleSection, NodeType.RuleSection,
            "Regelsectie is de bovenliggende regel van deze subsectie"),
        new(EdgeType.DEFINES, NodeType.RuleSection, NodeType.Mechanic,
            "Regelsectie definieert of beschrijft deze mechaniek"),
        new(EdgeType.GOVERNED_BY, NodeType.Card, NodeType.RuleSection,
            "Afgeleid: kaart valt onder deze regelsectie via haar mechanieken"),
        new(EdgeType.EXPLAINS, NodeType.Concept, NodeType.RuleSection,
            "Primer-concept legt deze regelsectie in gewone taal uit"),
        new(EdgeType.AMENDS, NodeType.Erratum, NodeType.Card,
            "Erratum wijzigt de tekst van deze kaart"),
        new(EdgeType.BANS, NodeType.BanEntry, NodeType.Card,
            "Banlijst-item verbiedt deze kaart (geldt voor de variantgroep)"),
    ];

    /// <summary>De graaf werkt op canonieke kaart-identiteiten: alt-art- en
    /// promo-printings zijn dezelfde kaart in het spel en krijgen géén eigen
    /// knoop (#57). Dat is meteen onze entity-resolution-regel — koppelen aan
    /// een externe graaf gebeurt straks op deze canonieke sleutel.</summary>
    public const string IdentityRule = "canonieke printing per kaartnaam";

    /// <summary>Sleutel-eigenschap per knooptype (uniek, gebruikt in MERGE).</summary>
    public static string KeyProperty(NodeType type) => type switch
    {
        NodeType.Domain or NodeType.Tag or NodeType.Mechanic => "name",
        NodeType.RuleSection => "code",
        _ => "id",
    };

    /// <summary>Is deze relatie toegestaan volgens de ontologie? De sync
    /// gebruikt dit zodat een edge die hier niet staat er nooit in komt.</summary>
    public static bool IsValid(EdgeType type, NodeType from, NodeType to) =>
        Edges.Any(e => e.Type == type && e.From == from && e.To == to);

    /// <summary>Afgeleide relaties: niet ingelezen maar berekend uit andere
    /// feiten. Documenteert de inferentieregels op één plek.</summary>
    public static IReadOnlyList<EdgeType> Inferred { get; } = [EdgeType.GOVERNED_BY];

    public static bool IsInferred(EdgeType type) => Inferred.Contains(type);
}
