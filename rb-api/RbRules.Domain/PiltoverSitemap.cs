using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Deck-verwijzing uit een sitemap-shard: de uuid uit
/// /decks/view/{uuid} plus de lastmod die PA per deck meegeeft (basis voor
/// gerichte versheid — alleen her-fetchen wat op PA écht wijzigde).</summary>
public record DeckSitemapEntry(string Uuid, DateTimeOffset? LastModified);

/// <summary>Sitemap-lezer voor piltoverarchive.com (#15): de index
/// (/sitemap.xml) verwijst naar shards (/sitemap/0..N); de shards dragen de
/// deck-URL's mét lastmod. Regex-gebaseerd en tolerant — een half kapotte
/// shard levert minder entries op, geen crash. Puur; netwerk zit in
/// Infrastructure.</summary>
public static partial class PiltoverSitemap
{
    [GeneratedRegex(@"<sitemap>\s*<loc>\s*([^<\s]+)\s*</loc>", RegexOptions.IgnoreCase)]
    private static partial Regex ShardLoc();

    [GeneratedRegex(@"<loc>\s*([^<\s]+)\s*</loc>", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLoc();

    [GeneratedRegex(@"<lastmod>\s*([^<\s]+)\s*</lastmod>", RegexOptions.IgnoreCase)]
    private static partial Regex LastMod();

    /// <summary>Shard-URL's uit de sitemap-index, alleen van de eigen host én
    /// onder /sitemap — de index is pagina-inhoud en mag ons nooit ergens
    /// anders laten fetchen (robots-afspraak: /api/ blijft onaangeraakt).</summary>
    public static IReadOnlyList<string> ShardUrls(string xml, string host = "piltoverarchive.com")
    {
        var shards = new List<string>();
        foreach (Match m in ShardLoc().Matches(xml))
        {
            var url = m.Groups[1].Value;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
                && (uri.AbsolutePath == "/sitemap.xml"
                    || uri.AbsolutePath.StartsWith("/sitemap/", StringComparison.Ordinal)))
                shards.Add(url);
        }
        return shards;
    }

    /// <summary>Deck-entries uit een shard: per &lt;url&gt;-blok de loc met
    /// /decks/view/{uuid} en de optionele lastmod. Andere pagina's in de
    /// shard (cards, news, tournaments) tellen niet mee.</summary>
    public static IReadOnlyList<DeckSitemapEntry> DeckEntries(string xml)
    {
        var entries = new List<DeckSitemapEntry>();
        foreach (var block in xml.Split("<url>", StringSplitOptions.RemoveEmptyEntries))
        {
            var loc = UrlLoc().Match(block);
            if (!loc.Success) continue;
            var marker = "/decks/view/";
            var at = loc.Groups[1].Value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var uuid = loc.Groups[1].Value[(at + marker.Length)..].TrimEnd('/');
            if (!Guid.TryParse(uuid, out _)) continue;

            var lastmod = LastMod().Match(block);
            DateTimeOffset? modified = lastmod.Success
                && DateTimeOffset.TryParse(lastmod.Groups[1].Value, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt
                    : null;
            entries.Add(new(uuid.ToLowerInvariant(), modified));
        }
        return entries;
    }
}
