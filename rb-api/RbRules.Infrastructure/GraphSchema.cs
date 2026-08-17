using Neo4j.Driver;

namespace RbRules.Infrastructure;

/// <summary>Neo4j-schema-bootstrap. Audit-fix: de PoP had geen enkele constraint,
/// waardoor elke MERGE een label-scan was en concurrent syncs dubbele nodes
/// konden opleveren. Idempotent (IF NOT EXISTS).</summary>
public static class GraphSchema
{
    public static readonly string[] Constraints =
    [
        "CREATE CONSTRAINT card_id IF NOT EXISTS FOR (c:Card) REQUIRE c.id IS UNIQUE",
        "CREATE CONSTRAINT set_id IF NOT EXISTS FOR (s:Set) REQUIRE s.id IS UNIQUE",
        "CREATE CONSTRAINT domain_name IF NOT EXISTS FOR (d:Domain) REQUIRE d.name IS UNIQUE",
        "CREATE CONSTRAINT tag_name IF NOT EXISTS FOR (t:Tag) REQUIRE t.name IS UNIQUE",
        "CREATE CONSTRAINT mechanic_name IF NOT EXISTS FOR (m:Mechanic) REQUIRE m.name IS UNIQUE",
        "CREATE CONSTRAINT rule_section_code IF NOT EXISTS FOR (r:RuleSection) REQUIRE r.code IS UNIQUE",
        "CREATE CONSTRAINT concept_id IF NOT EXISTS FOR (c:Concept) REQUIRE c.id IS UNIQUE",
        "CREATE CONSTRAINT erratum_id IF NOT EXISTS FOR (e:Erratum) REQUIRE e.id IS UNIQUE",
        "CREATE CONSTRAINT ban_entry_id IF NOT EXISTS FOR (b:BanEntry) REQUIRE b.id IS UNIQUE",
    ];

    public static async Task EnsureAsync(IDriver driver, CancellationToken ct = default)
    {
        await using var session = driver.AsyncSession();
        foreach (var cypher in Constraints)
            await session.RunAsync(cypher);
    }
}
