using Neo4j.Driver;

namespace RbRules.Infrastructure;

/// <summary>Neo4j-schema-bootstrap. Audit-fix: de PoP had geen enkele constraint,
/// waardoor elke MERGE een label-scan was en concurrent syncs dubbele nodes
/// konden opleveren. Idempotent (IF NOT EXISTS / IF EXISTS).</summary>
public static class GraphSchema
{
    public static readonly string[] Statements =
    [
        // De oude code-unieke RuleSection-constraint (stond klaar, kreeg nooit
        // knopen) klopt niet met het brein-schema (#104): §-codes botsen
        // tussen bronnen ("intro" en "101" bestaan in de Core Rules én de
        // Tournament Rules). De ref (bron/code) is de echte sleutel.
        "DROP CONSTRAINT rule_section_code IF EXISTS",

        "CREATE CONSTRAINT card_id IF NOT EXISTS FOR (c:Card) REQUIRE c.id IS UNIQUE",
        "CREATE CONSTRAINT set_id IF NOT EXISTS FOR (s:Set) REQUIRE s.id IS UNIQUE",
        "CREATE CONSTRAINT domain_name IF NOT EXISTS FOR (d:Domain) REQUIRE d.name IS UNIQUE",
        "CREATE CONSTRAINT tag_name IF NOT EXISTS FOR (t:Tag) REQUIRE t.name IS UNIQUE",
        "CREATE CONSTRAINT mechanic_name IF NOT EXISTS FOR (m:Mechanic) REQUIRE m.name IS UNIQUE",

        // Kennislaag-knopen (#104, docs/BRAIN.md §2.2): de ref-property
        // (BrainRef-conventie, §2.1) is overal de unieke sleutel.
        "CREATE CONSTRAINT rule_section_ref IF NOT EXISTS FOR (r:RuleSection) REQUIRE r.ref IS UNIQUE",
        "CREATE CONSTRAINT concept_ref IF NOT EXISTS FOR (k:Concept) REQUIRE k.ref IS UNIQUE",
        "CREATE CONSTRAINT claim_ref IF NOT EXISTS FOR (cl:Claim) REQUIRE cl.ref IS UNIQUE",
        "CREATE CONSTRAINT source_ref IF NOT EXISTS FOR (s:Source) REQUIRE s.ref IS UNIQUE",
        "CREATE CONSTRAINT erratum_ref IF NOT EXISTS FOR (e:Erratum) REQUIRE e.ref IS UNIQUE",
        "CREATE CONSTRAINT change_ref IF NOT EXISTS FOR (ch:Change) REQUIRE ch.ref IS UNIQUE",

        // Geverifieerde rulings (#191): zelfde ref-sleutel-afspraak.
        "CREATE CONSTRAINT ruling_ref IF NOT EXISTS FOR (rl:Ruling) REQUIRE rl.ref IS UNIQUE",

        // Provenance-ruggengraat (fase 0a, #233): PROV-O-Activity + Assertion,
        // dezelfde ref-sleutel-afspraak. De harde write-guard (Assertion draagt
        // ALTIJD WAS_GENERATED_BY + DERIVED_FROM) leeft in Postgres (DbContext-
        // poort + AssertionProvenanceGuard) en in de deterministische projectie
        // hieronder; een relatie-existentie-constraint is Neo4j-Enterprise-only
        // en dus bewust niet de bron van die garantie.
        "CREATE CONSTRAINT mining_run_ref IF NOT EXISTS FOR (r:MiningRun) REQUIRE r.ref IS UNIQUE",
        "CREATE CONSTRAINT assertion_ref IF NOT EXISTS FOR (a:Assertion) REQUIRE a.ref IS UNIQUE",

        // Brein-projectie (fase live-graph, #227, §3.5): de brein-lagen die
        // GraphSyncService niet dekt, elk MERGE'd op de eigen ref-namespace
        // (entity:/predicate:/ontologyversion: — bewust los van het BrainRef-
        // alfabet, zodat een label-loze ref-match nooit ambigu wordt). De
        // constraint maakt de MERGE correct (geen dubbele knopen) en snel.
        "CREATE CONSTRAINT canonical_entity_ref IF NOT EXISTS FOR (e:CanonicalEntity) REQUIRE e.ref IS UNIQUE",
        "CREATE CONSTRAINT mechanic_predicate_ref IF NOT EXISTS FOR (p:MechanicPredicate) REQUIRE p.ref IS UNIQUE",
        "CREATE CONSTRAINT ontology_version_ref IF NOT EXISTS FOR (v:OntologyVersion) REQUIRE v.ref IS UNIQUE",
    ];

    public static async Task EnsureAsync(IDriver driver, CancellationToken ct = default)
    {
        await using var session = driver.AsyncSession();
        foreach (var cypher in Statements)
            await session.RunAsync(cypher);
    }
}
