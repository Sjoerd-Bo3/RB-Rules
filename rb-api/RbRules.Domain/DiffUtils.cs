using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Zin-gebaseerde diff, geport uit de PoP. Bewust simpel: toont
/// toegevoegde/verwijderde zinnen als leesbaar signaal voor de AI-classifier
/// en de feed. (Sectie-gealigneerde diff komt in S2 met de sectie-boom.)</summary>
public static partial class DiffUtils
{
    private const int MaxItems = 40;

    public static string LineDiff(string oldText, string newText)
    {
        var a = Sentences(oldText);
        var b = Sentences(newText);
        var added = b.Except(a).Take(MaxItems).ToList();
        var removed = a.Except(b).Take(MaxItems).ToList();

        var sb = new StringBuilder();
        if (added.Count > 0)
        {
            sb.AppendLine("+ toegevoegd:");
            foreach (var s in added) sb.AppendLine($"  {s}");
        }
        if (removed.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("- verwijderd:");
            foreach (var s in removed) sb.AppendLine($"  {s}");
        }
        return sb.ToString().TrimEnd();
    }

    private static HashSet<string> Sentences(string text) =>
        SentenceSplit().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToHashSet();

    [GeneratedRegex(@"(?<=\.)\s+")]
    private static partial Regex SentenceSplit();
}
