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
    ];

    public static async Task EnsureAsync(IDriver driver, CancellationToken ct = default)
    {
        await using var session = driver.AsyncSession();
        foreach (var cypher in Statements)
            await session.RunAsync(cypher);
    }
}
