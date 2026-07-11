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

    // ── Groeiend vocabulaire (#52) ──────────────────────────────────────

    [Fact]
    public void Vocabulary_MergesAcceptedKeywords_SeedSpellingWins()
    {
        var vocab = MechanicMiner.Vocabulary(["Ganking", "tank", "  ", "Ganking"]);
        Assert.Contains("Ganking", vocab);
        // "tank" bestaat al als seed-term "Tank" — geen duplicaat erbij.
        Assert.Single(vocab, v => v.Equals("Tank", StringComparison.OrdinalIgnoreCase));
        Assert.Single(vocab, v => v == "Ganking");
    }

    [Fact]
    public void GetSystemPrompt_IncludesAcceptedKeywords() =>
        Assert.Contains("Ganking", MechanicMiner.GetSystemPrompt(["Ganking"]));

    [Fact]
    public void ParseBatch_NormalizesAgainstAcceptedKeywords()
    {
        var result = MechanicMiner.ParseBatch(
            """[{"id": "x", "mechanics": ["ganking"], "triggers": [], "effects": []}]""",
            ["Ganking"]);
        Assert.Equal(["Ganking"], result[0].Mechanics);
    }

    [Fact]
    public void ExtractKeywordCandidates_FindsBracketedTermsOutsideVocabulary()
    {
        // Echte tekstvorm uit de Riftcodex-API (text.plain).
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[Ganking] (May move when a showdown starts.) [Action] Deal 4.",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Ganking"], candidates); // Action zit al in het vocabulaire
    }

    [Fact]
    public void ExtractKeywordCandidates_StripsNumericParameterAndDedupes()
    {
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[Assault 2] here, [Assault] there, [Hunt 2] everywhere.",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Assault", "Hunt"], candidates);
    }

    [Fact]
    public void ExtractKeywordCandidates_IgnoresIconArrowsAndPlaceholderNoise()
    {
        // "[&gt;]" is de ge-escapete pijl-icoon, "[NO TEXT]" een placeholder;
        // vocab-termen tellen case-insensitive niet mee als kandidaat.
        var candidates = MechanicMiner.ExtractKeywordCandidates(
            "[&gt;] exhaust me. [NO TEXT] [Level 6] [TANK] [Quick-Draw]",
            MechanicMiner.SeedVocabulary);
        Assert.Equal(["Level", "Quick-Draw"], candidates);
    }

    [Fact]
    public void ExtractKeywordCandidates_EmptyForEmptyText()
    {
        Assert.Empty(MechanicMiner.ExtractKeywordCandidates(null, MechanicMiner.SeedVocabulary));
        Assert.Empty(MechanicMiner.ExtractKeywordCandidates("  ", MechanicMiner.SeedVocabulary));
    }
}
