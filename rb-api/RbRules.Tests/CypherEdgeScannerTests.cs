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
        // Een kale ref-match zonder label (en zonder WHERE-disjunctie — zo schreef
        // de projectie RELATES_TO tot #317, en DERIVED_FROM nog steeds) moet als
        // "niets opgelegd" terugkomen, niet als een verzonnen label en niet als
        // fout. Dít is ook de vorm die mutatie (a) van #317 oplevert: sloop de
        // WHERE-disjunctie en deze lege vorm valt bij L1/L3 door de mand.
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
    public void PredicaatTussenHaakjes_IsGeenKnooppatroon_MaarWelEenDisjunctie()
    {
        // `WHERE (n:Set OR n:Tag)` is geen knooppatroon (de haakjes-groep mag geen
        // nep-alias binden via de keten-walker), maar het IS sinds #317 een
        // label-disjunctie: de MATCH dwingt af dat n een Set óf een Tag is. Dat
        // komt disjunctief terug (`|`), niet als multi-label garantie (`:Set:Tag`).
        Assert.Equal(["(:Set|Tag)-[:X]->()"], Shapes(
            "MATCH (n) WHERE (n:Set OR n:Tag) MATCH (m) MERGE (n)-[:X]->(m)"));

        // Een predicaat dat GEEN zuivere label-disjunctie is, bindt niets — anders
        // kreeg `n` een garantie die de MATCH helemaal niet afdwingt (vals alarm de
        // ene kant op, valse geruststelling de andere).
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (n) WHERE (n.rank > 1 OR n.rank IS NULL) MATCH (m) MERGE (n)-[:X]->(m)"));
    }

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

    [Fact]
    public void LabelExpressieInEenPatroon_IsGeenConjunctieveGarantie() =>
        // Cypher's (a:Card|Mechanic) betekent "Card OF Mechanic". De oude lezer las
        // daar `Card` uit — een valse conjunctieve garantie, precies de stille
        // dekkingsfout die deze guard moet betrappen. Onbepaald is de eerlijke
        // lezing: de guard meldt dan "niet te garanderen" in plaats van te zwijgen.
        Assert.Equal(["()-[:X]->(:Tag)"], Shapes(
            "MERGE (a:Card|Mechanic)-[:X]->(t:Tag)"));
}

/// <summary>De WHERE-label-disjunctie (#317): <c>MATCH (a {ref: …})
/// WHERE (a:Card OR a:Mechanic OR …)</c> dwingt wél labels af — de knoop is één
/// van de soorten — en moet dus als DISJUNCTIEF eindpunt binden. Strikt: alles wat
/// geen zuivere label-disjunctie over één alias is, bindt niets.</summary>
public class CypherWhereDisjunctionScannerTests
{
    private static string[] Shapes(string cypher) =>
        [.. CypherEdgeScanner.WrittenEdgeShapes(cypher).Select(s => s.Format())];

    /// <summary>De letterlijke #317-vorm zoals GraphSyncService hem schrijft:
    /// beide eindpunten label-loos gematcht op ref, beide kanten door één
    /// WHERE-disjunctie gebonden.</summary>
    private const string RelatesToStatement =
        """
        UNWIND $rows AS row
        MATCH (a {ref: row.from})
        MATCH (b {ref: row.to})
        WHERE (a:Card OR a:Mechanic OR a:Concept OR a:RuleSection OR a:Claim)
          AND (b:Card OR b:Mechanic OR b:Concept OR b:RuleSection OR b:Claim)
        MERGE (a)-[r:RELATES_TO {kind: row.kind}]->(b)
          SET r.trust = row.trust
        """;

    private const string RelatesToShape =
        "(:Card|Claim|Concept|Mechanic|RuleSection)-[:RELATES_TO]->(:Card|Claim|Concept|Mechanic|RuleSection)";

    [Fact]
    public void WhereDisjunctie_BindtBeideEindpunten() =>
        Assert.Equal([RelatesToShape], Shapes(RelatesToStatement));

    [Fact]
    public void DisjunctieveVorm_MeldtZichDisjunctief()
    {
        // Niet alleen de labels maar ook de AARD van de binding moet kloppen: een
        // disjunctie als multi-label lezen zou de alle-soorten-moeten-passen-toets
        // (ProjectionLabelCheck) stil terugzetten naar één-past-is-genoeg.
        var shape = Assert.Single(CypherEdgeScanner.WrittenEdgeShapes(RelatesToStatement));
        Assert.True(shape.FromDisjunctive);
        Assert.True(shape.ToDisjunctive);
        Assert.Equal(["Card", "Mechanic", "Concept", "RuleSection", "Claim"], shape.FromLabels);
        Assert.Equal(["Card", "Mechanic", "Concept", "RuleSection", "Claim"], shape.ToLabels);
    }

