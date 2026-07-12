namespace RbRules.Domain;

public record GraphDriftEntry(string Label, int Postgres, int Graph, int Delta);

/// <summary>Drift-vergelijking Postgres ↔ Neo4j (#108, docs/BRAIN.md §4):
/// Postgres muteert continu, de graph alleen per sync — dit meet per
/// knooptype hoe ver de projectie achterloopt in plaats van dat te raden.
/// Pure logica: de aanroeper (KnowledgeGapsService) levert beide tellingen,
/// dit vergelijkt en ordent alleen.</summary>
public static class GraphDrift
{
    /// <summary>Alle knooptypes die GraphSyncService projecteert, in de
    /// presentatievolgorde van de sync zelf (kaart-facetten eerst, dan de
    /// kennislagen). Nieuwe labels in de sync horen ook hier — de vergelijking
    /// toont onbekende graph-labels sowieso (vangnet), maar zonder
    /// Postgres-verwachting is dat een tel zonder referentie.</summary>
    public static readonly string[] Labels =
    [
        "Card", "Set", "Domain", "Tag", "Mechanic",
        "RuleSection", "Concept", "Claim", "Source", "Erratum", "Change",
    ];

    /// <summary>Vergelijkt de verwachte aantallen (Postgres, dezelfde
    /// predicaten als de sync-projectie) met de werkelijke graph-tellingen.
    /// Elk bekend label krijgt een rij, óók als beide kanten 0 zijn — "leeg
    /// aan beide kanten" is een geldige, zichtbare meting. Labels die alleen
    /// in de graph bestaan (bv. na een schema-wijziging of handmatige
    /// Cypher) komen achteraan: onverwachte knopen zijn óók drift.</summary>
    public static IReadOnlyList<GraphDriftEntry> Compare(
        IReadOnlyDictionary<string, int> postgres, IReadOnlyDictionary<string, int> graph)
    {
        var entries = Labels
            .Select(label => Entry(label,
                postgres.GetValueOrDefault(label), graph.GetValueOrDefault(label)))
            .ToList();

        var known = Labels.ToHashSet(StringComparer.Ordinal);
        entries.AddRange(graph
            .Where(kv => !known.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Entry(kv.Key, postgres.GetValueOrDefault(kv.Key), kv.Value)));
        return entries;
    }

    private static GraphDriftEntry Entry(string label, int postgres, int graph) =>
        new(label, postgres, graph, graph - postgres);
}
