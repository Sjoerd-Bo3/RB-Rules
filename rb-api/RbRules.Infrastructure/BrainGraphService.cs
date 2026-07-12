using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BrainNeighbor(
    string Ref, string? Name, string Edge, string Richting,
    IReadOnlyDictionary<string, object>? Props);

/// <summary>Wrapper {neighbors: [...]} — de rb-ai-tools accepteren een platte
/// array óf deze wrapper (zelfde afspraak als search/path).</summary>
public record BrainNeighborsResponse(string Ref, IReadOnlyList<BrainNeighbor> Neighbors);

public record BrainPathNode(string Ref, string? Name);

/// <summary>Kortste pad als keten [knoop, edge-type, knoop, …] (§2.3 — "de
/// bewijsketen"): knopen zijn {ref, name}-objecten, edges zijn strings. Leeg
/// pad = beide knopen bestaan maar zijn binnen maxLen niet verbonden.</summary>
public record BrainPathResponse(string From, string To, IReadOnlyList<object> Path);

/// <summary>Graph-kant van de brein-API (#105, docs/BRAIN.md §2.3): buren en
/// kortste paden via de bestaande IDriver. Alle Cypher is geparametriseerd
/// mét LIMIT; het enige dat ooit in de query-tekst wordt geïnterpoleerd zijn
/// het node-label uit BrainQuery.GraphLabel (vaste enum-mapping) en de
/// geclampte maxLen-int — nooit een gebruikersstring. Neo4j-uitval is een
/// verwacht pad: de exceptions bubbelen naar het endpoint, dat er een nette
/// Problem-response ("graph niet beschikbaar") van maakt terwijl de vier
/// Postgres-koppelvlakken blijven werken.</summary>
public class BrainGraphService(IDriver driver)
{
    /// <summary>Weergavenaam over alle knooptypes heen: elke soort heeft een
    /// eigen "naam"-property (Card.name, Concept.title, RuleSection.code, …).</summary>
    private const string NameCoalesce =
        "coalesce(m.name, m.title, m.code, m.cardName, m.statement, m.label, m.changeType, toString(m.id))";

