using RbRules.Domain.Ontology;
using RbRules.Domain.Reasoning;

namespace RbRules.Tests;

/// <summary>Redeneer-laag (fase 3, #227, §5): de PURE regel-generatie uit de
/// ontologie en de verplichte 'derived-by-rule'-tagging + provenance op elke
/// afgeleide edge. Neo4j zit niet in CI — hier toetsen we dat de Cypher-templates
/// correct uit <see cref="OntologySchema"/> volgen en de invarianten dragen; de
/// live-executie is integratie-follow-up.</summary>
public class InferenceRuleRegistryTests
{
    // ── isa-closure (subclass-overerving) ────────────────────────────────────

    [Fact]
    public void IsaPairs_ErftGovernanceVanBestuurbareVoorouders()
    {
        var pairs = InferenceRuleRegistry.IsaPairs()
            .Select(p => ((string)p["sub"]!, (string)p["super"]!))
            .ToHashSet();

        // Unit ⊑ Card én ⊑ Object → erft van beide bestuurbare superklassen.
        Assert.Contains(("Unit", "Object"), pairs);
        Assert.Contains(("Unit", "Card"), pairs);
        // Mechanic ⊑ Concept.
        Assert.Contains(("Mechanic", "Concept"), pairs);
    }

    [Fact]
    public void IsaPairs_ErftNooitVanDeWortelThing()
    {
        Assert.DoesNotContain(InferenceRuleRegistry.IsaPairs(),
            p => (string)p["super"]! == nameof(EntityType.Thing));
    }

    [Fact]
    public void IsaPairs_ZijnAllemaalGeldigeGeregistreerdeTypen()
    {
        foreach (var p in InferenceRuleRegistry.IsaPairs())
        {
            Assert.NotNull(OntologySchema.ParseEntityType((string)p["sub"]!));
            Assert.NotNull(OntologySchema.ParseEntityType((string)p["super"]!));
        }
    }

    // ── property-chain → GOVERNED_BY ─────────────────────────────────────────

    [Fact]
    public void GovernedByChains_VindtDeKeywordInvokesMechanicKeten()
    {
        var chains = InferenceRuleRegistry.GovernedByChains()
            .Select(c => c.Select(r => r.EdgeName).ToArray())
            .ToList();

        // De canonieke keten: Card -HAS_KEYWORD-> Keyword -INVOKES-> Mechanic
        // -GOVERNED_BY-> RuleSection (zo bereikt een Deflect-vraag §7.4 in één hop).
        Assert.Contains(chains, c =>
            c.SequenceEqual(new[] { "HAS_KEYWORD", "INVOKES", "GOVERNED_BY" }));
    }

    [Fact]
    public void GovernedByChains_EindigenAltijdInGovernedByEnZijnMinstensTweeHops()
    {
        foreach (var chain in InferenceRuleRegistry.GovernedByChains())
        {
            Assert.True(chain.Count >= 2);
            Assert.Equal(RelationType.GovernedBy, chain[^1].Type);
        }
    }

