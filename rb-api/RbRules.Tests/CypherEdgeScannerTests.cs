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

/// <summary>De knooplabel-kant van dezelfde scanner (#289 PR 2). Wat hij hier niet
/// ziet, bewaakt <see cref="ProjectionLabelGuardTests"/> niet — en een verkeerd
/// gelezen label geeft juist wél vals alarm, dus dit deel moet zowel volledig als
/// terughoudend zijn.</summary>
public class CypherEdgeShapeScannerTests
{
    private static string[] Shapes(string cypher) =>
        [.. CypherEdgeScanner.WrittenEdgeShapes(cypher).Select(s => s.Format())];

    // ── Alias-binding: het eigenlijke werk ────────────────────────────────────

    [Fact]
    public void AliasUitEenEerdereMatch_BepaaltHetLabel() =>
        // De vorm waarin de projectie vrijwel al haar edges schrijft: de labels staan
        // in de MATCH-clausules, de MERGE noemt alleen de aliassen.
        Assert.Equal(["(:Claim)-[:ABOUT]->(:Card)"], Shapes(
            "MATCH (cl:Claim {ref: p.claim}) MATCH (t:Card {id: p.target}) MERGE (cl)-[:ABOUT]->(t)"));

    [Fact]
    public void AliasUitEenEerdereMerge_TeltOok() =>
        // FROM_SET: beide eindpunten worden in hetzelfde statement ge-MERGE'd, en pas
        // daarna verbonden.
        Assert.Equal(["(:Card)-[:FROM_SET]->(:Set)"], Shapes(
            """
            MERGE (c:Card {id: row.id}) SET c.ref = row.ref
            MERGE (s:Set {id: row.set}) ON CREATE SET s.label = row.setLabel
            MERGE (c)-[:FROM_SET]->(s)
            """));

    [Fact]
    public void HergebruikteAlias_VerliestZijnLabelNiet() =>
        // De binding is een UNIE, geen overschrijving: de latere, label-loze vermelding
        // van `c` mag de eerdere niet wissen. Deed hij dat wel, dan zou élke edge in de
        // kaart-projectie als label-loos binnenkomen — precies de stille dekkingsval
        // die deze guard moet betrappen.
        Assert.Equal(["(:Card)-[:X]->(:Tag)", "(:Card)-[:Y]->(:Tag)"], Shapes(
            """
            MATCH (c:Card) MATCH (t:Tag)
            MERGE (c)-[:X]->(t)
            MERGE (c)-[:Y]->(t)
            """));

    [Fact]
    public void LabelLozeAlias_IsOnbepaald_GeenFout()
    {
        // RELATES_TO en HAS_ROLE matchen op ref zonder label. Dat moet als "niets
        // opgelegd" terugkomen, niet als een verzonnen label en niet als fout.
        Assert.Equal(["()-[:RELATES_TO]->()"], Shapes(
            "MATCH (a {ref: row.from}) MATCH (b {ref: row.to}) MERGE (a)-[r:RELATES_TO {kind: row.kind}]->(b)"));
        Assert.Equal(["(:Interaction)-[:HAS_ROLE]->()"], Shapes(
            "MATCH (ix:Interaction {ref: row.interaction}) MATCH (f {ref: row.filler}) "
            + "MERGE (ix)-[r:HAS_ROLE {role: row.role}]->(f)"));
    }

    [Fact]
    public void MeerdereLabelsOpEenKnoop()
    {
        // :Interaction:Concept — de multi-label vorm uit de reïficatie-tak. Labels
        // worden gesorteerd weergegeven: de volgorde in de Cypher is geen contract.
        Assert.Equal(["(:Concept:Interaction)-[:X]->(:RuleSection)"], Shapes(
            "MATCH (ix:Interaction:Concept) MATCH (s:RuleSection) MERGE (ix)-[:X]->(s)"));
        Assert.Equal(["(:Concept:Interaction)-[:X]->(:RuleSection)"], Shapes(
            "MATCH (ix:Concept:Interaction) MATCH (s:RuleSection) MERGE (ix)-[:X]->(s)"));
    }

    [Fact]
    public void InlineLabelsInDeSchrijfClausule() =>
        Assert.Equal(["(:Card)-[:HAS_TAG]->(:Tag)"], Shapes(
            "MERGE (c:Card {id: p.id})-[:HAS_TAG]->(t:Tag {name: p.value})"));

    [Fact]
    public void AnoniemeKnoop_HoudtZijnEigenLabel() =>
        Assert.Equal(["(:Card)-[:X]->()"], Shapes("MERGE (:Card)-[:X]->()"));

    // ── Richting ──────────────────────────────────────────────────────────────

    [Fact]
    public void OmgekeerdePijl_DraaitBronEnDoelOm() =>
        Assert.Equal(["(:Set)-[:X]->(:Card)"], Shapes("MERGE (c:Card)<-[:X]-(s:Set)"));

    [Fact]
    public void Keten_LevertElkeVorm() =>
        Assert.Equal(
            ["(:Card)-[:X]->(:Tag)", "(:Set)-[:Y]->(:Tag)"],
            Shapes("MERGE (c:Card)-[:X]->(t:Tag)<-[:Y]-(s:Set)"));

    // ── Terughoudendheid: wat GEEN knooppatroon is ────────────────────────────

    [Fact]
    public void FunctieAanroep_BindtGeenAlias() =>
        // `toLower(p.child)` mag geen alias `p` opleveren; deed het dat wel, dan zou
        // een willekeurige property-expressie labels gaan verzinnen.
        Assert.Equal(["()-[:PART_OF]->()"], Shapes(
            "MERGE (child {norm: toLower(p.child)})-[:PART_OF]->(parent {n: coalesce(a, b)})"));

    [Fact]
    public void PredicaatTussenHaakjes_BindtGeenAlias() =>
        // `WHERE (n:Set OR n:Tag)` is een expressie, geen knooppatroon. Zou de scanner
        // hem binden, dan kreeg `n` een label dat de MATCH helemaal niet afdwingt —
        // een schending die er niet is, oftewel vals alarm.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (n) WHERE (n:Set OR n:Tag) MATCH (m) MERGE (n)-[:X]->(m)"));

    [Fact]
    public void LabelsUitCommentaarEnLiteralen_TellenNietMee()
    {
        Assert.Empty(Shapes("// MERGE (c:Card)-[:X]->(t:Tag)"));
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MERGE (a {note: 'MATCH (a:Card)'})-[:X]->(b)"));
    }

    [Fact]
    public void MatchClausules_SchrijvenNiets() =>
        Assert.Empty(Shapes("MATCH (:CanonicalEntity)-[r:MERGED_INTO]->() DELETE r"));
}
