namespace RbRules.Tests;

/// <summary>De projectie↔ontologie-guard (#289) leunt volledig op
/// <see cref="CypherEdgeScanner"/>: wat hij niet ziet, wordt niet bewaakt. De
/// eerste versie was één regex en liet vier klassen drift door (commentaar,
/// ketens, geneste haken, cijfers in namen) — elk hier vastgelegd als test, zodat
/// een latere "vereenvoudiging" naar een regex meteen rood gaat.</summary>
public class CypherEdgeScannerTests
{
    private static string[] Edges(string cypher) => [.. CypherEdgeScanner.WrittenEdges(cypher)];

    // ── De vorm zoals de projecties hem echt schrijven ────────────────────────

    [Theory]
    [InlineData("MERGE (c)-[:FROM_SET]->(s)", "FROM_SET")]
    [InlineData("MERGE (ix)-[r:HAS_ROLE {role: row.role}]->(f)", "HAS_ROLE")]
    [InlineData("MERGE (a)-[r:RELATES_TO {kind: row.kind}]->(b)", "RELATES_TO")]
    [InlineData("MERGE (a)-[:X]-(b)", "X")]
    [InlineData("MERGE (a)<-[:X]-(b)", "X")]
    public void LeestDeGeschrevenEdge(string cypher, string expected) =>
        Assert.Equal([expected], Edges(cypher));

    [Fact]
    public void OpmaakIsGeenContract()
    {
        // Aliassen, witruimte en regelafbrekingen mogen nooit iets uitmaken — dat
        // is de hele reden dat deze guard uitgevoerde Cypher leest.
        foreach (var variant in (string[])
                 [
                     "MERGE (k)-[:EXPLAINS]->(r)",
                     "MERGE (concept)-[e:EXPLAINS]->(section)",
                     "MERGE   (k) - [ :EXPLAINS ] -> (r)",
                     "MERGE\n  (k)\n  -[:EXPLAINS]->\n  (r)",
                     "merge (k)-[:EXPLAINS]->(r)",
                 ])
            Assert.Equal(["EXPLAINS"], Edges(variant));
    }

    // ── De vier gaten uit de review ───────────────────────────────────────────

    [Fact]
    public void Commentaar_TeltNietMee()
    {
        // F2: een statement uitcommentariëren is de alledaagste manier om het uit
        // te zetten. Voorheen bleef dat groen, terwijl VERWIJDEREN rood gaf.
        Assert.Empty(Edges("// MERGE (k)-[:EXPLAINS]->(r)"));
        Assert.Empty(Edges("/* MERGE (k)-[:EXPLAINS]->(r) */"));
        Assert.Equal(["PART_OF"], Edges(
            """
            MERGE (a)-[:PART_OF]->(b)   // was ooit MERGE (a)-[:EXPLAINS]->(b)
            """));
    }

    [Fact]
    public void Keten_LevertElkeEdge_NietAlleenDeEerste()
    {
        // F3: geldige, idiomatische Cypher waarin alles ná de eerste hop onzichtbaar was.
        Assert.Equal(["X", "Y", "Z"], Edges("MERGE (a)-[:X]->(b)-[:Y]->(c)<-[:Z]-(d)"));
    }

    [Fact]
    public void GenesteHaken_GevenGeenValsAlarm()
    {
        // F4: hier gaf de regex "geen enkele projectie schrijft die edge nog" op een
        // volstrekt gewone refactor.
        Assert.Equal(["PART_OF"], Edges(
            "MERGE (child {norm: toLower(p.child)})-[:PART_OF]->(parent {n: coalesce(a, b)})"));
    }

    [Fact]
    public void CijfersInDeNaam_HorenBijDeNaam()
    {
        // F5: "ABOUT2" werd gelezen als "ABOUT" — een stille alias op een bestaande
        // catalogus-entry, precies de #274-driftklasse met een cijfer erin.
        Assert.Equal(["ABOUT2"], Edges("MERGE (cl)-[:ABOUT2]->(t)"));
        Assert.Equal(["HAS_ROLE_V2"], Edges("MERGE (a)-[:HAS_ROLE_V2]->(b)"));
    }

    // ── Wat NIET mag meetellen ────────────────────────────────────────────────

    [Fact]
    public void MatchEnDelete_ZijnGeenSchrijfClausules()
    {
        // Een edge verwijderen of matchen is geen bewering over de ontologie.
        Assert.Empty(Edges("MATCH ()-[r:RELATES_TO]->() DELETE r"));
        Assert.Empty(Edges("MATCH (:CanonicalEntity)-[r:MERGED_INTO]->() DELETE r"));
        Assert.Equal(["ABOUT"], Edges(
            "MATCH (cl:Claim {ref: p.claim}) MATCH (t:Card {id: p.target}) MERGE (cl)-[:ABOUT]->(t)"));
    }

    [Fact]
    public void StringLiteralen_VerstorenDeBalansNiet()
    {
        // Een haakje of aanhalingsteken in een literal mag de haakjes-telling niet
        // laten ontsporen, en Cypher-achtige tekst in een literal telt niet mee.
        Assert.Equal(["PART_OF"], Edges(
            "MERGE (a {label: 'een ) haakje'})-[:PART_OF]->(b)"));
        Assert.Empty(Edges("CREATE (n {note: 'MERGE (a)-[:NEP]->(b)'})"));
    }

    [Fact]
    public void KnoopClausulesZonderRelatie_LeverenNiets()
    {
        Assert.Empty(Edges("CREATE (r:RuleSection {ref: row.ref, code: row.code})"));
        Assert.Empty(Edges("MERGE (c:Card {id: row.id}) SET c.ref = row.ref"));
        // "ON CREATE SET" is geen schrijf-clausule over edges.
        Assert.Equal(["FROM_SET"], Edges(
            """
            MERGE (s:Set {id: row.set}) ON CREATE SET s.label = row.setLabel
            SET s.ref = row.setRef
            MERGE (c)-[:FROM_SET]->(s)
            """));
    }

    [Fact]
    public void PropertyMap_LevertGeenNepType()
    {
        // "{role: row.role}" mag geen type "row" opleveren.
        Assert.Equal(["HAS_ROLE"], Edges("MERGE (ix)-[r:HAS_ROLE {role: row.role}]->(f)"));
    }

    [Fact]
    public void MeerdereTypes_EnRelatieZonderType()
    {
        Assert.Equal(["X", "Y"], Edges("MERGE (a)-[:X|Y]->(b)"));
        Assert.Empty(Edges("MERGE (a)-[r]->(b)"));
        Assert.Empty(Edges("MERGE (a)--(b)"));
    }

    [Fact]
    public void Foreach_WordtGelezen()
    {
        Assert.Equal(["AFFECTS"], Edges(
            "FOREACH (x IN $rows | MERGE (a)-[:AFFECTS]->(b))"));
    }
}
