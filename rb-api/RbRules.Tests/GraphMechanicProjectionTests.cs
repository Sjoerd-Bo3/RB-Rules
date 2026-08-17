using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Regressie bij #249: de brein-mining mag kaart↔eigen-keyword niet meer
/// als <c>Interaction</c> afleiden, maar de kaart↔keyword-STRUCTUUR moet ongewijzigd
/// in de graph blijven bestaan — dat netwerk ís de graph-verkenner. Deze test
/// bewaakt dat de deterministische projectie (uit <c>Card.Mechanics</c>, zonder LLM)
/// intact is en dus niet per ongeluk meesneuvelt met de tautologie-poort.</summary>
public class GraphMechanicProjectionTests
{
    [Fact]
    public void MechanicPairs_ProjecteertElkeKaartMechanicUitHetBronveld()
    {
        var cards = new[]
        {
            new Card
            {
                RiftboundId = "ogn-001", Name = "Cloth Armor",
                Mechanics = ["Quick-Draw", "Reaction", "Equip"],
            },
            new Card { RiftboundId = "ogn-002", Name = "Shieldbearer", Mechanics = ["Tank"] },
            new Card { RiftboundId = "ogn-003", Name = "Vanilla", Mechanics = null },
        };

        var pairs = GraphSyncService.MechanicPairs(cards)
            .Cast<Dictionary<string, object?>>()
            .Select(p => ((string?)p["id"], (string?)p["value"], (string?)p["ref"]))
            .ToList();

        Assert.Equal(4, pairs.Count);   // 3 + 1 + 0 (geen mechanics = geen edge)
        Assert.Contains(("ogn-001", "Equip", "mechanic:Equip"), pairs);
        Assert.Contains(("ogn-001", "Quick-Draw", "mechanic:Quick-Draw"), pairs);
        Assert.Contains(("ogn-002", "Tank", "mechanic:Tank"), pairs);
    }

    [Fact]
    public void MechanicPairs_GebruiktDezelfdeRefVormAlsDeMining()
    {
        // De mining verwijst naar mechanic:{label}; de graph-projectie moet exact
        // dezelfde ref-vorm gebruiken, anders vallen de twee lagen uit elkaar.
        var cards = new[]
        {
            new Card { RiftboundId = "ogn-001", Name = "Test", Mechanics = ["Deflect"] },
        };

        var pair = Assert.Single(GraphSyncService.MechanicPairs(cards))
            as Dictionary<string, object?>;

        Assert.Equal(BrainRef.Mechanic("Deflect").Format(), (string?)pair!["ref"]);
    }
}
