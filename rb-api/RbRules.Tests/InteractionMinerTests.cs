using RbRules.Domain;

namespace RbRules.Tests;

public class InteractionMinerTests
{
    private static Card Make(string id, string name,
        string[]? mechanics = null, string[]? triggers = null, string[]? effects = null) => new()
    {
        RiftboundId = id, Name = name, TextPlain = $"{name} tekst",
        Mechanics = mechanics ?? [], Triggers = triggers ?? [], Effects = effects ?? [],
    };

    [Fact]
    public void FindCandidates_MatchesEffectToTrigger()
    {
        var killer = Make("a", "Killer", effects: ["kill a unit"]);
        var listener = Make("b", "Avenger", triggers: ["when a unit dies"]);
        var candidates = InteractionMiner.FindCandidates([killer, listener]);
        // 'kill'/'unit' ↔ 'unit'/'dies' delen het woord 'unit'
        Assert.Contains(candidates, c =>
            (c.A.RiftboundId, c.B.RiftboundId) is ("a", "b") or ("b", "a"));
    }

    [Fact]
    public void FindCandidates_MatchesSharedSpecificMechanic()
    {
        var a = Make("a", "Sluiper", mechanics: ["Hidden"]);
        var b = Make("b", "Spion", mechanics: ["Hidden"]);
        var candidates = InteractionMiner.FindCandidates([a, b]);
        Assert.Contains(candidates, c => c.Reason.Contains("Hidden"));
    }

    [Fact]
    public void FindCandidates_SkipsOverlyCommonMechanics()
    {
        // 13 kaarten met dezelfde mechanic → te generiek, geen paren.
        var cards = Enumerable.Range(0, 13)
            .Select(i => Make($"c{i}", $"Kaart {i}", mechanics: ["Accelerate"]))
            .ToList();
        var candidates = InteractionMiner.FindCandidates(cards);
        Assert.DoesNotContain(candidates, c => c.Reason.Contains("Accelerate"));
    }

    [Fact]
    public void FindCandidates_DedupsAndRespectsMax()
    {
        var a = Make("a", "A", mechanics: ["Hidden"], effects: ["kill a unit"]);
        var b = Make("b", "B", mechanics: ["Hidden"], triggers: ["when a unit dies"]);
        var candidates = InteractionMiner.FindCandidates([a, b], max: 10);
        Assert.Single(candidates); // één paar, ook al matchen ze op twee manieren
    }

    [Fact]
    public void TriggerKeywords_DropsStopwords()
    {
        var words = InteractionMiner.TriggerKeywords("when a unit dies").ToList();
        Assert.Contains("unit", words);
        Assert.Contains("dies", words);
        Assert.DoesNotContain("when", words);
    }

    [Fact]
    public void ParseVerified_KeepsOnlyInteractingPairs()
    {
        var verified = InteractionMiner.ParseVerified("""
            [{"a": "x", "b": "y", "interacts": true, "kind": "combo", "explanation": "Keten."},
             {"a": "x", "b": "z", "interacts": false, "kind": "synergy", "explanation": "Niets."}]
            """);
        Assert.Single(verified);
        Assert.Equal("combo", verified[0].Kind);
    }

    [Fact]
    public void ParseVerified_ClampsUnknownKind()
    {
        var verified = InteractionMiner.ParseVerified(
            """[{"a": "x", "b": "y", "interacts": true, "kind": "mega", "explanation": "?"}]""");
        Assert.Equal("synergy", verified[0].Kind);
    }
}
