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
    // ── isa-closure: bewust géén materialisatie-regel (regressie #227-review) ─

    [Fact]
    public void GovernedByAfleiding_MaterialiseertGeenCartesischCrossProduct()
    {
        // Finding #1/#2: de verwijderde isa-closure-regel koppelde via twee
        // ONGEJOINDE MATCH-clausules ('super'-knoop los van 'n'-knoop) élke
        // co-getypeerde instantie aan ELKE sectie die een willekeurige
        // superklasse-instantie bestuurde. Pin: elke GOVERNED_BY-afleidende regel
        // vertrekt vanaf één anker langs een VERBONDEN pad — nooit meer een losse
        // tweede knoop-match op label-lidmaatschap (de cross-product-tell).
        var governedByRules = InferenceRuleRegistry.All
            .Where(r => r.DerivedEdge == "GOVERNED_BY")
            .ToList();

        Assert.NotEmpty(governedByRules);           // de property-chain-afleiding blijft
        foreach (var rule in governedByRules)
        {
            Assert.Contains("MATCH (c:Card)", rule.Cypher);
            Assert.DoesNotContain("IN labels(", rule.Cypher);
        }
    }

    // ── property-chain → GOVERNED_BY ─────────────────────────────────────────

    [Fact]
    public void GovernedByChains_VindtDeMechanicKeten()
    {
        var chains = InferenceRuleRegistry.GovernedByChains()
            .Select(c => c.Select(r => r.EdgeName).ToArray())
            .ToList();

        // De canonieke keten: Card -HAS_MECHANIC-> Mechanic -GOVERNED_BY->
        // RuleSection (zo bereikt een Deflect-vraag §7.4 in één hop). Sinds #274
        // loopt die over de relatie die de projectie ECHT schrijft; de oude
        // HAS_KEYWORD ∘ INVOKES-variant mikte op edges en knopen die niemand zet.
        Assert.Contains(chains, c =>
            c.SequenceEqual(new[] { "HAS_MECHANIC", "GOVERNED_BY" }));
        Assert.DoesNotContain(chains, c => c.Contains("HAS_KEYWORD"));
    }

    [Fact]
    public void GovernedByChains_Nr304DeclaratiesScheppenGeenNieuweKetens()
    {
        // #304 declareerde zeven projectie-edges (ABOUT, PART_OF, EXPLAINS,
        // FROM_SET, HAS_TAG, HAS_ROLE, REQUIRES_CONDITION). Geen daarvan mag een
        // nieuwe GOVERNED_BY-keten opleveren: zo'n keten zou per constructie nul
        // rijen matchen (GOVERNED_BY wordt alleen vanaf :Interaction geschreven) —
        // de stille #274-fout. De gevoeligste kandidaat was HAS_TAG: hing Tag als
        // Concept-subklasse in de hiërarchie, dan ontstond hier
        // HAS_TAG ∘ GOVERNED_BY vanzelf. Bewust uitgeschreven literals: de hele
        // ketenverzameling ligt vast, niet alleen "bevat de mechanic-keten".
        var chains = InferenceRuleRegistry.GovernedByChains()
            .Select(c => string.Join("/", c.Select(r => r.EdgeName)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["HAS_DOMAIN/GOVERNED_BY", "HAS_MECHANIC/GOVERNED_BY"],
            chains);
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

    [Theory]
    [InlineData("isa-closure")]                             // huis-vorm: kleine letters + '-'
    [InlineData("pc:has_mechanic-governed_by")]             // ':' '_' '-' zijn veilig
    public void IsSafeRuleId_AccepteertHetVeiligeAlfabet(string id) =>
        Assert.True(InferenceRuleRegistry.IsSafeRuleId(id));

    [Theory]
    [InlineData("bad'; MATCH (x) DETACH DELETE x //")]      // Cypher-injectie via een quote
    [InlineData("has space")]                               // spaties zijn niet veilig
    [InlineData("UpperCase")]                               // hoofdletters buiten het alfabet
    [InlineData("komma,injectie")]                          // komma buiten het alfabet
    [InlineData("regel$met#tekens")]                        // overige leestekens
    [InlineData("")]                                        // leeg matcht '+' niet
    public void IsSafeRuleId_WeigertOnveiligeIds(string id) =>
        Assert.False(InferenceRuleRegistry.IsSafeRuleId(id));

    [Fact]
    public void IsSafeRuleId_WeigertNull() =>
        Assert.False(InferenceRuleRegistry.IsSafeRuleId(null!));

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
