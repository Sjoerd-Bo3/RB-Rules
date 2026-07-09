using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Tekst-hulpfuncties, geport uit de PoP (src/lib/text.ts) met tests.</summary>
public static partial class TextUtils
{
    public static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Eenvoudige HTML→tekst: strip scripts/styles/tags, decodeer de
    /// gangbare entities, normaliseer whitespace.</summary>
    public static string HtmlToText(string html)
    {
        var text = ScriptRegex().Replace(html, " ");
        text = StyleRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = text
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
