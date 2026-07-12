using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Drift-vergelijking Postgres ↔ Neo4j (#108): pure logica die het
/// kennis-gaten-rapport voedt — een achterlopende graph wordt gemeten, niet
/// geraden.</summary>
public class GraphDriftTests
{
    private static Dictionary<string, int> Counts(params (string Label, int Count)[] pairs) =>
        pairs.ToDictionary(p => p.Label, p => p.Count);

    [Fact]
    public void Compare_InSync_AllDeltasZero()
    {
        var both = Counts(("Card", 300), ("RuleSection", 1200), ("Claim", 40));
        var entries = GraphDrift.Compare(both, both);

        Assert.All(entries, e => Assert.Equal(0, e.Delta));
        Assert.Equal(GraphDrift.Labels.Length, entries.Count);
    }

    [Fact]
    public void Compare_GraphBehind_NegativeDelta()
    {
        var entries = GraphDrift.Compare(
            Counts(("Card", 300), ("Claim", 40)),
            Counts(("Card", 280), ("Claim", 0)));

        var card = entries.Single(e => e.Label == "Card");
        Assert.Equal(300, card.Postgres);
        Assert.Equal(280, card.Graph);
        Assert.Equal(-20, card.Delta);
        Assert.Equal(-40, entries.Single(e => e.Label == "Claim").Delta);
    }

    [Fact]
    public void Compare_EveryKnownLabelGetsARow_EvenWhenBothSidesEmpty()
    {
        var entries = GraphDrift.Compare(
            Counts(("Card", 1)), Counts(("Card", 1)));

        // "Leeg aan beide kanten" is een geldige, zichtbare meting: een
        // knooptype dat nog nergens data heeft mag niet stil verdwijnen.
        Assert.Equal(GraphDrift.Labels, entries.Select(e => e.Label));
        var erratum = entries.Single(e => e.Label == "Erratum");
        Assert.Equal(0, erratum.Postgres);
        Assert.Equal(0, erratum.Graph);
        Assert.Equal(0, erratum.Delta);
    }

    [Fact]
    public void Compare_UnknownGraphLabel_AppendedAsDrift()
    {
        // Een label dat de sync niet kent (schema-experiment, handmatige
        // Cypher) is óók drift en hoort zichtbaar te zijn — achteraan.
        var entries = GraphDrift.Compare(
            Counts(),
            Counts(("Zombie", 7), ("Aap", 2)));

        Assert.Equal(GraphDrift.Labels.Length + 2, entries.Count);
        Assert.Equal(["Aap", "Zombie"], entries.Skip(GraphDrift.Labels.Length).Select(e => e.Label));
        var zombie = entries.Single(e => e.Label == "Zombie");
        Assert.Equal(0, zombie.Postgres);
        Assert.Equal(7, zombie.Delta);
    }

    [Fact]
    public void Compare_KnownLabelsKeepSyncOrder()
    {
        // Presentatievolgorde = volgorde van de sync (kaart-facetten eerst,
        // dan de kennislagen), onafhankelijk van dictionary-volgorde.
        var entries = GraphDrift.Compare(
            Counts(("Change", 5), ("Card", 3)),
            Counts(("Claim", 1), ("Set", 2)));

        Assert.Equal(GraphDrift.Labels, entries.Select(e => e.Label));
    }
}
