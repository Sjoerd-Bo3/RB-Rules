using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Eén webvondst uit de bronnenjacht (#63): een kandidaat-bron als
/// vóórstel voor de beheerder — nooit iets dat automatisch bron wordt.</summary>
public record SourceProposal(string Url, string Name, string Type, string Motivation);

/// <summary>Prompt + parser voor de herhaalbare bronnenjacht (#63, stap 2).
/// De LLM-call loopt via rb-ai (task "research", #64); dit deel is puur en
/// getest, zelfde patroon als QueryRewriter/Classifier. Uitval of
/// onzin-output ⇒ null, en de aanroeper degradeert netjes.</summary>
public static class SourceScout
{
    /// <summary>Harde cap per run — liever een handvol goede voorstellen dan
    /// een ongefilterde lijst die de beheerder toch niet doorwerkt.</summary>
    public const int MaxProposals = 10;
    private const int MaxNameLength = 120;
    private const int MaxMotivationLength = 300;

    // Let op: rb-ai plakt bij task "research" zijn eigen contract achter deze
    // prompt (verplichte "Bronnen:"-sectie, zie rb-ai/src/ai.ts). Die sectie
    // botst met "alleen JSON", dus het formaat hieronder benoemt haar
    // expliciet: éérst het JSON-object, daarna het Bronnen-blok. De eerste
    // live run (#63) leverde onparseerbare output op — vandaar de harde
    // formaateisen. Parse blijft tolerant voor tekst rond de JSON.
    public const string SystemPrompt = """
        Je bent de bronnen-scout van een kennisbank over Riftbound, het
        League of Legends trading card game van Riot Games. Doorzoek het web
        naar actuele bronnen die de kennisbank versterken: how-to-play-uitleg,
        regels-uitleg, judge-resources, rulings-verzamelingen en
        community-wiki's. Geen webshops, geen nieuws zonder regels-inhoud,
        geen video's zonder leesbare pagina.
        Je krijgt een uitsluitlijst met URL's die al in het bronnenregister
        staan of al eerder zijn voorgesteld; stel alleen bronnen voor die daar
        nog NIET tussen staan.

        Antwoordformaat — dit wordt machinaal geparsed, wijk er niet van af:
        1. Je antwoord MOET beginnen met dit JSON-object (een ```json-codefence
           eromheen is toegestaan). Géén inleidende tekst, geen samenvatting,
           geen kopje vóór de JSON:
           {"proposals": [{"url": "https://...", "name": "...", "type": "...", "motivation": "..."}]}
        2. Direct ná het JSON-object volgt uitsluitend de verplichte sectie
           "Bronnen:" (de geraadpleegde URL's); verder geen tekst.

        Veldregels:
        - url: volledige https-URL van de concrete pagina (niet de homepage
          als de inhoud dieper zit).
        - name: korte naam van de bron (site + onderwerp).
        - type ∈ official (door Riot zelf gepubliceerd) | partner
          (organized-play-partner, zoals UVS Games) | community (al het
          overige; kies dit bij twijfel).
        - motivation: 1-2 zinnen (NL) waarom deze bron de kennisbank versterkt.
        - Maximaal 10 voorstellen; liever 3 sterke dan 10 matige.
        - Niets nieuws gevonden? Begin dan met {"proposals": []}.
        """;

    public static string BuildPrompt(IEnumerable<string> knownUrls) =>
        "Zoek nieuwe Riftbound-bronnen voor de kennisbank.\n\n"
        + "Uitsluitlijst (al in het register of eerder voorgesteld):\n"
        + string.Join('\n', knownUrls.Select(u => $"- {u}"));

    /// <summary>Tolerante JSON-extractie uit een LLM-antwoord. null bij
    /// mislukking (geen bruikbare JSON); een lege lijst betekent "geparsed,
    /// maar niets (nieuws) gevonden". Alleen https-URL's; duplicaten binnen
    /// het antwoord én tegen <paramref name="knownUrls"/> vallen weg.</summary>
    public static IReadOnlyList<SourceProposal>? Parse(
        string raw, IEnumerable<string>? knownUrls = null)
    {
        foreach (var json in JsonCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("proposals", out var p)
                    && p.ValueKind == JsonValueKind.Array)
                {
                    return MapProposals(p, knownUrls);
                }
                // Kale array — tolerantie voor prompt-afwijking, maar alleen
                // als er echt voorstel-objecten in staan (of hij leeg is):
                // "[1]" uit een bronvermelding is géén voorstel-lijst.
                if (root.ValueKind == JsonValueKind.Array
                    && (root.GetArrayLength() == 0
                        || root.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object)))
                {
                    return MapProposals(root, knownUrls);
                }
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    private static IReadOnlyList<SourceProposal> MapProposals(
        JsonElement items, IEnumerable<string>? knownUrls)
    {
        var seen = new HashSet<string>(
            (knownUrls ?? []).Select(NormalizeUrl), StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceProposal>();
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count >= MaxProposals) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var url = GetString(item, "url");
            if (string.IsNullOrEmpty(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps) continue;
            if (!seen.Add(NormalizeUrl(url))) continue;

            var name = Truncate(GetString(item, "name"), MaxNameLength);
            var type = GetString(item, "type")?.ToLowerInvariant();
            result.Add(new SourceProposal(
                url,
                string.IsNullOrEmpty(name) ? uri.Host : name,
                // Webvondsten zijn per definitie hooguit zo betrouwbaar
                // als hun type-inschatting; onbekende labels degraderen
                // naar community (docs/KNOWLEDGE.md: trust is heilig).
                type is "official" or "partner" ? type : "community",
                Truncate(GetString(item, "motivation"), MaxMotivationLength) ?? ""));
        }
        return result;
    }

    /// <summary>Vergelijkingsvorm voor URL-dedupe: case- en
    /// trailing-slash-ongevoelig, maar verder de URL zoals opgegeven.</summary>
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>Kandidaat-JSON-blokken in het antwoord, objecten vóór arrays
    /// (het contract is een object; een kale array is een terugvaloptie).
    /// Gebalanceerd en string-bewust gescand in plaats van first/last-index,
    /// zodat prose vóór de JSON, ```json-fences en het "Bronnen:"-blok erna
    /// (rb-ai's research-contract, met "[1]"-achtige markers) niet storen —
    /// de eerste live run (#63) strandde precies daarop.</summary>
    private static IEnumerable<string> JsonCandidates(string raw)
    {
        foreach (var open in new[] { '{', '[' })
            for (var i = raw.IndexOf(open); i >= 0; i = raw.IndexOf(open, i + 1))
                if (BalancedBlock(raw, i) is { } block)
                    yield return block;
    }

    /// <summary>Het gebalanceerde blok dat op <paramref name="start"/> opent,
    /// met JSON-strings (incl. escapes) overgeslagen; null als het blok niet
    /// sluit of met het verkeerde haakje sluit.</summary>
    private static string? BalancedBlock(string raw, int start)
    {
        var close = raw[start] == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0)
            {
                return c == close ? raw[start..(i + 1)] : null;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    private static string? Truncate(string? s, int max) =>
        s?.Length > max ? s[..max] : s;
}