    /// <summary>Knopen per label, voor de drift-meting in het
    /// kennis-gaten-rapport (#108, docs/BRAIN.md §4). Eén query over de hele
    /// graph — de aantallen zijn klein (§2.2). Neo4j-uitval bubbelt als
    /// exception naar de aanroeper, die er "graph niet beschikbaar" van
    /// maakt (zelfde afspraak als neighbors/path).</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountsByLabelAsync(
        CancellationToken ct = default)
    {
        await using var session = driver.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n) UNWIND labels(n) AS label RETURN label, count(*) AS count");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in await cursor.ToListAsync(ct))
            counts[record["label"].As<string>()] = record["count"].As<int>();
        return counts;
    }

    /// <summary>Buren van één knoop, optioneel gefilterd op edge-types
    /// (whitelist, al gevalideerd) en richting. Null = knoop niet in de graph
    /// (404 bij het endpoint — de graph-job is dan mogelijk nog niet gedraaid).</summary>
    public async Task<IReadOnlyList<BrainNeighbor>?> NeighborsAsync(
        string label, string refValue, string[] edgeFilter, BrainDirection direction,
        int take, CancellationToken ct = default)
    {
        // Richting als vast patroon-drietal — geen gebruikerstekst.
        var pattern = direction switch
        {
            BrainDirection.Uit => "(n)-[r]->(m)",
            BrainDirection.In => "(n)<-[r]-(m)",
            _ => "(n)-[r]-(m)",
        };

        // WHERE hoort bij de OPTIONAL MATCH: matcht niets, dan blijft er één
        // rij met r = null over — zo blijft "knoop bestaat, geen (passende)
        // buren" onderscheidbaar van "knoop bestaat niet" (nul rijen).
        var cypher = $$"""
            MATCH (n:{{label}} {ref: $ref})
            OPTIONAL MATCH {{pattern}}
            WHERE size($edges) = 0 OR type(r) IN $edges
            RETURN m.ref AS ref,
                   {{NameCoalesce}} AS name,
                   type(r) AS edge,
                   CASE WHEN r IS NULL THEN null
                        WHEN elementId(startNode(r)) = elementId(n) THEN 'uit'
                        ELSE 'in' END AS richting,
                   properties(r) AS props
            LIMIT $take
            """;

        await using var session = driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, new Dictionary<string, object>
        {
            ["ref"] = refValue,
            ["edges"] = edgeFilter.Cast<object>().ToList(),
            ["take"] = (long)take,
        });
        var records = await cursor.ToListAsync(ct);
        if (records.Count == 0) return null;

        var neighbors = new List<BrainNeighbor>();
        foreach (var record in records)
        {
            if (record["edge"].As<string?>() is not { } edge) continue; // de lege r=null-rij
            var props = record["props"].As<Dictionary<string, object>?>();
            neighbors.Add(new(
                record["ref"].As<string?>() ?? "?",
                record["name"].As<string?>(),
                edge,
                record["richting"].As<string?>() ?? "uit",
                props is { Count: > 0 } ? props : null));
        }
        return neighbors;
    }

    public enum PathOutcome { Found, NoPath, FromMissing, ToMissing }

    /// <summary>Kortste pad tussen twee refs. maxLen is vooraf geclampt
    /// (1..6) — variabele padlengtes zijn in Cypher niet parametriseerbaar,
    /// dus dit is de ene bewuste int-interpolatie.</summary>
    public async Task<(PathOutcome Outcome, IReadOnlyList<object> Chain)> PathAsync(
        string fromLabel, string fromRef, string toLabel, string toRef,
        int maxLen, CancellationToken ct = default)
    {
        await using var session = driver.AsyncSession();

        // Bestaan-check eerst: "geen pad" en "knoop ontbreekt" zijn voor de
        // agent wezenlijk verschillende antwoorden.
        var checkCursor = await session.RunAsync(
            $$"""
            OPTIONAL MATCH (a:{{fromLabel}} {ref: $from})
            OPTIONAL MATCH (b:{{toLabel}} {ref: $to})
            RETURN a IS NOT NULL AS fromFound, b IS NOT NULL AS toFound
            """,
            new Dictionary<string, object> { ["from"] = fromRef, ["to"] = toRef });
        var check = await checkCursor.SingleAsync(ct);
        if (!check["fromFound"].As<bool>()) return (PathOutcome.FromMissing, []);
        if (!check["toFound"].As<bool>()) return (PathOutcome.ToMissing, []);

        var cypher = $$"""
            MATCH (a:{{fromLabel}} {ref: $from})
            MATCH (b:{{toLabel}} {ref: $to})
            MATCH p = shortestPath((a)-[*..{{maxLen}}]-(b))
            RETURN [x IN nodes(p) | [x.ref, {{NameCoalesce.Replace("m.", "x.")}}]] AS nodes,
                   [r IN relationships(p) | type(r)] AS edges
            LIMIT 1
            """;
        var cursor = await session.RunAsync(cypher, new Dictionary<string, object>
        {
            ["from"] = fromRef,
            ["to"] = toRef,
        });
        var records = await cursor.ToListAsync(ct);
        if (records.Count == 0) return (PathOutcome.NoPath, []);

        var nodes = records[0]["nodes"].As<List<object>>()
            .Select(n =>
            {
                var pair = (n as IList<object>) ?? [];
                return new BrainPathNode(
                    pair.ElementAtOrDefault(0)?.ToString() ?? "?",
                    pair.ElementAtOrDefault(1)?.ToString());
            })
            .ToList();
        var edges = records[0]["edges"].As<List<object>>()
            .Select(e => e.ToString() ?? "?")
            .ToList();

        // Keten [knoop, edge, knoop, …] — het contract uit §2.3.
        var chain = new List<object>();
        for (var i = 0; i < nodes.Count; i++)
        {
            chain.Add(nodes[i]);
            if (i < edges.Count) chain.Add(edges[i]);
        }
        return (PathOutcome.Found, chain);
    }
}