    [Fact]
    public void PropertyChainRules_MergenGovernedByMetProvenance()
    {
        var rules = InferenceRuleRegistry.PropertyChainRules();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.Equal(InferenceFamily.PropertyChain, rule.Family);
            Assert.Equal("GOVERNED_BY", rule.DerivedEdge);
            Assert.Contains("MATCH (c:Card)", rule.Cypher);
            Assert.Contains("MERGE (c)-[g:GOVERNED_BY]->(s)", rule.Cypher);
            AssertDerivedTagging(rule);
        }
    }

    [Fact]
    public void PropertyChainRules_KetenenNietDoorDeDenormRelatesToCache()
    {
        // RELATES_TO is de gedenormaliseerde retrieval-projectie (nooit bron van
        // waarheid) en INTERACTS_WITH een kennis-loze hint — uit die caches leid je
        // geen governance af.
        foreach (var rule in InferenceRuleRegistry.PropertyChainRules())
        {
            Assert.DoesNotContain("RELATES_TO", rule.Cypher);
            Assert.DoesNotContain("INTERACTS_WITH", rule.Cypher);
        }
    }

    // ── symmetrische sluiting ────────────────────────────────────────────────

    [Fact]
    public void SymmetricClosureRules_DekkenDeSymmetrischeKaleRelaties()
    {
        var edges = InferenceRuleRegistry.SymmetricClosureRules()
            .Select(r => r.DerivedEdge)
            .ToHashSet();

        Assert.Contains("INTERACTS_WITH", edges);
        Assert.Contains("CONTRADICTS", edges);
    }

    [Fact]
    public void SymmetricClosureRules_ZijnBoundedMetNotExists()
    {
        foreach (var rule in InferenceRuleRegistry.SymmetricClosureRules())
        {
            Assert.Contains("NOT EXISTS", rule.Cypher);
            AssertDerivedTagging(rule);
        }
    }

    // ── subproperty-collapse ─────────────────────────────────────────────────

    [Fact]
    public void SubpropertyCollapse_LegeMap_GeenRegels()
    {
        Assert.Empty(InferenceRuleRegistry.SubpropertyCollapseRules(
            new Dictionary<string, string>()));
    }

    [Fact]
    public void SubpropertyCollapse_MapEntry_VouwtNaarCanoniekeSuperProperty()
    {
        var rule = Assert.Single(InferenceRuleRegistry.SubpropertyCollapseRules(
            new Dictionary<string, string> { ["versterkt"] = "strengthens" }));

        Assert.Equal(InferenceFamily.SubpropertyCollapse, rule.Family);
        Assert.Equal("RELATES_TO", rule.DerivedEdge);
        var row = Assert.Single(rule.Rows);
        Assert.Equal("versterkt", row["sub"]);
        Assert.Equal("strengthens", row["super"]);
        Assert.Contains("UNWIND $rows AS m", rule.Cypher);
        AssertDerivedTagging(rule);
    }

    // ── invarianten over ALLE regels ─────────────────────────────────────────

    [Fact]
    public void All_RegelsHebbenUniekeVeiligeIds()
    {
        var ids = InferenceRuleRegistry.All.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(InferenceRuleRegistry.IsSafeRuleId(id),
            $"regel-id '{id}' is niet Cypher-veilig"));
    }

    [Fact]
    public void All_ElkeAfgeleideEdgeIsGetagdMetRegelIdEnProvenance()
    {
        Assert.NotEmpty(InferenceRuleRegistry.All);
        foreach (var rule in InferenceRuleRegistry.All)
            AssertDerivedTagging(rule);
    }

    [Fact]
    public void Params_DragenRunIdEnTijd()
    {
        var now = DateTimeOffset.UtcNow;
        var p = DerivedEdgeProvenance.Params("RUN123", now);
        Assert.Equal("RUN123", p[DerivedEdgeProvenance.RunProp]);
        Assert.Equal(now.UtcDateTime.ToString("o"), p["now"]);
    }

    /// <summary>Elke reasoner-regel tagt zijn afgeleide edge als 'derived by rule X'
    /// mét run-provenance — de rode draad #236 (niets levert onzichtbare state) én de
    /// 'afgeleide edge is nooit bron'-invariant (herkenbaar aan derived=true).</summary>
    private static void AssertDerivedTagging(InferenceRule rule)
    {
        Assert.Contains($"{DerivedEdgeProvenance.DerivedProp} = true", rule.Cypher);
        Assert.Contains($"{DerivedEdgeProvenance.RuleProp} = '{rule.Id}'", rule.Cypher);
        Assert.Contains("$runId", rule.Cypher);
        Assert.Contains("$now", rule.Cypher);
        Assert.Contains($"'{DerivedEdgeProvenance.DeterministicModel}'", rule.Cypher);
    }
}
