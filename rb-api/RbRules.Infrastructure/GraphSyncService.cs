using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphSyncResult(int Cards, int Domains, int Tags, int Mechanics);

/// <summary>Neo4j-sync met batched UNWIND (audit-fix: de PoP deed ~4 queries
/// per kaart; dit zijn er 4 totaal). Tag ≠ Mechanic: facties/tribes worden
/// (:Tag), geminede spelmechanieken (:Mechanic). Parameters als dictionaries —
/// de driver serialiseert geen anonymous types in collecties.</summary>
public class GraphSyncService(RbRulesDbContext db, IDriver driver)
{
    public async Task<GraphSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var cards = await db.Cards.AsNoTracking().ToListAsync(ct);

        var cardRows = cards.Select(c => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["name"] = c.Name,
            ["type"] = c.Type,
            ["rarity"] = c.Rarity,
            ["energy"] = c.Energy,
            ["might"] = c.Might,
            ["set"] = c.SetId,
            ["setLabel"] = c.SetLabel,
        }).ToList();

        var domainPairs = Pairs(cards, c => c.Domains);
        var tagPairs = Pairs(cards, c => c.Tags);
        var mechanicPairs = Pairs(cards, c => c.Mechanics ?? []);

        await using var session = driver.AsyncSession();

        await session.RunAsync(
            """
            UNWIND $rows AS row
            MERGE (c:Card {id: row.id})
              SET c.name = row.name, c.type = row.type, c.rarity = row.rarity,
                  c.energy = row.energy, c.might = row.might
            WITH c, row WHERE row.set IS NOT NULL
            MERGE (s:Set {id: row.set}) ON CREATE SET s.label = row.setLabel
            MERGE (c)-[:FROM_SET]->(s)
            """,
            new Dictionary<string, object> { ["rows"] = cardRows });

        await RunPairsAsync(session,
            "MERGE (d:Domain {name: p.value}) MERGE (c)-[:HAS_DOMAIN]->(d)", domainPairs);
        await RunPairsAsync(session,
            "MERGE (t:Tag {name: p.value}) MERGE (c)-[:HAS_TAG]->(t)", tagPairs);
        await RunPairsAsync(session,
            "MERGE (m:Mechanic {name: p.value}) MERGE (c)-[:HAS_MECHANIC]->(m)", mechanicPairs);

        return new(
            cardRows.Count,
            CountDistinct(domainPairs),
            CountDistinct(tagPairs),
            CountDistinct(mechanicPairs));
    }

    private static async Task RunPairsAsync(
        IAsyncSession session, string mergeClause, List<object> pairs)
    {
        await session.RunAsync(
            $"UNWIND $pairs AS p MATCH (c:Card {{id: p.id}}) {mergeClause}",
            new Dictionary<string, object> { ["pairs"] = pairs });
    }

    private static List<object> Pairs(IEnumerable<Card> cards, Func<Card, string[]> selector) =>
        [.. cards.SelectMany(c => selector(c).Select(v => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["value"] = v,
        }))];

    private static int CountDistinct(List<object> pairs) =>
        pairs.Cast<Dictionary<string, object?>>()
            .Select(d => (string?)d["value"])
            .Distinct()
            .Count();
}
