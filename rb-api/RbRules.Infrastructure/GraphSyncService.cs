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
        // Alleen canonieke printings (#57): alt-arts zijn dezelfde kaart in
        // het spel en horen niet als losse knopen in de graph. Projectie
        // zonder embedding-vectoren (#43).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, Type = c.Type,
                Rarity = c.Rarity, Domains = c.Domains, Tags = c.Tags,
                Mechanics = c.Mechanics, Energy = c.Energy, Might = c.Might,
                SetId = c.SetId, SetLabel = c.SetLabel,
            })
            .ToListAsync(ct);

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

        // Eén transactie rond de hele rebuild (conventie): opruimen en de
        // nieuwe stand schrijven slagen of falen samen — een fout halverwege
        // mag geen half leeggeruimde graph achterlaten. Dispose zonder commit
        // = rollback.
        await using var tx = await session.BeginTransactionAsync();

        // Knopen die geen canonieke kaart (meer) zijn opruimen (#57): de graph
        // is vóór de variantgroepering gevuld, en ook een latere canonical-
        // wissel zou het oude id als wees achterlaten.
        await tx.RunAsync(
            "MATCH (c:Card) WHERE NOT c.id IN $ids DETACH DELETE c",
            new Dictionary<string, object>
            {
                ["ids"] = cards.Select(c => (object)c.RiftboundId).ToList(),
            });

        await tx.RunAsync(
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

        await RunPairsAsync(tx,
            "MERGE (d:Domain {name: p.value}) MERGE (c)-[:HAS_DOMAIN]->(d)", domainPairs);
        await RunPairsAsync(tx,
            "MERGE (t:Tag {name: p.value}) MERGE (c)-[:HAS_TAG]->(t)", tagPairs);
        await RunPairsAsync(tx,
            "MERGE (m:Mechanic {name: p.value}) MERGE (c)-[:HAS_MECHANIC]->(m)", mechanicPairs);

        // Facet-knopen die na de opruiming nergens meer aan hangen (bijv. een
        // promo-set die alleen variant-printings bevatte) verdwijnen mee.
        await tx.RunAsync(
            """
            MATCH (n) WHERE (n:Set OR n:Domain OR n:Tag OR n:Mechanic)
              AND NOT (n)--() DELETE n
            """);

        await tx.CommitAsync();

        return new(
            cardRows.Count,
            CountDistinct(domainPairs),
            CountDistinct(tagPairs),
            CountDistinct(mechanicPairs));
    }

    private static async Task RunPairsAsync(
        IAsyncQueryRunner runner, string mergeClause, List<object> pairs)
    {
        await runner.RunAsync(
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
