using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BreinProjectionResult(
    int CanonicalEntities, int MergedInto, int Predicates, int HasPredicate,
    int OntologyVersions, int Precedes, bool GraphAvailable)
{
    public string Summary => GraphAvailable
        ? $"{CanonicalEntities} entiteiten ({MergedInto} merges) · {Predicates} predicaten · " +
          $"{OntologyVersions} ontologie-versies"
        : "graph niet beschikbaar — brein-projectie overgeslagen (herbouwbaar uit Postgres)";
}

/// <summary>Fase live-graph (#227, §3.5) — de brein-projectie: projecteert de
/// Postgres-brein-tabellen die <see cref="GraphSyncService"/> NOG NIET dekt
/// idempotent naar Neo4j (<c>:CanonicalEntity</c>, <c>:MechanicPredicate</c>,
/// <c>:OntologyVersion</c>). ADDITIEF en geïsoleerd: een aparte service + eigen
/// transactie + eigen job (<c>breinprojectie</c>) die de bestaande
/// GraphSyncService-transactie/-jobs NIET aanraakt (minimaliseer risico). De
/// rij-/param-/sleutel-opbouw is puur en getest (<see cref="BrainProjection"/>);
/// deze service is de dunne IO-schil eromheen (zelfde arbeidsdeling als
/// <see cref="InteractionProjection"/> ↔ GraphSyncService).
///
/// Invarianten. (a) Postgres = SoT; Neo4j is idempotent herbouwbaar (MERGE op de
/// canonieke <c>ref</c>). (b) Wees-opruiming per OWNED label (nooit de labels die
/// GraphSyncService beheert) zodat de projectie een exacte spiegel van Postgres
/// blijft. (c) Nette degradatie: Neo4j-uitval is een verwacht pad — de run doet
/// niets en meldt "graph niet beschikbaar" (afgeleide state is herberekenbaar),
/// nooit een crash. Live-Cypher-executie is verifieerbaar zodra de job draait
/// (integratie-follow-up, docs/ARCHITECTURE §6.5).</summary>
public class BreinProjectionService(RbRulesDbContext db, IDriver driver)
{
    public async Task<BreinProjectionResult> ProjectAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Invoke("brein-tabellen laden (entiteiten, predicaten, ontologie-versies)");

        // ALLE canonieke entiteiten (ook merged tombstones — herstelpad-historie).
        var entities = await db.CanonicalEntities.AsNoTracking().ToListAsync(ct);
        var predicates = await db.MechanicPredicates.AsNoTracking().ToListAsync(ct);
        var ontologyVersions = await db.OntologyVersions.AsNoTracking().ToListAsync(ct);

        var rows = BrainProjection.Build(entities, predicates, ontologyVersions);

        var graphAvailable = true;
        try
        {
            await using var session = driver.AsyncSession();
            await using var tx = await session.BeginTransactionAsync();

            // Wees-opruiming per OWNED label (alleen brein-eigen labels — nooit de
            // GraphSyncService-labels): knopen die niet meer in Postgres bestaan
            // verdwijnen, zodat de projectie een exacte spiegel blijft.
            await DeleteStaleAsync(tx, "CanonicalEntity", rows.CanonicalEntities);
            await DeleteStaleAsync(tx, "MechanicPredicate", rows.Predicates);
            await DeleteStaleAsync(tx, "OntologyVersion", rows.OntologyVersions);

            progress?.Invoke($"{rows.CanonicalEntities.Count} :CanonicalEntity MERGE'n");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MERGE (e:CanonicalEntity {ref: row.ref})
                  SET e.kind = row.kind, e.canonicalLabel = row.canonicalLabel,
                      e.brainRef = row.brainRef, e.altLabels = row.altLabels,
                      e.status = row.status, e.definition = row.definition,
                      e.createdByRun = row.createdByRun
                """,
                Param(rows.CanonicalEntities));

            // MERGED_INTO tussen twee :CanonicalEntity-knopen; oude edges eerst weg
            // (een teruggedraaide merge mag geen fossiel achterlaten).
            await tx.RunAsync("MATCH (:CanonicalEntity)-[r:MERGED_INTO]->() DELETE r");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MATCH (a:CanonicalEntity {ref: row.from})
                MATCH (b:CanonicalEntity {ref: row.to})
                MERGE (a)-[:MERGED_INTO]->(b)
                """,
                Param(rows.MergedIntoEdges));

            progress?.Invoke($"{rows.Predicates.Count} :MechanicPredicate MERGE'n");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MERGE (p:MechanicPredicate {ref: row.ref})
                  SET p.predicate = row.predicate, p.objectToken = row.objectToken,
                      p.status = row.status, p.createdByRun = row.createdByRun
                """,
                Param(rows.Predicates));

            // HAS_PREDICATE: subject-entiteit → predicaat. Oude edges eerst weg
            // (een ingetrokken/gewisseld predicaat mag geen wees-edge houden).
            await tx.RunAsync("MATCH (:CanonicalEntity)-[r:HAS_PREDICATE]->(:MechanicPredicate) DELETE r");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MATCH (e:CanonicalEntity {ref: row.entity})
                MATCH (p:MechanicPredicate {ref: row.predicate})
                MERGE (e)-[:HAS_PREDICATE]->(p)
                """,
                Param(rows.HasPredicateEdges));

            progress?.Invoke($"{rows.OntologyVersions.Count} :OntologyVersion MERGE'n");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MERGE (v:OntologyVersion {ref: row.ref})
                  SET v.version = row.version, v.fingerprint = row.fingerprint,
                      v.bumpKind = row.bumpKind, v.notes = row.notes,
                      v.current = row.current, v.appliedAt = row.appliedAt,
                      v.createdByRun = row.createdByRun
                """,
                Param(rows.OntologyVersions));

            await tx.RunAsync("MATCH (:OntologyVersion)-[r:PRECEDES]->() DELETE r");
            await tx.RunAsync(
                """
                UNWIND $rows AS row
                MATCH (a:OntologyVersion {ref: row.from})
                MATCH (b:OntologyVersion {ref: row.to})
                MERGE (a)-[:PRECEDES]->(b)
                """,
                Param(rows.PrecedesEdges));

            await tx.CommitAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Neo4j-uitval (driver-/connectie-fout) is een verwacht pad: de projectie
            // is best-effort (zelfde patroon als GraphSyncService/ReasoningService).
            // Postgres blijft leidend; de projectie is bij de volgende run herbouwbaar.
            graphAvailable = false;
        }

        return new BreinProjectionResult(
            rows.CanonicalEntities.Count,
            rows.MergedIntoEdges.Count,
            rows.Predicates.Count,
            rows.HasPredicateEdges.Count,
            rows.OntologyVersions.Count,
            rows.PrecedesEdges.Count,
            graphAvailable);
    }

    // Wees-opruiming: DETACH DELETE de owned-label-knopen waarvan de ref niet in de
    // huidige Postgres-stand voorkomt. `refs` als lijst-param (dictionaries-only-lijn).
    private static async Task DeleteStaleAsync(
        IAsyncQueryRunner runner, string label, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var refs = rows.Select(r => r["ref"]).Where(r => r is not null).Cast<object>().ToList();
        await runner.RunAsync(
            $"MATCH (n:{label}) WHERE NOT n.ref IN $refs DETACH DELETE n",
            new Dictionary<string, object> { ["refs"] = refs });
    }

    private static Dictionary<string, object> Param(IReadOnlyList<Dictionary<string, object?>> rows) =>
        new() { ["rows"] = rows.Cast<object>().ToList() };
}
