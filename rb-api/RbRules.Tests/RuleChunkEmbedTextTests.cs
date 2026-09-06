using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Contextuele embed-invoer voor regelchunks (#385): sectiecode, bron
/// en de eerste zin van de hoogste en dichtstbijzijnde ouder vóór de tekst.
/// De opgeslagen tekst zelf verandert nooit — dit is alleen de invoer.</summary>
public class RuleChunkEmbedTextTests
{
    [Fact]
    public void Build_ZetCodeBronEnOuderZinnenVoorDeTekst()
    {
        var s = RuleChunkEmbedText.Build("466.2.c", "Core Rules",
            [("466", "Combat. Combat is the phase in which units fight."),
             ("466.2", "Blocking. The defending player may declare blockers.")],
            "A blocker must be ready.");

        Assert.Equal(
            "§ 466.2.c (Core Rules) — Combat. — Blocking. — A blocker must be ready.", s);
    }

    [Fact]
    public void Build_EenOuder_KomtEenKeer_EnZonderCodeAlleenDeBron()
    {
        Assert.Equal(
            "§ 466.2 (Core Rules) — Combat. — Blocking rules.",
            RuleChunkEmbedText.Build("466.2", "Core Rules", [("466", "Combat.")], "Blocking rules."));
        Assert.Equal(
            "§ 466 (Core Rules) — Combat overview.",
            RuleChunkEmbedText.Build("466", "Core Rules", [], "Combat overview."));
        Assert.Equal(
            "(Core Rules) — Introduction text.",
            RuleChunkEmbedText.Build(null, "Core Rules", [], "Introduction text."));
    }

    [Fact]
    public void Build_GelijkeOuderZinnen_NietDubbel()
    {
        var s = RuleChunkEmbedText.Build("1.2.3", "Core Rules",
            [("1", "Same heading."), ("1.2", "Same heading.")], "Leaf.");
        Assert.Equal("§ 1.2.3 (Core Rules) — Same heading. — Leaf.", s);
    }

    [Fact]
    public void FirstSentence_StoptOpZinseinde_NietOpSectienummer()
    {
        Assert.Equal("See 601.2 for details.",
            RuleChunkEmbedText.FirstSentence("See 601.2 for details. More text follows."));
        Assert.Equal("Heading line", RuleChunkEmbedText.FirstSentence("Heading line\nBody text."));
        Assert.Equal("", RuleChunkEmbedText.FirstSentence("   "));
        Assert.Equal("", RuleChunkEmbedText.FirstSentence(null));
    }

    [Fact]
    public void FirstSentence_BegrensdOpWoordgrens_MetLiteral()
    {
        // 160 = MaxParentSentence, uitgeschreven (#286-les).
        var lang = string.Join(" ", Enumerable.Repeat("woord", 60)); // 359 tekens, geen zinseinde
        var s = RuleChunkEmbedText.FirstSentence(lang);
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 161, $"lengte {s.Length}");
        Assert.DoesNotContain("woor…", s); // op woordgrens geknipt
    }

    [Fact]
    public void ParentsFor_SlaatOntbrekendeTussenniveausOver()
    {
        var byCode = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["466"] = "Combat.", ["466.2.c"] = "leaf",
        };
        var parents = RuleChunkEmbedText.ParentsFor("466.2.c", byCode);
        Assert.Equal([("466", "Combat.")], parents);
        Assert.Empty(RuleChunkEmbedText.ParentsFor(null, byCode));
        Assert.Empty(RuleChunkEmbedText.ParentsFor("999", byCode));
    }

    [Fact]
    public void Variant_IsEen_TotDeVormWijzigt()
    {
        // Regressiewachter: wie Build() wijzigt, moet dit getal bumpen, anders
        // vindt de her-embed-job de oude rijen niet.
        Assert.Equal(1, RuleChunkEmbedText.Variant);
    }
}
