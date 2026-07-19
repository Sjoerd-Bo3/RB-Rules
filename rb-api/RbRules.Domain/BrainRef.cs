namespace RbRules.Domain;

/// <summary>Knoopsoorten van het brein (docs/BRAIN.md §2.1). Elke soort heeft
/// één tekstueel prefix dat in pgvector-land, Neo4j-land én API-contracten
/// identiek is.</summary>
public enum BrainRefKind
{
    Card,
    Mechanic,
    Concept,
    Section,
    Claim,
    Source,
    Erratum,
    Change,
    Set,
    Domain,
    Tag,
    Ruling,
    /// <summary>Dynamische relatie (#116-entiteit) als onderwerp van een
    /// beheerder-ruling (#124). Bewust géén graph-knoop (relaties zijn edges);
    /// GraphLabel geeft er dan ook null voor terug.</summary>
    Relation,
    /// <summary>Provenance-tak (fase 0a, #233): PROV-O-Activity die feiten
    /// afleidde. Wél een graph-knoop (:MiningRun).</summary>
    MiningRun,
    /// <summary>Provenance-tak (fase 0a, #233): gereïficeerd feit-met-herkomst.
    /// Wél een graph-knoop (:Assertion), doel van WAS_GENERATED_BY/DERIVED_FROM.</summary>
    Assertion,
    /// <summary>Reïficatie-tak (fase 2, #226): gereïficeerde, gekwalificeerde
    /// n-aire relatie (:Interaction) — de canonieke opslagvorm van elk
    /// COUNTERS/MODIFIES/GRANTS/REQUIRES-feit. Subject van de bijbehorende
    /// <see cref="BrainRefKind.Assertion"/> en van gekwalificeerde
    /// <see cref="BrainRefKind.Condition"/>-knopen.</summary>
    Interaction,
    /// <summary>Reïficatie-tak (fase 2, #226): een gereïficeerde voorwaarde op
    /// een <see cref="BrainRefKind.Interaction"/> (window/status/cost). Wél een
    /// graph-knoop (:Condition).</summary>
    Condition,
}

/// <summary>Eén canonieke, tekstuele referentie voor alles wat het brein kent
/// (docs/BRAIN.md §2.1): "card:ogn-011-298", "section:core-rules-pdf/101.2",
/// "claim:17". Puur parse/format — de ref is een afspraak, geen opslag; de
/// bestaande tabellen zíjn de knopen.</summary>
public readonly record struct BrainRef(BrainRefKind Kind, string Key)
{
    public static BrainRef Card(string riftboundId) => new(BrainRefKind.Card, riftboundId);
    public static BrainRef Mechanic(string name) => new(BrainRefKind.Mechanic, name);
    public static BrainRef Concept(string topicKey) => new(BrainRefKind.Concept, topicKey);

    /// <summary>Sectie-refs dragen bron én code: een §-code is alleen binnen
    /// één document uniek ("101" of "intro" bestaat in de Core Rules én de
    /// Tournament Rules).</summary>
    public static BrainRef Section(string sourceId, string code) =>
        new(BrainRefKind.Section, $"{sourceId}/{code}");

    public static BrainRef Claim(long id) => new(BrainRefKind.Claim, id.ToString());
    public static BrainRef Source(string sourceId) => new(BrainRefKind.Source, sourceId);
    public static BrainRef Erratum(long id) => new(BrainRefKind.Erratum, id.ToString());
    public static BrainRef Change(long id) => new(BrainRefKind.Change, id.ToString());
    public static BrainRef Set(string setId) => new(BrainRefKind.Set, setId);
    public static BrainRef Domain(string name) => new(BrainRefKind.Domain, name);
    public static BrainRef Tag(string name) => new(BrainRefKind.Tag, name);
    public static BrainRef Ruling(long correctionId) => new(BrainRefKind.Ruling, correctionId.ToString());
    public static BrainRef Relation(long id) => new(BrainRefKind.Relation, id.ToString());
    public static BrainRef MiningRun(string ulid) => new(BrainRefKind.MiningRun, ulid);
    public static BrainRef Assertion(string ulid) => new(BrainRefKind.Assertion, ulid);
    public static BrainRef Interaction(long id) => new(BrainRefKind.Interaction, id.ToString());
    public static BrainRef Condition(long id) => new(BrainRefKind.Condition, id.ToString());

    public string Format() => $"{Prefix(Kind)}:{Key}";

    public override string ToString() => Format();

    /// <summary>Parse van "kind:key". Alleen de eerste dubbele punt scheidt —
    /// de key mag er zelf één bevatten. Onbekend prefix of lege/rafelige key
    /// (witruimte aan de randen) is ongeldig: refs zijn exact of niets.</summary>
    public static bool TryParse(string? text, out BrainRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var split = trimmed.IndexOf(':');
        if (split <= 0 || split == trimmed.Length - 1) return false;

        if (KindFromPrefix(trimmed[..split]) is not { } kind) return false;
        var key = trimmed[(split + 1)..];
        if (key != key.Trim()) return false;

        result = new BrainRef(kind, key);
        return true;
    }

    private static string Prefix(BrainRefKind kind) => kind switch
    {
        BrainRefKind.Card => "card",
        BrainRefKind.Mechanic => "mechanic",
        BrainRefKind.Concept => "concept",
        BrainRefKind.Section => "section",
        BrainRefKind.Claim => "claim",
        BrainRefKind.Source => "source",
        BrainRefKind.Erratum => "erratum",
        BrainRefKind.Change => "change",
        BrainRefKind.Set => "set",
        BrainRefKind.Domain => "domain",
        BrainRefKind.Tag => "tag",
        BrainRefKind.Ruling => "ruling",
        BrainRefKind.Relation => "relation",
        BrainRefKind.MiningRun => "run",
        BrainRefKind.Assertion => "assertion",
        BrainRefKind.Interaction => "interaction",
        BrainRefKind.Condition => "condition",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "onbekende BrainRefKind"),
    };

    private static BrainRefKind? KindFromPrefix(string prefix) => prefix switch
    {
        "card" => BrainRefKind.Card,
        "mechanic" => BrainRefKind.Mechanic,
        "concept" => BrainRefKind.Concept,
        "section" => BrainRefKind.Section,
        "claim" => BrainRefKind.Claim,
        "source" => BrainRefKind.Source,
        "erratum" => BrainRefKind.Erratum,
        "change" => BrainRefKind.Change,
        "set" => BrainRefKind.Set,
        "domain" => BrainRefKind.Domain,
        "tag" => BrainRefKind.Tag,
        "ruling" => BrainRefKind.Ruling,
        "relation" => BrainRefKind.Relation,
        "run" => BrainRefKind.MiningRun,
        "assertion" => BrainRefKind.Assertion,
        "interaction" => BrainRefKind.Interaction,
        "condition" => BrainRefKind.Condition,
        _ => null,
    };
}
