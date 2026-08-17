namespace RbRules.Domain.Reasoning;

/// <summary>De provenance-envelop op elke door de reasoner gematerialiseerde
/// (afgeleide) edge — fase 3 (#227, redeneer-laag §5). Elke afgeleide edge draagt
/// verplicht: <c>derived=true</c> (het onderscheid met een basisfeit),
/// <c>derivedByRule</c> (wélke inferentie-regel de edge maakte — inzicht #236: geen
/// onzichtbare state), plus de run-herkomst (<c>runId</c>, <c>model='deterministic'</c>,
/// <c>derivedAt</c>). Eén bron voor de SET-clause én de param-dictionary, zodat de
/// regel-Cypher en de service nooit uiteenlopen en tests de tagging kunnen
/// afdwingen. Afgeleide edges zijn NOOIT bron van waarheid — ze zijn volledig
/// herberekenbaar en worden bij een full-rebuild opnieuw gematerialiseerd, nooit
/// als Postgres-feit gepersisteerd (SoT = de basisfeiten).</summary>
public static class DerivedEdgeProvenance
{
    /// <summary>Vlag die een afgeleide edge onderscheidt van een basisfeit.</summary>
    public const string DerivedProp = "derived";
    /// <summary>Id van de inferentie-regel die de edge materialiseerde.</summary>
    public const string RuleProp = "derivedByRule";
    /// <summary>ULID van de <see cref="MiningRun"/> die de reasoner-run vastlegde.</summary>
    public const string RunProp = "runId";
    /// <summary>Herkomst-stempel — reasoner-regels zijn puur deterministisch.</summary>
    public const string ModelProp = "model";
    /// <summary>ISO-8601-tijdstip van materialisatie.</summary>
    public const string AtProp = "derivedAt";

    /// <summary>Waarde van <see cref="ModelProp"/>: een reasoner-regel draagt nooit
    /// een LLM-oordeel — de materialisatie is monotone, deterministische inferentie.</summary>
    public const string DeterministicModel = "deterministic";

    /// <summary>De Cypher-<c>SET</c>-clause die <paramref name="edgeVar"/> als afgeleide
    /// edge tagt met regel-id + run-provenance. <paramref name="ruleId"/> wordt letterlijk
    /// geïnterpoleerd; het is altijd een door de registry gecontroleerd, veilig id
    /// (<see cref="InferenceRuleRegistry.IsSafeRuleId"/>) — nooit gebruikersinvoer.
    /// De run-props komen als parameters (<see cref="Params"/>), niet geïnterpoleerd.</summary>
    public static string SetClause(string edgeVar, string ruleId) =>
        $"SET {edgeVar}.{DerivedProp} = true, " +
        $"{edgeVar}.{RuleProp} = '{ruleId}', " +
        $"{edgeVar}.{RunProp} = $runId, " +
        $"{edgeVar}.{ModelProp} = '{DeterministicModel}', " +
        $"{edgeVar}.{AtProp} = $now";

    /// <summary>De run-provenance-parameters die élke reasoner-Cypher meekrijgt
    /// (dictionaries-only, zoals de rest van de graaf-writes). <c>$now</c> is een
    /// ISO-8601-string — Neo4j-temporals hoeven zo niet geraden te worden.</summary>
    public static Dictionary<string, object?> Params(string runId, DateTimeOffset now) => new()
    {
        [RunProp] = runId,
        ["now"] = now.UtcDateTime.ToString("o"),
    };
}
