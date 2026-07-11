using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Prompt + parser voor de herhaalbare bronnenjacht (#63, stap 2).
/// De LLM-call loopt via rb-ai (task "research", #64); dit deel is puur en
/// getest, zelfde patroon als QueryRewriter/Classifier. Uitval of
/// onzin-output ⇒ null, en de aanroeper degradeert netjes. Parse levert
/// direct <see cref="SourceProposal"/>-entiteiten (de vondst ís het
/// reviewqueue-item — geen aparte DTO-laag nodig).</summary>
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
            result.Add(new SourceProposal
            {
                Url = url,
                Name = string.IsNullOrEmpty(name) ? uri.Host : name,
                // Webvondsten zijn per definitie hooguit zo betrouwbaar
                // als hun type-inschatting; onbekende labels degraderen
                // naar community (docs/KNOWLEDGE.md: trust is heilig).
                Type = type is "official" or "partner" ? type : "community",
                Motivation = Truncate(GetString(item, "motivation"), MaxMotivationLength) ?? "",
            });
        }
        return result;
    }

    /// <summary>Backfill-parser (#63): reconstrueert een voorstel uit de oude
    /// run_log-vorm (Ref = url, Detail = "url — naam (type): motivatie"), zodat
    /// voorstellen van vóór de reviewqueue niet verloren gaan. Onherkenbaar
    /// detail degradeert veilig: host als naam, community als type (nooit een
    /// trust-upgrade door een parse-gok).</summary>
    public static SourceProposal FromRunLog(string url, string? detail, DateTimeOffset foundAt)
    {
        var rest = detail ?? "";
        var prefix = url + " — ";
        if (rest.StartsWith(prefix, StringComparison.Ordinal)) rest = rest[prefix.Length..];

        var m = RunLogDetailPattern.Match(rest);
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return new SourceProposal
        {
            Url = url,
            Name = Truncate(m.Success ? m.Groups["name"].Value : host, MaxNameLength)!,
            Type = m.Success ? m.Groups["type"].Value : "community",
            Motivation = Truncate(m.Success ? m.Groups["motivation"].Value : rest, MaxMotivationLength)!,
            FoundAt = foundAt,
        };
    }

    // Greedy name-groep: een naam als "Judge FAQ (example.com)" mag zelf
    // haakjes bevatten — het láátste "(type):" telt.
    private static readonly Regex RunLogDetailPattern = new(
        @"^(?<name>.+) \((?<type>official|partner|community)\): (?<motivation>.*)$",
        RegexOptions.Singleline | RegexOptions.ExplicitCapture);

    /// <summary>Register-entry voor een geaccepteerd voorstel, met veilige
    /// defaults: uitgeschakeld (de beheerder zet hem bewust aan via de
    /// bronnen-tabel), cadence weekly, parser naar bestandstype, trust volgens
    /// de type-inschatting maar nooit tier 1 voor niet-official — de
    /// kennislagen-regel blijft zo intact. Id-uniekheid regelt de
    /// aanroeper (suffix bij botsing).</summary>
    public static Source ToSource(SourceProposal p) => new()
    {
        Id = SlugForUrl(p.Url),
        Name = p.Name,
        Url = p.Url,
        Type = p.Type is "official" or "partner" ? p.Type : "community",
        TrustTier = p.Type switch { "official" => (short)1, "partner" => (short)2, _ => (short)3 },
        // Laag in de pikorde tot de beheerder anders beslist (curated
        // bronnen: officieel 90-110, partner 70, community 40-50).
        Rank = 10,
        Parser = Uri.TryCreate(p.Url, UriKind.Absolute, out var uri)
                 && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "pdf" : "html",
        Cadence = "weekly",
        Enabled = false,
    };

    /// <summary>Leesbaar register-id uit een URL: host zonder www + laatste
    /// betekenisvolle padsegment ("riftbound.gg/judge-faq/" →
    /// "riftbound-gg-judge-faq"), begrensd op 60 tekens.</summary>
    public static string SlugForUrl(string url)
    {
        string host = url, segment = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..] : uri.Host;
            segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? "";
            var dot = segment.LastIndexOf('.');
            if (dot > 0) segment = segment[..dot];      // extensie eraf (.pdf, .html)
        }
        var raw = string.Concat($"{host}-{segment}".ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        var slug = string.Join('-', raw.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
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
