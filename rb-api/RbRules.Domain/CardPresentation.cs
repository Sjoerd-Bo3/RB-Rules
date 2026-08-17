using System.Globalization;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Lokale terugval voor de presentatievelden van een kaart (#270).
/// Riot levert afmetingen, kleuren en alt-tekst bij élke gallery-kaart; de
/// ~141 kaarten die alléén via riftcodex binnenkomen (JDG-promo's) hebben ze
/// niet allemaal. Deze helpers leiden af wat af te leiden valt, zodat ook die
/// tegels de juiste verhouding en een bruikbare alt-tekst krijgen.
/// Puur — geen I/O, geen netwerk.</summary>
public static partial class CardPresentation
{
    /// <summary>De staande verhouding van een gewone Riftbound-kaart. Laatste
    /// terugval als noch de bron noch de URL een maat geeft: beter een kaart
    /// die iets te staand staat dan een tegel zonder verhouding, want dan
    /// springt de hele lijst bij het laden (layout shift).</summary>
    public const int DefaultWidth = 744;
    public const int DefaultHeight = 1039;

    // De Sanity-CDN codeert de maat in de bestandsnaam:
    // ".../7447b04d…c471a-1039x744.png?accountingTag=RB". Riot én riftcodex
    // wijzen naar diezelfde CDN, dus dit werkt voor beide bronnen.
    [GeneratedRegex(@"-(\d{2,5})x(\d{2,5})\.(?:png|jpe?g|webp|avif)(?:$|[?#])",
        RegexOptions.IgnoreCase)]
    private static partial Regex SizeInUrl();

    /// <summary>Afmetingen uit de afbeeldings-URL, of null als de URL er geen
    /// draagt.</summary>
    public static (int Width, int Height)? SizeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = SizeInUrl().Match(url);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var w) &&
               int.TryParse(m.Groups[2].Value, out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    /// <summary>Battlefields liggen; alle andere kaarten staan. Onbekende maat
    /// telt als staand — dat is de verhouding van 1112 van de 1178 kaarten.</summary>
    public static bool IsLandscape(int? width, int? height) =>
        width is > 0 && height is > 0 && width > height;

    /// <summary>Alt-tekst in Riots eigen bewoording ("Riftbound Battlefield:
    /// Abandoned Hall. …") voor kaarten waar de bron er geen levert.
    /// LET OP (#270): dit is een AFGELEIDE tekst. Hij hoort in een
    /// <c>alt=</c> en nergens anders — niet als zichtbare kaarttekst, niet in
    /// de kennisbank, niet in een prompt. Afgeleid is niet officieel.</summary>
    public static string ComposeAltText(
        string name, string? supertype, string? type, string? textPlain)
    {
        var typeLine = string.Join(" ",
            new[] { supertype, type }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var prefix = typeLine.Length > 0 ? $"Riftbound {typeLine}" : "Riftbound card";
        // Rauwe icon-tokens (:rb_might:) zijn onleesbaar voor een screenreader;
        // Riot schrijft daar zelf "[S]" — HumanizeIcons doet hetzelfde werk.
        var text = CardText.HumanizeIcons(textPlain);
        return string.IsNullOrWhiteSpace(text)
            ? $"{prefix}: {name}."
            : $"{prefix}: {name}. {text}";
    }

    /// <summary>Hexkleur uit de bron normaliseren naar "#rrggbb". Alles wat
    /// daar niet op lijkt wordt geweigerd: de waarde belandt in een
    /// style-attribuut, dus onvertrouwde tekst mag er niet doorheen.</summary>
    public static string? NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (!HexColor().IsMatch(v)) return null;
        return "#" + v.TrimStart('#').ToLower(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$")]
    private static partial Regex HexColor();
}
