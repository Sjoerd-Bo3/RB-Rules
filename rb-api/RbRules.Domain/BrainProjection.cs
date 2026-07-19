using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Pure projectie-bouwstenen (fase live-graph, #227) tussen de
/// Postgres-brein-tabellen (bron van waarheid) en hun idempotente Neo4j-projectie.
/// IO-loos en volledig testbaar — <see cref="RbRules.Infrastructure"/>'s
/// <c>BreinProjectionService</c> hangt de batched-UNWIND/dictionaries-only-writes
/// eromheen, exact zoals <see cref="InteractionProjection"/> dat voor de
/// reïficatie-tak doet.
///
/// Deze bouwer dekt de brein-lagen die de bestaande <c>GraphSyncService</c> NOG NIET
/// projecteert (fase 1 <see cref="CanonicalEntity"/>, fase 5
/// <see cref="MechanicPredicateAssertion"/>, fase 6 <see cref="OntologyVersionRecord"/>);
/// <c>MiningRun</c>/<c>Assertion</c>/<c>Interaction</c>/<c>Condition</c> blijven bij
/// GraphSyncService (additief, geen overlap — kritiek: raak die transactie niet aan).
///
/// KRITIEK — ref-namespace-scheiding. De owned-node-refs dragen een EIGEN prefix
/// (<c>entity:</c>/<c>predicate:</c>/<c>ontologyversion:</c>) die NIET in het
/// <see cref="BrainRef"/>-alfabet zit. Dat is bewust: GraphSyncService matcht
/// DERIVED_FROM/RELATES_TO label-LOOS op <c>ref</c>; zou een brein-node de
/// <c>mechanic:</c>-ref van een bestaande <c>:Mechanic</c>-knoop delen, dan werd zo'n
/// label-loze match ambigu en zou hij dubbele edges maken. De eigen prefix sluit dat
/// uit, en de projectie linkt daarom NIET naar GraphSyncService-eigen knopen
/// (Card/Mechanic/MiningRun/…) — provenance rijdt als <c>createdByRun</c>-property mee,
/// niet als edge naar een knoop die een latere graph-rebuild weer weggooit.</summary>
public static class BrainProjection
{
    /// <summary>Ref-prefix voor <see cref="CanonicalEntity"/>-knopen. Bewust NIET
    /// <c>mechanic:</c>/<c>concept:</c> (die <see cref="CanonicalEntity.Ref"/>
    /// oplevert) — zie de klasse-uitleg over ref-namespace-scheiding.</summary>
    public static string EntityRef(long id) => $"entity:{id}";

    /// <summary>Ref-prefix voor <see cref="MechanicPredicateAssertion"/>-knopen.</summary>
    public static string PredicateRef(long id) => $"predicate:{id}";

    /// <summary>Ref-prefix voor <see cref="OntologyVersionRecord"/>-knopen.</summary>
    public static string OntologyVersionRef(long id) => $"ontologyversion:{id}";

    /// <summary>Bouwt alle param-rijen voor de brein-projectie, dictionaries-only
    /// (de driver serialiseert geen anonymous types in collecties). Deterministisch:
    /// dezelfde invoer levert altijd dezelfde rijen — de projectie is een spiegel van
    /// Postgres, nooit andersom.</summary>
    public static BrainProjectionRows Build(
        IEnumerable<CanonicalEntity> entities,
        IEnumerable<MechanicPredicateAssertion> predicates,
        IEnumerable<OntologyVersionRecord> ontologyVersions)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(ontologyVersions);

        var entityList = entities.ToList();
        var entityRows = new List<Dictionary<string, object?>>();
        var mergedInto = new List<Dictionary<string, object?>>();
        var knownEntityIds = new HashSet<long>(entityList.Select(e => e.Id));

        foreach (var e in entityList)
        {
            entityRows.Add(new()
            {
                ["ref"] = EntityRef(e.Id),
                ["kind"] = e.Kind,
                ["canonicalLabel"] = e.CanonicalLabel,
                // De BrainRef-vorm (mechanic:/concept:/tag:) rijdt als PROPERTY mee —
                // handig voor toekomstige entity-linking (fase 4), maar NOOIT de
                // node-sleutel (zie ref-namespace-scheiding).
                ["brainRef"] = e.Ref.Format(),
                ["altLabels"] = (e.AltLabels ?? []).ToList(),
                ["status"] = e.Status,
                ["definition"] = e.Definition,
                ["createdByRun"] = e.CreatedByRunId,
            });

            // MERGED_INTO: tombstone → overlevende (beide brein-knopen, eigen ref).
            // Alleen als het doel óók geprojecteerd wordt — een dangling merge-doel
            // levert geen edge (zelfde "knoop zonder edge"-gedrag als GraphSyncService).
            if (e.MergedIntoId is { } targetId && knownEntityIds.Contains(targetId))
                mergedInto.Add(new()
                {
                    ["from"] = EntityRef(e.Id),
                    ["to"] = EntityRef(targetId),
                });
        }

        // Predicaten: rejected leeft alleen als audit-spoor (voedt de motor niet) —
        // geen knoop, zelfde lijn als een rejected Interaction. Candidate + reviewed
        // zijn retrieval-zichtbaar met de status als property (staging-weging).
        var predicateRows = new List<Dictionary<string, object?>>();
        var hasPredicate = new List<Dictionary<string, object?>>();
        foreach (var p in predicates)
        {
            if (p.Status == MechanicPredicateStatus.Rejected) continue;
            var predRef = PredicateRef(p.Id);
            predicateRows.Add(new()
            {
                ["ref"] = predRef,
                ["predicate"] = p.Predicate,
                ["objectToken"] = p.ObjectToken,
                ["status"] = p.Status,
                ["createdByRun"] = p.CreatedByRunId,
            });
            // HAS_PREDICATE: subject-entiteit → predicaat. Alleen als de subject-
            // entiteit geprojecteerd is; anders knoop zonder edge (de MATCH mist stil).
            if (knownEntityIds.Contains(p.SubjectEntityId))
                hasPredicate.Add(new()
                {
                    ["entity"] = EntityRef(p.SubjectEntityId),
                    ["predicate"] = predRef,
                });
        }

        // Ontologie-versies: op SemVer geordend (onparseerbaar → achteraan op
        // AppliedAt/Id). De hoogste versie is `current`; opeenvolgende versies
        // krijgen een PRECEDES-edge (ouder → nieuwer) — de versie-historie als keten.
        var ordered = ontologyVersions
            .OrderBy(v => SemVer.Parse(v.Version) is { } s ? 0 : 1)
            .ThenBy(v => SemVer.Parse(v.Version) ?? new SemVer(0, 0, 0))
            .ThenBy(v => v.AppliedAt)
            .ThenBy(v => v.Id)
            .ToList();

        var ontologyRows = new List<Dictionary<string, object?>>();
        var precedes = new List<Dictionary<string, object?>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var v = ordered[i];
            ontologyRows.Add(new()
            {
                ["ref"] = OntologyVersionRef(v.Id),
                ["version"] = v.Version,
                ["fingerprint"] = v.Fingerprint,
                ["bumpKind"] = v.BumpKind,
                ["notes"] = v.Notes,
                ["current"] = i == ordered.Count - 1,
                ["appliedAt"] = v.AppliedAt.UtcDateTime.ToString("o"),
                ["createdByRun"] = v.RunId,
            });
            if (i > 0)
                precedes.Add(new()
                {
                    ["from"] = OntologyVersionRef(ordered[i - 1].Id),
                    ["to"] = OntologyVersionRef(v.Id),
                });
        }

        return new BrainProjectionRows(
            entityRows, mergedInto, predicateRows, hasPredicate, ontologyRows, precedes);
    }
}

/// <summary>De param-rijen voor de brein-projectie (fase live-graph, #227). Alle
/// lijsten zijn dictionaries-only, klaar voor batched UNWIND. De refs in
/// <see cref="CanonicalEntities"/>/<see cref="Predicates"/>/<see cref="OntologyVersions"/>
/// zijn de idempotentie-sleutels (MERGE op <c>ref</c>).</summary>
public sealed record BrainProjectionRows(
    IReadOnlyList<Dictionary<string, object?>> CanonicalEntities,
    IReadOnlyList<Dictionary<string, object?>> MergedIntoEdges,
    IReadOnlyList<Dictionary<string, object?>> Predicates,
    IReadOnlyList<Dictionary<string, object?>> HasPredicateEdges,
    IReadOnlyList<Dictionary<string, object?>> OntologyVersions,
    IReadOnlyList<Dictionary<string, object?>> PrecedesEdges);