    [Fact]
    public void OpmaakIsGeenContract()
    {
        // Dezelfde niet-mutaties als bij de patroon-scanner: herformatteren, alias
        // hernoemen, kleine letters, haakjes om elk atoom — geen gedragsverschil.
        foreach (var variant in (string[])
                 [
                     "MATCH (x {ref: row.from}) MATCH (y {ref: row.to}) "
                     + "WHERE (x:Card OR x:Mechanic OR x:Concept OR x:RuleSection OR x:Claim) "
                     + "AND (y:Card OR y:Mechanic OR y:Concept OR y:RuleSection OR y:Claim) "
                     + "MERGE (x)-[r:RELATES_TO {kind: row.kind}]->(y)",

                     "MATCH (a {ref: row.from})\nMATCH (b {ref: row.to})\nwhere\n"
                     + "  (a:Card or a:Mechanic or a:Concept or a:RuleSection or a:Claim)\n"
                     + "  and (b:Card or b:Mechanic or b:Concept or b:RuleSection or b:Claim)\n"
                     + "MERGE (a)-[r:RELATES_TO {kind: row.kind}]->(b)",

                     "MATCH (a {ref: row.from}) MATCH (b {ref: row.to}) "
                     + "WHERE ((a:Card) OR (a:Mechanic) OR (a:Concept) OR (a:RuleSection) OR (a:Claim)) "
                     + "AND ((b:Card) OR (b:Mechanic) OR (b:Concept) OR (b:RuleSection) OR (b:Claim)) "
                     + "MERGE (a)-[:RELATES_TO]->(b)",
                 ])
            Assert.Equal([RelatesToShape], Shapes(variant));
    }

    [Fact]
    public void DisjunctieZonderHaakjes_BindtOok() =>
        Assert.Equal(["(:Mechanic|Set)-[:X]->()"], Shapes(
            "MATCH (n {ref: r.x}) WHERE n:Set OR n:Mechanic MATCH (m {ref: r.y}) MERGE (n)-[:X]->(m)"));

    [Fact]
    public void EnkelvoudigLabelPredicaat_BindtAlsEnkeleSoort() =>
        // `WHERE a:Card` is een disjunctie van één: dezelfde garantie als :Card.
        Assert.Equal(["(:Card)-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE a:Card MATCH (b {ref: r.y}) MERGE (a)-[:X]->(b)"));

    [Fact]
    public void PatroonLabelWint_VanDeDisjunctie() =>
        // MATCH (a:Card …) garandeert al méér dan elke disjunctie erbovenop; de
        // disjunctie mag die hardere garantie niet verwateren tot "één van".
        Assert.Equal(["(:Card)-[:X]->()"], Shapes(
            "MATCH (a:Card {ref: r.x}) WHERE (a:Card OR a:Mechanic) MERGE (a)-[:X]->(b)"));

    // ── Wat NIET mag binden: een verzonnen garantie is erger dan een gemiste ──

    [Fact]
    public void GemengdeAliassenInEenGroep_BindenNiets() =>
        // (a:Card OR b:Tag) dwingt geen van beide af: de knoop mag de andere tak zijn.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) MATCH (b {ref: r.y}) WHERE (a:Card OR b:Tag) MERGE (a)-[:X]->(b)"));

    [Fact]
    public void VreemdeTermInDeGroep_MaaktDeGroepOnbruikbaar()
    {
        // Een property-vergelijking in de OR-groep betekent dat de knoop óók zonder
        // enig label door de poort kan.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE (a:Card OR a.rank > 1) MERGE (a)-[:X]->(b)"));
        // Ook op topniveau: (a:Card OR a:Mechanic) OR a.rank > 1 is één OR-boom
        // met een vreemde tak — de haakjes maken dat niet ineens een garantie.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE (a:Card OR a:Mechanic) OR a.rank > 1 MERGE (a)-[:X]->(b)"));
    }

