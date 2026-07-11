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

    /// <summary>Audit-fix: strip boilerplate (nav/header/footer/aside) vóór
    /// hash/diff, zodat menu-wijzigingen geen spurious change-events geven.</summary>
    public static string StripBoilerplate(string html)
    {
        var text = html;
        foreach (var tag in new[] { "nav", "header", "footer", "aside" })
            text = Regex.Replace(text, $@"<{tag}[\s\S]*?</{tag}>", " ", RegexOptions.IgnoreCase);
        return text;
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

    /// <summary>Zoekresultaat-snippet (#72): de eerste ~max tekens, afgekapt
    /// op een woordgrens waar dat kan, met ellipsis als er tekst wegvalt.
    /// Bewust buiten de EF-projectie gehouden — eigen methodes vertalen niet
    /// in expression trees, dus de aanroeper materialiseert eerst.</summary>
    public static string Snippet(string text, int max = 180)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= max) return trimmed;
        var cut = trimmed[..max];
        var lastSpace = cut.LastIndexOf(' ');
        // Alleen op een woordgrens breken als dat geen half snippet oplevert
        // (codes zonder spaties, zoals lange URL's, worden hard afgekapt).
        if (lastSpace > max / 2) cut = cut[..lastSpace];
        return cut.TrimEnd() + "…";
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
