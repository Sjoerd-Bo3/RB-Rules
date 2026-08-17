using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Tekst-hulpfuncties, geport uit de PoP (src/lib/text.ts) met tests.</summary>
public static partial class TextUtils
{
    /// <summary>Versie van <see cref="StripBoilerplate"/> (#205-review).
    /// VERHOOG DIT NUMMER bij élke gedragswijziging aan de strip — de
    /// gestripte tekst bepaalt de content-hash van élke bron, dus een
    /// stille strip-wijziging zou anders één golf junk-"changes" over het
    /// hele register geven (de diff toont dan alleen de weggevallen
    /// boilerplate). <see cref="RbRules.Infrastructure.IngestService"/>
    /// vergelijkt dit nummer met <see cref="Source.StripVersion"/> en
    /// rebaselinet een verouderde bron stil (nieuwe baseline, geen
    /// diff/Change). Historie: v1 = nav/header/footer/aside (audit-fix);
    /// v2 = + de playriftbound "Related Articles"-carousel (#205).</summary>
    public const int BoilerplateVersion = 2;

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
        // #205: playriftbound-artikelen tonen een "Related Articles"-carousel
        // in een gewone <section id="related-articles"> (dus niet gevangen
        // door de aside-strip hierboven, want het is geen <aside>) die van
        // scan tot scan verandert zodra elders op de site een nieuw artikel
        // verschijnt — geen echte regelwijziging, maar wel editorial-ruis in
        // de wijzigingen-feed. Zelfde aanpak als hierboven: het hele blok
        // eruit vóór hash/diff. Niet-greedy is hier veilig: deze CMS nest
        // geen <section> in een andere <section>, dus de eerstvolgende
        // sluit-tag hoort altijd bij dít blok (zelfde aanname als de
        // nav/header/footer/aside-strip hierboven).
        text = Regex.Replace(
            text, @"<section\b[^>]*\bid=""related-articles""[^>]*>[\s\S]*?</section>", " ",
            RegexOptions.IgnoreCase);
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
