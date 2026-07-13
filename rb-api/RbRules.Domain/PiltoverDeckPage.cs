using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eén kaartregel uit een Piltover Archive-deck: sectie (legend/
/// champions/battlefields/runes/maindeck/sideboard/bench), het PA-variant-
/// nummer ("OGN-126a"), de kaartnaam zoals PA hem kent en het aantal.</summary>
public record ParsedDeckCard(string Section, string? VariantNumber, string? CardName, int Quantity);

/// <summary>Geparste publieke deck-pagina van Piltover Archive (#15).
/// Ontbrekende velden zijn null/leeg — de parser crasht nooit op een
/// afwijkende pagina, dat is een verwerkbaar "geen deck".</summary>
public record ParsedDeck(
    string Id, string? Name, string[] Domains,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
    int Views, int Likes, IReadOnlyList<ParsedDeckCard> Cards);

/// <summary>Parser voor de publieke deck-pagina's van piltoverarchive.com
/// (#15, Piltover-first): Next.js App Router serialiseert de deck-data als
/// React Server Components-flight in <c>self.__next_f.push([1,"…"])</c>-
/// chunks. Chunks unescapen en aaneenrijgen geeft een blob met daarin één
/// <c>"deck":{…}</c>-object; dat blok wordt gebalanceerd uitgeknipt
/// (LlmJson-scanner) en na flight-sanering als JSON gelezen. Puur — netwerk
/// zit in Infrastructure (DeckIngestService).</summary>
public static partial class PiltoverDeckPage
{
    public const string LegendSection = "legend";

    /// <summary>De kaartsecties zoals ze in het deck-object heten (live
    /// geverifieerd op meerdere pagina's, 2026-07-13); de legend staat er
    /// als los object naast en krijgt sectie "legend".</summary>
    public static readonly string[] CardSections =
        ["champions", "battlefields", "runes", "maindeck", "sideboard", "bench"];

    [GeneratedRegex("""self\.__next_f\.push\(\[1,\s*"((?:[^"\\]|\\.)*)"\]\)""")]
    private static partial Regex FlightChunk();

    public static ParsedDeck? Parse(string html)
    {
        var blob = FlightBlob(html);
        // Alle voorkomens proberen: '"deck":' kan in theorie ook in prose of
        // een ander flight-object staan — het eerste blok dat als deck parst
        // (uuid-id) wint.
        const string marker = "\"deck\":";
        for (var i = blob.IndexOf(marker, StringComparison.Ordinal); i >= 0;
             i = blob.IndexOf(marker, i + 1, StringComparison.Ordinal))
        {
            var objStart = i + marker.Length;
            if (objStart >= blob.Length || blob[objStart] != '{') continue;
            if (LlmJson.BalancedBlock(blob, objStart) is not { } block) continue;
            if (MapDeck(block) is { } deck) return deck;
        }
        return null;
    }

    /// <summary>Alle flight-chunks unescaped en aaneengeregen — het deck-object
    /// kan in principe over een chunk-grens heen lopen, dus eerst plakken en
    /// dan pas zoeken.</summary>
    private static string FlightBlob(string html)
    {
        var sb = new StringBuilder();
        foreach (Match m in FlightChunk().Matches(html))
            sb.Append(Unescape(m.Groups[1].Value));
        return sb.ToString();
    }

    /// <summary>JS-string-escapes (JSON.stringify-vormen) terug naar tekst;
    /// onbekende escapes verliezen alleen hun backslash — nooit een crash.</summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < s.Length && ushort.TryParse(
                    s.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var code):
                    // Surrogaatparen komen als twee \u-escapes; los aaneen-
                    // geplakt vormen ze vanzelf weer het juiste teken.
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(n); break; // \" \\ \/ en onbekenden
            }
        }
        return sb.ToString();
    }

    /// <summary>Flight-JSON naar strikte JSON: <c>"$D…"</c>-datums verliezen
    /// hun prefix, <c>"$$…"</c> is een ge-escapete dollar-string en andere
    /// <c>"$…"</c>-waarden zijn flight-referenties ($undefined, $L…) die als
    /// null lezen. String-bewust — een '$' mídden in een deck-naam blijft
    /// gewoon staan.</summary>
    public static string SanitizeFlight(string json)
    {
        var sb = new StringBuilder(json.Length);
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (c != '"')
            {
                sb.Append(c);
                i++;
                continue;
            }
            var end = i + 1;
            while (end < json.Length && json[end] != '"')
                end += json[end] == '\\' ? 2 : 1;
            if (end >= json.Length)
            {
                sb.Append(json, i, json.Length - i); // onafgesloten string — laat staan
                break;
            }
            var body = json[(i + 1)..end];
            if (body.StartsWith("$D", StringComparison.Ordinal))
                sb.Append('"').Append(body, 2, body.Length - 2).Append('"');
            else if (body.StartsWith("$$", StringComparison.Ordinal))
                sb.Append('"').Append(body, 1, body.Length - 1).Append('"');
            else if (body.StartsWith('$'))
                sb.Append("null");
            else
                sb.Append('"').Append(body).Append('"');
            i = end + 1;
        }
        return sb.ToString();
    }

    private static ParsedDeck? MapDeck(string block)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(SanitizeFlight(block));
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // PA-deck-ids zijn uuids — dat onderscheidt het echte deck-object
            // van een toevallig '"deck":'-voorkomen elders in de flight.
            var id = Str(root, "id");
            if (id is null || !Guid.TryParse(id, out _)) return null;

            var cards = new List<ParsedDeckCard>();
            var domains = Array.Empty<string>();
            if (root.TryGetProperty("legend", out var legend)
                && legend.ValueKind == JsonValueKind.Object)
            {
                var legendName = legend.TryGetProperty("card", out var legendCard)
                    && legendCard.ValueKind == JsonValueKind.Object
                        ? Str(legendCard, "name")
                        : null;
                cards.Add(new(LegendSection, Str(legend, "variantNumber"), legendName, 1));
                domains = DomainsOf(legend);
            }
            foreach (var section in CardSections)
            {
                if (!root.TryGetProperty(section, out var arr)
                    || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in arr.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object)
                        cards.Add(MapEntry(section, entry));
            }

            return new(
                id,
                Str(root, "name"),
                domains,
                Date(root, "createdAt"),
                Date(root, "updatedAt"),
                Int(root, "views") ?? 0,
                Int(root, "likes") ?? 0,
                cards);
        }
    }

    /// <summary>Eén deck-entry: het aantal plus de gekozen printing — de
    /// entry wijst met variantId naar één van card.cardVariants; zonder match
    /// telt de eerste variant (live bevat de lijst meestal precies de gekozen
    /// variant).</summary>
    private static ParsedDeckCard MapEntry(string section, JsonElement entry)
    {
        var quantity = Int(entry, "quantity") ?? 1;
        string? name = null;
        string? variantNumber = null;
        if (entry.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
        {
            name = Str(card, "name");
            var variantId = Str(entry, "variantId");
            if (card.TryGetProperty("cardVariants", out var variants)
                && variants.ValueKind == JsonValueKind.Array)
            {
                JsonElement? chosen = null;
                foreach (var v in variants.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    chosen ??= v;
                    if (variantId is not null && Str(v, "id") == variantId)
                    {
                        chosen = v;
                        break;
                    }
                }
                if (chosen is { } picked) variantNumber = Str(picked, "variantNumber");
            }
        }
        return new(section, variantNumber, name, quantity);
    }

    /// <summary>Domeinen/kleuren van het deck via de legend:
    /// legend.card.cardColors[].color.name (PA noemt ze colors; het zijn
    /// dezelfde namen als onze card.domains — Body, Order, …).</summary>
    private static string[] DomainsOf(JsonElement legend)
    {
        if (!legend.TryGetProperty("card", out var card)
            || card.ValueKind != JsonValueKind.Object
            || !card.TryGetProperty("cardColors", out var colors)
            || colors.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var entry in colors.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("color", out var color)
                && color.ValueKind == JsonValueKind.Object
                && Str(color, "name") is { } name)
                result.Add(name);
        }
        return [.. result];
    }

    /// <summary>PA-userdata kan JSON-legale maar Postgres-onwettige tekens
    /// bevatten: een NUL-byte in een text-kolom geeft een DbUpdateException
    /// en zou zonder wasstraat élke run deterministisch op hetzelfde deck
    /// laten stranden (review-fix #15). NUL verdwijnt; overige control chars
    /// worden een spatie zodat een naam met een verdwaalde \n leesbaar blijft.</summary>
    public static string? CleanText(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(char.IsControl)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\0') continue;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>Alle string-reads lopen door <see cref="CleanText"/> — één
    /// funnel, dus ook kaartnamen, variantnummers en domeinen zijn schoon.</summary>
    private static string? Str(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? CleanText(p.GetString())
            : null;

    private static int? Int(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var n)
            ? n
            : null;

    private static DateTimeOffset? Date(JsonElement obj, string property) =>
        Str(obj, property) is { } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
}
