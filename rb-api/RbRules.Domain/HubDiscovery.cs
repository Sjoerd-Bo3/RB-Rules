using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eén per-set-artikel op de Rules Hub: patch notes of errata.</summary>
public record HubSetPage(string Url, string Title, string Kind);

/// <summary>Vindt per-set-artikelen ("… Patch Notes", "… Errata") op de
/// Rules Hub-indexpagina (#94) — zelfde patroon als PdfDiscovery: ankertekst-
/// matching, puur en unit-testbaar. Bij een nieuwe set (Vendetta) verschijnen
/// daar nieuwe links; wat nog niet in het register staat wordt een
/// bronvoorstel. Het resultaat is gededupliceerd en gesorteerd: de hub
/// wisselt per request de linkvolgorde (flip-flop) en dat mag geen verschil
/// in uitkomst of run_log-ruis geven.</summary>
public static partial class HubDiscovery
{
    public const string KindPatchNotes = "patch notes";
    public const string KindErrata = "errata";

    /// <summary>Linklabels zijn kort; langere ankerteksten zijn artikel-
    /// teasers of nav-blokken en geen per-set-pagina-verwijzing.</summary>
    private const int MaxTitleLength = 80;

    [GeneratedRegex("""<a[^>]*href=["']([^"']+)["'][^>]*>([\s\S]*?)</a>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();

    public static IReadOnlyList<HubSetPage> FindSetPages(string html, Uri baseUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<HubSetPage>();
        foreach (Match m in Anchor().Matches(html))
        {
            var title = Regex.Replace(m.Groups[2].Value, "<[^>]+>", " ");
            title = Regex.Replace(title, @"\s+", " ").Trim();
            var kind = KindFor(title);
            if (kind is null) continue;

            if (!Uri.TryCreate(baseUri, m.Groups[1].Value, out var abs)
                || abs.Scheme != Uri.UriSchemeHttps) continue;
            // PDF-links ("Core Rules") zijn PdfDiscovery-terrein, geen artikel.
            if (abs.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            var url = CanonicalUrl(abs);
            if (!seen.Add(ComparisonKey(url))) continue;
            pages.Add(new HubSetPage(url, title, kind));
        }
        // Deterministische volgorde, wat de hub ook per request husselt.
        return [.. pages.OrderBy(p => p.Url, StringComparer.Ordinal)];
    }

    private static string? KindFor(string title)
    {
        if (title.Length == 0 || title.Length > MaxTitleLength) return null;
        if (title.Contains("patch notes", StringComparison.OrdinalIgnoreCase))
            return KindPatchNotes;
        if (title.Contains("errata", StringComparison.OrdinalIgnoreCase))
            return KindErrata;
        return null;
    }

    /// <summary>De hub linkt (deels) via het legacy-domein
    /// riftbound.leagueoflegends.com; dat 301't pad-behoudend naar het
    /// canonieke playriftbound.com (geverifieerd 2026-07-11). Canoniek maken
    /// voorkomt dubbelingen tegen register-entries op het canonieke domein;
    /// fragmenten zijn navigatie-ruis en vallen weg.</summary>
    public static string CanonicalUrl(Uri url)
    {
        var b = new UriBuilder(url) { Fragment = string.Empty };
        if (b.Host.Equals("riftbound.leagueoflegends.com", StringComparison.OrdinalIgnoreCase))
            b.Host = "playriftbound.com";
        return b.Uri.ToString();
    }

    /// <summary>Vergelijkingssleutel tegen register-URL's en eerdere
    /// voorstellen: canoniek domein + zonder trailing slash, te gebruiken in
    /// een OrdinalIgnoreCase-set (zelfde dedupe-vorm als SourceScout).</summary>
    public static string ComparisonKey(string url) =>
        SourceScout.NormalizeUrl(
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? CanonicalUrl(u) : url);
}
