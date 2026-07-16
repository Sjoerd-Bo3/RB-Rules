using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>#188 increment 2: bron-type-classificatie via rb-ai i.p.v. de
/// keyword-heuristiek. Puur/getest, zelfde tolerantie-patroon als
/// ClarificationInformativenessJudgeTests (increment 1) — met name de
/// objectvorm-guard-regressie (array-blokken mogen nooit crashen).</summary>
public class SourceContentKindParseTests
{
    [Theory]
    [InlineData("""{"kind": "faq"}""", "faq")]
    [InlineData("""{"kind": "patch-notes"}""", "patch-notes")]
    [InlineData("""{"kind": "other"}""", "other")]
    // Hoofdletterongevoelig — een LLM antwoordt niet altijd exact lowercase.
    [InlineData("""{"kind": "FAQ"}""", "faq")]
    [InlineData("""{"kind": "Patch-Notes"}""", "patch-notes")]
    public void Parse_GeldigeKind_ReturnsKind(string raw, string expected) =>
        Assert.Equal(expected, SourceContentKind.Parse(raw));

    [Fact]
    public void Parse_ToleratesSurroundingProse()
    {
        var raw = "Mijn oordeel:\n{\"kind\": \"faq\"}\nKlaar.";
        Assert.Equal("faq", SourceContentKind.Parse(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("geen bruikbare JSON hier")]
    [InlineData("{kapotte json}")]
    [InlineData("""{"iets_anders": "faq"}""")]
    [InlineData("""{"kind": "faq-en-patch-notes"}""")] // onbekende waarde
    [InlineData("""{"kind": 1}""")] // geen string
    // Review-regressie (#188 increment 1): array-vormige kandidaten mogen
    // niet crashen — TryGetProperty op een niet-object gooit een
    // InvalidOperationException (geen JsonException). Zonder de
    // objectvorm-guard zou de scan 500'en i.p.v. te degraderen naar de
    // heuristiek.
    [InlineData("[true]")]
    [InlineData("[1, 2]")]
    [InlineData("This source refers to section [402.3] of the updated rules.")]
    [InlineData("Kind: faq, see reference [1].")]
    public void Parse_OnbruikbaarAntwoord_ReturnsNull(string raw) =>
        Assert.Null(SourceContentKind.Parse(raw));

    [Fact]
    public void BuildPrompt_BevatNaamUrlEnInhoud() =>
        Assert.Multiple(
            () => Assert.Contains("Unleashed Rules FAQ", SourceContentKind.BuildPrompt(
                "Unleashed Rules FAQ", "https://example.com/faq", "Korte inhoud.")),
            () => Assert.Contains("https://example.com/faq", SourceContentKind.BuildPrompt(
                "Unleashed Rules FAQ", "https://example.com/faq", "Korte inhoud.")),
            () => Assert.Contains("Korte inhoud.", SourceContentKind.BuildPrompt(
                "Unleashed Rules FAQ", "https://example.com/faq", "Korte inhoud.")));

    [Fact]
    public void BuildPrompt_LangeInhoud_KaptAfOp1500Tekens()
    {
        var content = new string('x', 3000);
        var prompt = SourceContentKind.BuildPrompt("Naam", "https://example.com", content);

        // Alleen het eerste deel van de inhoud gaat mee — de rest van de
        // pagina is niet nodig om het bron-type te herkennen.
        Assert.DoesNotContain(new string('x', 1501), prompt);
        Assert.Contains(new string('x', 1500), prompt);
    }
}

/// <summary>De heuristiek is sinds #188 increment 2 nog alleen het
/// deterministische vangnet (ClarificationSources herverpakt tot dezelfde
/// drieledige kind-waarde) en de transitionele null-fallback via Resolve.</summary>
public class SourceContentKindHeuristicTests
{
    [Fact]
    public void HeuristicKind_FaqSignaal_ReturnsFaq() =>
        Assert.Equal(SourceContentKind.Faq,
            SourceContentKind.HeuristicKind("id", "https://example.com/unleashed-rules-faq", null));

    [Fact]
    public void HeuristicKind_PatchNotesSignaal_ReturnsPatchNotes() =>
        Assert.Equal(SourceContentKind.PatchNotes,
            SourceContentKind.HeuristicKind("core-rules-patch-notes", null, null));

    [Fact]
    public void HeuristicKind_GeenSignaal_ReturnsOther() =>
        Assert.Equal(SourceContentKind.Other,
            SourceContentKind.HeuristicKind("s1", "https://example.com/regels", "Core Rules"));

    [Fact]
    public void HeuristicKind_DubbelSignaal_PatchNotesWint() =>
        // #185-principe: bevat een naam/URL zowel het FAQ- als het
        // patch-notes-woord, dan telt de bron als patch-notes.
        Assert.Equal(SourceContentKind.PatchNotes,
            SourceContentKind.HeuristicKind(
                "rules-faq-and-patch-notes",
                "https://example.com/rules-faq-and-patch-notes", "Rules FAQ and Patch Notes"));

    [Fact]
    public void Resolve_GepersisteerdeKind_WintVanHeuristiek() =>
        // Een LLM-classificatie "other" overschrijft wat de heuristiek zou
        // zeggen (de URL bevat "faq") — het hele punt van #188 increment 2.
        Assert.Equal(SourceContentKind.Other,
            SourceContentKind.Resolve(SourceContentKind.Other, "id", "https://example.com/faq", null));

    [Fact]
    public void Resolve_NullContentKind_ValtTerugOpHeuristiek() =>
        Assert.Equal(SourceContentKind.Faq,
            SourceContentKind.Resolve(null, "id", "https://example.com/unleashed-rules-faq", null));

    [Fact]
    public void Resolve_LlmKanBronZonderTrefwoordAlsFaqHerkennen() =>
        // De heuristiek zou hier "other" zeggen (geen "faq"/"clarification"-
        // woord in de slug) — de gepersisteerde LLM-kind wint alsnog.
        Assert.Equal(SourceContentKind.Faq,
            SourceContentKind.Resolve(SourceContentKind.Faq, "s1", "https://example.com/legion-uitleg", "Legion uitgelegd"));
}
