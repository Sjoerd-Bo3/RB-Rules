using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eén artikel op een Riot-nieuws-indexpagina (#167). Category is het
/// &lt;categorie&gt;-padsegment uit /en-us/news/&lt;categorie&gt;/&lt;slug&gt;
/// (bv. "rules-and-releases", "announcements", "organizedplay"); null als de
/// URL geen categorie-segment heeft (bv. /en-us/news/&lt;slug&gt;
/// rechtstreeks). Date is null als de kaart geen &lt;time&gt;-element droeg.</summary>
public record RiotNewsArticle(string Title, string Url, DateTimeOffset? Date, string? Category);

/// <summary>Parser voor playriftbound.com/en-us/news/-indexpagina's (#167):
/// zowel de smalle "rules-and-releases"-feed, de brede algemene nieuws-hub
/// (/en-us/news/) als de artikel-carrousel onderaan de Rules Hub delen
/// dezelfde React-kaartcomponent (`data-testid="articlefeaturedcard-
/// component"`) — één parser dekt alle drie (geverifieerd tegen een echte
/// fetch van elke pagina, 2026-07-14). Puur en unit-testbaar, zelfde patroon
/// als <see cref="HubDiscovery"/>: ankertekst/attribuut-matching, nooit een
/// crash op onverwachte opmaak.
///
/// Tolerantie: kaarten zonder bruikbare href of titel worden overgeslagen;
/// externe links (bv. een YouTube-video-kaart) vallen weg via de host-check;
/// een ontbrekende datum wordt null, nooit een gok.</summary>
public static partial class RiotNewsFeed
{
    // Kaart-anker: attributen (href, aria-label) los van hun volgorde in de
    // opening-tag, body tot de sluitende </a> (geen geneste <a> in de
    // kaartopmaak — geverifieerd tegen alle drie de paginavormen).
    [GeneratedRegex(
        """<a\b(?<attrs>[^>]*data-testid=["']articlefeaturedcard-component["'][^>]*)>(?<body>[\s\S]*?)</a>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Card();

    [GeneratedRegex("""<time[^>]*dateTime=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex TimeElement();

    [GeneratedRegex("""data-testid=["']card-title["'][^>]*>([\s\S]*?)</div>""", RegexOptions.IgnoreCase)]
    private static partial Regex TitleElement();

    /// <summary>Parst de artikelkaarten op een indexpagina. <paramref
    /// name="categoryFilter"/> is null/leeg (alle categorieën) of een
    /// komma-gescheiden lijst toegestane categorieën — een artikel zonder
    /// categorie-segment matcht dan nooit (het kan niet bevestigd worden),
    /// tenzij het filter zelf leeg is.</summary>
    public static IReadOnlyList<RiotNewsArticle> ParseArticles(
        string html, Uri baseUri, string? categoryFilter = null)
    {
        var allowed = string.IsNullOrWhiteSpace(categoryFilter)
            ? null
            : categoryFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var articles = new List<RiotNewsArticle>();
        foreach (Match m in Card().Matches(html))
        {
            var attrs = m.Groups["attrs"].Value;
            var body = m.Groups["body"].Value;

            var href = Attr(attrs, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseUri, href, out var abs)) continue;
            if (abs.Scheme != Uri.UriSchemeHttps) continue;
            // Externe kaarten (bv. een YouTube-videokaart tussen de
            // artikelen) horen niet bij deze feed — alleen artikelen op het
            // eigen domein zijn per-URL-registreerbare bronnen.
            if (!abs.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;

            var category = CategoryFromPath(abs.AbsolutePath);
            if (category is null && !IsNewsArticlePath(abs.AbsolutePath)) continue;
            if (allowed is not null && (category is null || !allowed.Contains(category))) continue;

            var titleMatch = TitleElement().Match(body);
            var rawTitle = titleMatch.Success ? titleMatch.Groups[1].Value : Attr(attrs, "aria-label");
            var title = CleanTitle(rawTitle);
            if (string.IsNullOrWhiteSpace(title)) continue;

            DateTimeOffset? date = null;
            var timeMatch = TimeElement().Match(body);
            if (timeMatch.Success && DateTimeOffset.TryParse(
                    timeMatch.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                date = parsed;

            var url = NormalizeUrl(abs.ToString());
            if (!seen.Add(url)) continue;

            articles.Add(new RiotNewsArticle(title, url, date, category));
        }
        return articles;
    }

    /// <summary>Categorie uit /en-us/news/&lt;categorie&gt;/&lt;slug&gt;; null
    /// als het pad geen categorie-segment heeft (bv. /en-us/news/&lt;slug&gt;
    /// rechtstreeks — voorkomt op de algemene nieuws-hub).</summary>
    private static string? CategoryFromPath(string absolutePath)
    {
        var segs = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length >= 4 && segs[0] == "en-us" && segs[1] == "news" ? segs[2] : null;
    }

    /// <summary>Is dit pad überhaupt een /en-us/news/-artikel (met of zonder
    /// categorie-segment)? Filtert navigatie-/categorie-indexlinks eruit.</summary>
    private static bool IsNewsArticlePath(string absolutePath)
    {
        var segs = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length >= 3 && segs[0] == "en-us" && segs[1] == "news";
    }

    private static string CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // De card-title-div is in de praktijk platte tekst, maar strip
        // eventuele geneste opmaak defensief (zelfde patroon als HubDiscovery).
        var stripped = Regex.Replace(raw, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $"""{name}=["']([^"']*)["']""", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    /// <summary>Vergelijkings-/opslagvorm: zonder trailing slash (zelfde
    /// dedupe-conventie als <see cref="SourceScout.NormalizeUrl"/>).</summary>
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}
