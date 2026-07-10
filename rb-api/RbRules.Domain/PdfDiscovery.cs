using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Vindt de actuele PDF-URL op de Rules Hub (audit-fix: PDF-URL's
/// wisselen per versie — nooit hardcoden; de hub is de bron van links).</summary>
public static partial class PdfDiscovery
{
    [GeneratedRegex("""href=["']([^"']+\.pdf(?:\?[^"']*)?)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex PdfHref();

    /// <summary>Zoek de eerste .pdf-link waarvan de URL het keyword bevat
    /// (bv. "core" of "tournament"); relatief wordt absoluut gemaakt.</summary>
    public static string? FindPdfUrl(string html, string keyword, Uri baseUri)
    {
        var candidates = PdfHref().Matches(html)
            .Select(m => m.Groups[1].Value)
            .ToList();
        var match = candidates.FirstOrDefault(
                u => u.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);
        if (match is null) return null;
        return Uri.TryCreate(baseUri, match, out var abs) ? abs.ToString() : match;
    }
}
