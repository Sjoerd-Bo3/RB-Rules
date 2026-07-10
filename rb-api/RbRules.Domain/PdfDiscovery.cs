using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Vindt de actuele PDF-URL op de Rules Hub (audit-fix: PDF-URL's
/// wisselen per versie — nooit hardcoden; de hub is de bron van links).
/// Riot serveert de PDF's als anonieme CDN-hashes, dus het keyword wordt
/// gematcht op de URL ÓF de linktekst ("Core Rules", "Tournament Rules").</summary>
public static partial class PdfDiscovery
{
    [GeneratedRegex("""<a[^>]*href=["']([^"']+\.pdf(?:\?[^"']*)?)["'][^>]*>([\s\S]*?)</a>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex PdfAnchor();

    [GeneratedRegex("""href=["']([^"']+\.pdf(?:\?[^"']*)?)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex PdfHref();

    public static string? FindPdfUrl(string html, string keyword, Uri baseUri)
    {
        // 1. Volledige ankers: match keyword op URL of (tag-gestripte) linktekst.
        var anchors = PdfAnchor().Matches(html)
            .Select(m => (Url: m.Groups[1].Value,
                          Text: Regex.Replace(m.Groups[2].Value, "<[^>]+>", " ")))
            .ToList();
        var match = anchors.FirstOrDefault(a =>
            a.Url.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            a.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Url;

        // 2. Terugval: kale hrefs op URL-keyword, of één enkele kandidaat.
        if (match is null)
        {
            var candidates = PdfHref().Matches(html).Select(m => m.Groups[1].Value).ToList();
            match = candidates.FirstOrDefault(
                    u => u.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Count == 1 ? candidates[0] : null);
        }

        if (match is null) return null;
        return Uri.TryCreate(baseUri, match, out var abs) ? abs.ToString() : match;
    }
}
