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
/// knooplabels die niemand ooit schrijft — inferentie die per definitie niets raakt.
///
/// Deze tests pinnen de drie lagen op elkaar vast: de canonieke naam in het schema,
/// de MERGE-clausule die de projectie uitvoert, de whitelist waarmee de brein-API de
/// graph bevraagt, en de door de reasoner gegenereerde property-chain-Cypher. Ze
/// falen op de eerste van de vier die wegloopt.</summary>
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

    private static string ClauseFor(RelationType type) => type switch
    {
        RelationType.HasMechanic => GraphSyncService.MechanicMergeClause,
        RelationType.HasDomain => GraphSyncService.DomainMergeClause,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

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
    public void Projectie_SchrijftPreciesDeSchemaEdgeEnHetSchemaLabel(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // De clausule die GraphSyncService.SyncAsync letterlijk uitvoert.
        var clause = ClauseFor(type);

        Assert.Contains($"MERGE ({alias}:{nodeLabel} {{name: p.value}})", clause);
        Assert.Contains($"MERGE (c)-[:{edgeName}]->({alias})", clause);
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public void BreinApi_KentDeEdgeInZijnWhitelist(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // BrainQuery.EdgeTypes is de whitelist waarmee /brain de graph filtert:
        // een schema-naam die daar niet in staat, is een naam die de graph niet kent.
        Assert.Contains(OntologySchema.Relations[type].EdgeName, BrainQuery.EdgeTypes);
        Assert.Contains(edgeName, BrainQuery.EdgeTypes);

        // En de projectie schrijft dat facet ook echt met dat label/alias.
        Assert.Contains($"({alias}:{nodeLabel} ", ClauseFor(type));
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
