using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

public record ParsedSection(string Code, string Text);

/// <summary>Splitst geëxtraheerde regeltekst (Core/Tournament Rules) in
/// genummerde secties: sectie = chunk-eenheid (audit-fix: geen 900-tekens-
/// vensters meer die dwars door secties lopen; geen regex-op-toeval).
/// Herkent koppen als "601.", "601.2.", "601.2d.", "601.2.d." aan regelbegin.</summary>
public static partial class RuleSectionParser
{
    // Sectiekop aan regelbegin: 1-4 cijfers, dan .sub(-letters), afgesloten
    // met punt of spatie. "601.2.d. Tekst…" → code "601.2.d".
    [GeneratedRegex(@"^\s*(\d{1,4}(?:\.\d+)*(?:\.?[a-z])?)[.\s]\s*(?=\S)", RegexOptions.Multiline)]
    private static partial Regex SectionHeader();

    /// <summary>Maximale sectiegrootte; grotere secties worden gesplitst met
    /// het sectienummer behouden (deel-suffix niet nodig voor citaten).</summary>
    private const int MaxSectionLength = 2400;

    public static IReadOnlyList<ParsedSection> Parse(string text)
    {
        var matches = SectionHeader().Matches(text);
        if (matches.Count == 0)
            return SplitPlain(text);

        var sections = new List<ParsedSection>();

        // Preamble vóór de eerste sectiekop (titel, inhoudsopgave-restanten).
        var first = matches[0].Index;
        if (first > 0)
        {
            var pre = Clean(text[..first]);
            if (pre.Length > 40) sections.AddRange(SplitLong("intro", pre));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var code = NormalizeCode(matches[i].Groups[1].Value);
            var body = Clean(text[start..end]);
            if (body.Length == 0) continue;
            sections.AddRange(SplitLong(code, body));
        }
        return sections;
    }

    /// <summary>"601.2d" en "601.2.d" normaliseren naar "601.2.d".</summary>
    public static string NormalizeCode(string code)
    {
        var m = Regex.Match(code, @"^(.*?)\.?([a-z])$");
        if (!m.Success) return code;
        var head = m.Groups[1].Value.TrimEnd('.');
        return $"{head}.{m.Groups[2].Value}";
    }

    private static IEnumerable<ParsedSection> SplitLong(string code, string body)
    {
        if (body.Length <= MaxSectionLength)
        {
            yield return new ParsedSection(code, body);
            yield break;
        }
        // Splits op zinsgrens; zelfde sectienummer op elk deel.
        var sb = new StringBuilder();
        foreach (var sentence in Regex.Split(body, @"(?<=\.)\s+"))
        {
            if (sb.Length + sentence.Length > MaxSectionLength && sb.Length > 0)
            {
                yield return new ParsedSection(code, sb.ToString().Trim());
                sb.Clear();
            }
            sb.Append(sentence).Append(' ');
        }
        if (sb.Length > 0) yield return new ParsedSection(code, sb.ToString().Trim());
    }

    private static List<ParsedSection> SplitPlain(string text)
    {
        var clean = Clean(text);
        return clean.Length == 0 ? [] : [.. SplitLong("", clean).Select(
            s => new ParsedSection("", s.Text))];
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
