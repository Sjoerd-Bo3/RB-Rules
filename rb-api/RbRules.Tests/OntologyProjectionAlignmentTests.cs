using System.Text.RegularExpressions;
using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Domain.Reasoning;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Regressie bij #274: schema en graph-projectie mogen niet meer stil uit
/// elkaar groeien. <see cref="OntologySchema"/> is volgens #227 de ÉNE schema-bron,
/// maar noemde de kaart→mechaniek-relatie HAS_KEYWORD terwijl
/// <see cref="GraphSyncService"/> HAS_MECHANIC projecteerde (en de kaart→domein-
/// relatie IN_DOMAIN terwijl de projectie HAS_DOMAIN schreef). Gevolgen: het schema
/// valideerde niet wat er écht stond, een weiger-reden noemde een relatietype dat
/// niet bestaat, en de Neo4j-native reasoner genereerde Cypher over edges én
/// knooplabels die niemand ooit schrijft.
///
/// Deze tests pinnen vier lagen op elkaar vast: de canonieke naam in het schema, de
/// Cypher die <see cref="GraphSyncService.SyncAsync"/> ECHT naar de driver stuurt
/// (via een opnemende <c>IDriver</c> — géén parallel opgebouwde string, zie
/// <see cref="CapturedCypherAsync"/>), de whitelist waarmee de brein-API de graph
/// bevraagt, en de gegenereerde property-chain-Cypher. Ze falen op de eerste die
/// wegloopt; geverifieerd met een mutatie die de aanroepplek in SyncAsync terugzet
/// op de oude literals (7 rode tests).
///
/// LET OP wat dit NIET bewijst: dat de reasoner nu iets afleidt. Hop 2 van de keten
/// (<c>(:Mechanic)-[:GOVERNED_BY]->(:RuleSection)</c>) wordt nergens geprojecteerd —
/// GOVERNED_BY komt uitsluitend van een <c>:Interaction</c> — dus de property-chain
/// materialiseert nog steeds nul edges. Deze tests bewaken de NAAM-uitlijning, niet
/// de levendheid van de inferentie (ARCHITECTURE §6.4).</summary>
public class OntologyProjectionAlignmentTests
{
    /// <summary>De kaart-facetten die de projectie deterministisch schrijft, met hun
    /// vastgepinde canonieke edge-naam en knooplabel. De literals hier zijn het anker:
    /// de projectie-clausules worden uit het schema gegenereerd, dus alleen een
    /// letterlijke verwachting betrapt een hernoeming die beide kanten meeneemt.</summary>
    public static TheoryData<RelationType, string, string, string> Facets() => new()
    {
        { RelationType.HasMechanic, "HAS_MECHANIC", nameof(EntityType.Mechanic), "m" },
        { RelationType.HasDomain, "HAS_DOMAIN", nameof(EntityType.Domain), "d" },
    };