    [Fact]
    public void AndBindtSterkerDanOr_GeenVerzonnenGarantie()
    {
        // Review PR #320: Cypher-precedentie is P OR (Q AND R). Een AND-eerst-
        // splitsing las hier "… AND a:Mechanic" als zelfstandig conjunct en bond
        // een verzonnen (:Mechanic)-garantie — terwijl deze WHERE een kale
        // :Card-knoop gewoon doorlaat via de eerste OR-tak.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE a:Card OR x.flag = true AND a:Mechanic MERGE (a)-[:X]->(b)"));

        // Spiegelbeeld met de disjunctie achteraan: zelfde precedentie-val.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE a:Card AND x.flag = true OR a:Mechanic MERGE (a)-[:X]->(b)"));

        // Zónder topniveau-OR zijn de AND-segmenten wél echte conjuncten: beide
        // kanten blijven gewoon binden. Dit pint dat de fix niet doorschiet.
        Assert.Equal(["(:Card)-[:X]->(:Mechanic)"], Shapes(
            "MATCH (a {ref: r.x}) MATCH (b {ref: r.y}) WHERE a:Card AND b:Mechanic MERGE (a)-[:X]->(b)"));
    }

    [Fact]
    public void ParenlozeHerformattering_MetGemengdePrecedentie_BindtNiets()
    {
        // Review PR #320, corpus-bewijs: haal de haakjes om de bron-groep weg en
        // de doel-disjunctie geldt (Cypher-precedentie!) nog maar op één van de
        // vijf takken — bron:Claim OR … OR (bron:Card AND (doel:…)). Vóór de fix
        // bleef de guard hier groen; nu bindt de scanner niets en komt de vorm
        // als label-loos terug, waarop L1/L3 rood gaan.
        Assert.Equal(["()-[:RELATES_TO]->()"], Shapes(
            """
            UNWIND $rows AS row
            MATCH (bron {ref: row.from})
            MATCH (doel {ref: row.to})
            where bron:Claim or bron:RuleSection or bron:Concept or bron:Mechanic or bron:Card
              and (doel:Card or doel:Mechanic or doel:Concept or doel:RuleSection or doel:Claim)
            MERGE (bron)-[r:RELATES_TO {kind: row.kind}]->(doel)
            """));
    }

    [Fact]
    public void NotVoorDeGroep_BindtNiets() =>
        // NOT (a:Card OR a:Mechanic) garandeert juist dat het GEEN van beide is.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE NOT (a:Card OR a:Mechanic) MERGE (a)-[:X]->(b)"));

    [Fact]
    public void AndereTermenBlijvenWerken_NaastEenOnbruikbareTerm() =>
        // De AND-termen zijn onafhankelijk: één onbruikbare term (NOT (a)--())
        // mag de disjunctie-term ernaast niet meesleuren.
        Assert.Equal(["(:Domain|Mechanic|Set|Tag)-[:X]->()"], Shapes(
            "MATCH (n {ref: r.x}) WHERE (n:Set OR n:Domain OR n:Tag OR n:Mechanic) "
            + "AND NOT (n)--() MERGE (n)-[:X]->(m)"));

    [Fact]
    public void MultiLabelAtoomInDeDisjunctie_BindtNiets() =>
        // (a:Card:Token OR a:Mechanic) is een conjunctie bínnen een disjunctie;
        // dat kan de platte labellijst niet eerlijk dragen, dus liever onbepaald.
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) WHERE (a:Card:Token OR a:Mechanic) MERGE (a)-[:X]->(b)"));

    [Fact]
    public void WhereLichaamStopt_BijDeVolgendeClausule()
    {
        // Het WHERE-lichaam eindigt bij MERGE: het knooppatroon (a:Card) in de
        // schrijf-clausule is patroon-binding, geen predicaat — en andersom mag
        // de disjunctie-lezer niet over de MERGE heen doorlezen.
        Assert.Equal(["(:Card)-[:Y]->(:Mechanic|Set)"], Shapes(
            "MATCH (m {ref: r.y}) WHERE m:Set OR m:Mechanic MERGE (a:Card {id: r.x})-[:Y]->(m)"));

        // En een WITH … WHERE op een rij-property bindt niets (het #116-statement
        // in de kaart-projectie heeft precies zo'n WHERE row.set IS NOT NULL).
        Assert.Equal(["(:Card)-[:FROM_SET]->(:Set)"], Shapes(
            """
            MERGE (c:Card {id: row.id}) SET c.ref = row.ref
            WITH c, row WHERE row.set IS NOT NULL
            MERGE (s:Set {id: row.set})
            MERGE (c)-[:FROM_SET]->(s)
            """));
    }

    [Fact]
    public void DisjunctieUitCommentaarOfLiteral_TeltNietMee()
    {
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {ref: r.x}) // WHERE (a:Card OR a:Mechanic)\nMERGE (a)-[:X]->(b)"));
        Assert.Equal(["()-[:X]->()"], Shapes(
            "MATCH (a {note: 'WHERE (a:Card OR a:Mechanic)'}) MERGE (a)-[:X]->(b)"));
    }
}
