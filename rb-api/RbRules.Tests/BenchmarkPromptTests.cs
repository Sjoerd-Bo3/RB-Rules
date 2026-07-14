using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Gecommitteerde-keuze-scoring (#158): BuildQuestion is de enige
/// plek waar de meerkeuze-opties in een vraagtekst komen (nooit in het
/// normale /ask-pad — zie AskService.AskOptions); ParseChoice is de
/// deterministische letter-parser die de score bepaalt. Randgevallen hier
/// zijn de "letter-parser-randgevallen" uit de opdracht.</summary>
public class BenchmarkPromptTests
{
    private static readonly string[] Options = ["Alpha", "Bravo", "Charlie", "Delta"];

    [Fact]
    public void Label_ZeroBased_GeeftLetters()
    {
        Assert.Equal('A', BenchmarkPrompt.Label(0));
        Assert.Equal('B', BenchmarkPrompt.Label(1));
        Assert.Equal('D', BenchmarkPrompt.Label(3));
    }

    [Fact]
    public void BuildQuestion_BevatVraagEnAlleOpties()
    {
        var q = BenchmarkPrompt.BuildQuestion("Wat gebeurt er?", Options);

        Assert.Contains("Wat gebeurt er?", q);
        Assert.Contains("A. Alpha", q);
        Assert.Contains("B. Bravo", q);
        Assert.Contains("C. Charlie", q);
        Assert.Contains("D. Delta", q);
        Assert.Contains("GEKOZEN OPTIE", q);
    }

    [Theory]
    [InlineData("Uitleg uitleg.\n\nGEKOZEN OPTIE: B", 1)]
    [InlineData("Uitleg uitleg.\n\ngekozen optie: b", 1)]
    [InlineData("Uitleg.\n\n**GEKOZEN OPTIE:** B", 1)]
    [InlineData("Uitleg.\n\n**GEKOZEN OPTIE: B**", 1)]
    [InlineData("Uitleg.\n\nGEKOZEN OPTIE B", 1)]
    [InlineData("Uitleg.\n\nGEKOZEN OPTIE: B.", 1)]
    [InlineData("Uitleg.\n\nGEKOZEN OPTIE: B)", 1)]
    [InlineData("Uitleg.\n\nGEKOZEN OPTIE:D", 3)]
    public void ParseChoice_HerkentDeCommitteerdeKeuze_InDiverseOpmaken(string answer, int expected)
    {
        Assert.Equal(expected, BenchmarkPrompt.ParseChoice(answer, Options.Length));
    }

    [Fact]
    public void ParseChoice_MeerdereTreffers_NeemtDeLaatste()
    {
        // De uitleg noemt "optie B" onderweg, maar de agent committeert zich
        // pas op de laatste regel aan C — die telt.
        const string answer =
            "Optie B lijkt eerst logisch, maar bij nader inzien...\n\nGEKOZEN OPTIE: C";
        Assert.Equal(2, BenchmarkPrompt.ParseChoice(answer, Options.Length));
    }

    [Fact]
    public void ParseChoice_TweeGekozenOptieRegels_NeemtDeLaatste()
    {
        // De agent noemt de marker twee keer (bv. na zelf-correctie) — de
        // laatste, definitieve regel wint.
        const string answer = "GEKOZEN OPTIE: A\n\nToch nog even nagedacht.\n\nGEKOZEN OPTIE: D";
        Assert.Equal(3, BenchmarkPrompt.ParseChoice(answer, Options.Length));
    }

    [Fact]
    public void ParseChoice_ZonderMarker_GeeftNull()
    {
        Assert.Null(BenchmarkPrompt.ParseChoice("Een antwoord zonder gecommitteerde keuze.", Options.Length));
    }

    [Fact]
    public void ParseChoice_LetterBuitenBereik_GeeftNull()
    {
        // Options.Length = 4 (A t/m D) — E bestaat niet.
        Assert.Null(BenchmarkPrompt.ParseChoice("GEKOZEN OPTIE: E", Options.Length));
    }

    [Fact]
    public void ParseChoice_LegeOfNullTekst_GeeftNull()
    {
        Assert.Null(BenchmarkPrompt.ParseChoice(null, Options.Length));
        Assert.Null(BenchmarkPrompt.ParseChoice("", Options.Length));
        Assert.Null(BenchmarkPrompt.ParseChoice("   ", Options.Length));
    }

    [Fact]
    public void ParseChoice_ZonderOpties_GeeftNull()
    {
        Assert.Null(BenchmarkPrompt.ParseChoice("GEKOZEN OPTIE: A", optionCount: 0));
    }
}