    /// <summary>Draait <see cref="GraphSyncService.SyncAsync"/> met één kaart die een
    /// domein én een mechaniek draagt, tegen een <see cref="RecordingDriver"/>, en geeft
    /// élke Cypher-query terug die de service naar Neo4j stuurde. Zo toetsen de tests
    /// het UITVOERENDE pad in plaats van een hulp-property die de service zelf niet
    /// hoeft te gebruiken.</summary>
    private static async Task<IReadOnlyList<string>> CapturedCypherAsync()
    {
        await using var db = TestGraphDb.New();
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001", Name = "Shieldbearer", Type = "Unit",
            Domains = ["Fury"], Mechanics = ["Tank"],
        });
        await db.SaveChangesAsync();

        var driver = new RecordingDriver();
        await new GraphSyncService(db, driver).SyncAsync();
        return driver.Queries;
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public void Schema_DraagtDeCanoniekeEdgeNaamEnRange(
        RelationType type, string edgeName, string nodeLabel, string _)
    {
        var relation = OntologySchema.Relations[type];

        Assert.Equal(edgeName, relation.EdgeName);
        Assert.Equal(nodeLabel, Assert.Single(relation.Range).ToString());
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public async Task Projectie_SchrijftPreciesDeSchemaEdgeEnHetSchemaLabel(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // CRUCIAAL: dit toetst de Cypher die SyncAsync ECHT naar de driver stuurt,
        // niet een parallel opgebouwde string. Een eerdere versie van deze test
        // asserteerde op GraphSyncService.MechanicMergeClause; die property wordt
        // dode code zodra iemand op de aanroepplek weer een literal zet, en dan
        // bewees de test alleen nog dat het schema met zichzelf klopt. Door de
        // service met een opnemende driver te draaien valt de test wél om als de
        // aanroepplek de ontologie omzeilt.
        var executed = await CapturedCypherAsync();

        var merge = Assert.Single(executed, q => q.Contains($":{nodeLabel} {{name: p.value}}"));
        Assert.Contains($"MERGE ({alias}:{nodeLabel} {{name: p.value}})", merge);
        Assert.Contains($"MERGE (c)-[:{edgeName}]->({alias})", merge);

        // En de naam die er echt uitgaat is de naam uit het register.
        Assert.Contains($"-[:{OntologySchema.Relations[type].EdgeName}]->", merge);
    }

    [Theory]
    [InlineData("HAS_KEYWORD")]
    [InlineData("IN_DOMAIN")]
    [InlineData(":Keyword {name:")]
    public async Task Projectie_SchrijftDeOudeNamenNergensMeer(string retired)
    {
        var executed = await CapturedCypherAsync();
        Assert.DoesNotContain(executed, q => q.Contains(retired, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public async Task BreinApi_KentDeEdgeDieDeProjectieSchrijft(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // BrainQuery.EdgeTypes is de whitelist waarmee /brain de graph filtert:
        // een edge die de projectie schrijft maar die daar niet in staat, is voor de
        // brein-API onzichtbaar. Ook hier het uitvoerende pad als bron.
        Assert.Contains(OntologySchema.Relations[type].EdgeName, BrainQuery.EdgeTypes);
        Assert.Contains(edgeName, BrainQuery.EdgeTypes);

        var executed = await CapturedCypherAsync();
        Assert.Contains(executed, q => q.Contains($"({alias}:{nodeLabel} ")
                                       && q.Contains($"-[:{edgeName}]->"));
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public void Reasoner_KetentOverDeGeprojecteerdeEdgeEnHetGeprojecteerdeLabel(
        RelationType type, string edgeName, string nodeLabel, string _)
    {
        // De property-chain-regel die bij dit facet begint moet exact de geprojecteerde
        // hop matchen. Vóór #274 stond hier HAS_KEYWORD->(:Keyword) resp.
        // IN_DOMAIN->(:Domain): een MATCH die op de live graaf nul rijen oplevert.
        var rules = InferenceRuleRegistry.PropertyChainRules()
            .Where(r => r.Cypher.Contains($"[:{edgeName}]"))
            .ToList();

        Assert.NotEmpty(rules);
        foreach (var rule in rules)
            Assert.Contains($"MATCH (c:Card)-[:{edgeName}]->(:{nodeLabel})", rule.Cypher);

        Assert.Equal(OntologySchema.Relations[type].EdgeName, edgeName);
    }

    [Fact]
    public void Reasoner_GebruiktGeenEdgeNaamDieBuitenDeOntologieValt()
    {
        // Elke edge-naam in een gegenereerde regel moet een geregistreerde relatie
        // zijn. Vangt de omgekeerde drift: een handgeschreven Cypher-hop die nergens
        // in het schema staat en dus door niets gevalideerd wordt.
        foreach (var rule in InferenceRuleRegistry.All)
            foreach (Match m in Regex.Matches(rule.Cypher, @"\[[a-z]*:([A-Z_]+)"))
            {
                var edge = m.Groups[1].Value;
                Assert.True(OntologySchema.RelationByEdgeName(edge) is not null,
                    $"regel '{rule.Id}' gebruikt edge '{edge}' die niet in de ontologie staat");
            }
    }

    [Theory]
    [InlineData("HAS_KEYWORD")]
    [InlineData("IN_DOMAIN")]
    public void OudeNaam_BestaatNietMeerInHetSchema(string retiredEdgeName)
    {
        // De hernoemde namen mogen niet als tweede ingang terugsluipen: één relatie,
        // één naam. RelationByEdgeName is hoofdletterongevoelig, dus dit dekt ook
        // een 'has_keyword'-variant.
        Assert.Null(OntologySchema.RelationByEdgeName(retiredEdgeName));
    }
}
