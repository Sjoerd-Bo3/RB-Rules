using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Gecommitteerde-keuze-scoring (#158, issue-comment "Verfijning:
/// meerkeuze + objectieve score"): de meerkeuze-opties gaan ALLEEN in de
/// benchmark-prompt mee (deze klasse), nooit in het normale /ask-pad — de
/// composed vraag hieronder is gewoon de `question` die AskService binnenkrijgt,
/// dus AskService zelf verandert niet voor deze opmaak. Een deterministische
/// parser haalt daarna de door de agent gekozen letter uit het antwoord;
/// geen match ⇒ null (geen fout, zie BenchmarkResult.ChosenIndex).</summary>
public static class BenchmarkPrompt
{
    /// <summary>A, B, C, … voor optie-index 0, 1, 2, ….</summary>
    public static char Label(int index) => (char)('A' + index);

    /// <summary>Bouwt de vraagtekst die naar AskService gaat: de oorspronkelijke
    /// vraag plus de meerkeuze-opties en een expliciete instructie om af te
    /// sluiten met een eenduidige keuzeregel. De rest van de pipeline (retrieval,
    /// scheidsrechter-structuur) blijft ongewijzigd — dit is puur tekst die als
    /// `question` meegaat.</summary>
    public static string BuildQuestion(string question, IReadOnlyList<string> options)
    {
        var optionLines = options.Select((o, i) => $"{Label(i)}. {o}");
        return $"""
            {question}

            Dit is een meerkeuzevraag uit de scheidsrechter-benchmark. Opties:
            {string.Join("\n", optionLines)}

            Analyseer de situatie zoals gebruikelijk volgens je scheidsrechter-
            structuur. Sluit daarna af met een aparte, laatste regel EXACT in de
            vorm:
            GEKOZEN OPTIE: <letter>
            waarbij <letter> de letter is (A, B, C, …) van de optie die je kiest.
            Kies altijd precies één letter, ook bij twijfel — motiveer twijfel in
            de Zekerheid-regel, niet door de keuze open te laten.
            """;
    }

    /// <summary>Matcht "GEKOZEN OPTIE" gevolgd door een letter, met optionele
    /// dubbele punt en markdown-asterisken ertussen (het antwoord komt als
    /// markdown terug, bv. "**GEKOZEN OPTIE:** B" of "**GEKOZEN OPTIE: B**").</summary>
    private static readonly Regex ChoiceMarker = new(
        @"GEKOZEN\s+OPTIE\s*\**\s*:?\s*\**\s*([A-Za-z])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>0-based gekozen index, of null zonder eenduidige/valide match —
    /// dat laatste is geen fout (zie BenchmarkResult.ChosenIndex), gewoon een
    /// onscoorbaar antwoord. Bij meerdere treffers telt de laatste (het
    /// antwoord kan de letter eerder noemen tijdens de uitleg; de instructie
    /// vraagt om de keuze als laatste, aparte regel).</summary>
    public static int? ParseChoice(string? answer, int optionCount)
    {
        if (string.IsNullOrWhiteSpace(answer) || optionCount <= 0) return null;
        Match? last = null;
        foreach (Match m in ChoiceMarker.Matches(answer)) last = m;
        if (last is null) return null;
        var index = char.ToUpperInvariant(last.Groups[1].Value[0]) - 'A';
        return index >= 0 && index < optionCount ? index : null;
    }
}
