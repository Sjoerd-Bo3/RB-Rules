using RbRules.Domain;

namespace RbRules.Tests;

public class GraphOntologyTests
{
    [Theory]
    [InlineData(EdgeType.HAS_MECHANIC, NodeType.Card, NodeType.Mechanic, true)]
    [InlineData(EdgeType.PARENT_OF, NodeType.RuleSection, NodeType.RuleSection, true)]
    [InlineData(EdgeType.GOVERNED_BY, NodeType.Card, NodeType.RuleSection, true)]
    [InlineData(EdgeType.BANS, NodeType.BanEntry, NodeType.Card, true)]
    // Omgekeerde richting of verkeerd type mag niet in de graaf terechtkomen.
    [InlineData(EdgeType.HAS_MECHANIC, NodeType.Mechanic, NodeType.Card, false)]
    [InlineData(EdgeType.PARENT_OF, NodeType.Card, NodeType.Card, false)]
    [InlineData(EdgeType.EXPLAINS, NodeType.Card, NodeType.RuleSection, false)]
    public void IsValid_HandhaaftRichtingEnTypen(
        EdgeType type, NodeType from, NodeType to, bool expected) =>
        Assert.Equal(expected, GraphOntology.IsValid(type, from, to));

    [Fact]
    public void Edges_DekkenElkRelatietype()
    {
        // Elk gedeclareerd relatietype hoort in de ontologie te staan; anders
        // bestaat er een enum-waarde waar de sync niets mee kan.
        foreach (var type in Enum.GetValues<EdgeType>())
            Assert.Contains(GraphOntology.Edges, e => e.Type == type);
    }

    [Fact]
    public void Edges_HebbenEenUitlegEnUniekeCombinatie()
    {
        Assert.All(GraphOntology.Edges, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
        var keys = GraphOntology.Edges.Select(e => (e.Type, e.From, e.To)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void GovernedBy_IsAfgeleid_DeRestNiet()
    {
        Assert.True(GraphOntology.IsInferred(EdgeType.GOVERNED_BY));
        Assert.False(GraphOntology.IsInferred(EdgeType.HAS_MECHANIC));
    }

    [Theory]
    [InlineData(NodeType.Card, "id")]
    [InlineData(NodeType.Mechanic, "name")]
    [InlineData(NodeType.RuleSection, "code")]
    public void KeyProperty_KomtOvereenMetDeConstraints(NodeType type, string expected) =>
        Assert.Equal(expected, GraphOntology.KeyProperty(type));
}
