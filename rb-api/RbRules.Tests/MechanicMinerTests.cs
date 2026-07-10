using RbRules.Domain;

namespace RbRules.Tests;

public class MechanicMinerTests
{
    [Fact]
    public void ParseBatch_ExtractsJsonArrayFromNoisyResponse()
    {
        var raw = """
            Hier is de analyse:
            [{"id": "ogn-1", "mechanics": ["Tank"], "triggers": ["when I conquer"], "effects": ["buff might"]},
             {"id": "ogn-2", "mechanics": [], "triggers": [], "effects": ["draw a card"]}]
            """;
        var result = MechanicMiner.ParseBatch(raw);
        Assert.Equal(2, result.Count);
        Assert.Equal(["Tank"], result[0].Mechanics);
        Assert.Equal(["when I conquer"], result[0].Triggers);
        Assert.Empty(result[1].Mechanics);
        Assert.Equal(["draw a card"], result[1].Effects);
    }

    [Fact]
    public void ParseBatch_NormalizesVocabularyCasing()
    {
        var result = MechanicMiner.ParseBatch(
            """[{"id": "x", "mechanics": ["deathknell", "TANK", "Splinter"], "triggers": [], "effects": []}]""");
        Assert.Equal(["Deathknell", "Tank", "Splinter"], result[0].Mechanics);
    }

    [Fact]
    public void ParseBatch_SkipsItemsWithoutId()
    {
        var result = MechanicMiner.ParseBatch(
            """[{"mechanics": ["Tank"]}, {"id": "ok", "mechanics": []}]""");
        Assert.Single(result);
        Assert.Equal("ok", result[0].Id);
    }

    [Fact]
    public void ParseBatch_ReturnsEmptyOnGarbage() =>
        Assert.Empty(MechanicMiner.ParseBatch("geen json"));

    [Fact]
    public void BuildPrompt_IncludesIdNameAndText()
    {
        var prompt = MechanicMiner.BuildPrompt([
            new Card { RiftboundId = "ogn-1", Name = "Adaptatron", Type = "Unit", TextPlain = "Tank. When I conquer, buff me." },
        ]);
        Assert.Contains("ogn-1", prompt);
        Assert.Contains("Adaptatron", prompt);
        Assert.Contains("Tank. When I conquer", prompt);
    }

    [Fact]
    public void SystemPrompt_ContainsVocabulary() =>
        Assert.Contains("Accelerate", MechanicMiner.GetSystemPrompt());
}
