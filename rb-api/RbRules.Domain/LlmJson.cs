using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Gedeelde tolerante JSON-vondst in LLM-antwoorden. Kandidaat-blokken
/// worden gebalanceerd en string-bewust gescand in plaats van first/last-index,
/// zodat prose vóór de JSON, ```json-fences en bracket-markers zoals "[1]" in
/// bronvermeldingen de extractie niet breken. De eerste live scout-run (#63,
/// PR #87) strandde precies op die first-bracket-fout; de claims-parser had
/// dezelfde bug (#93) — vandaar één gedeelde implementatie.</summary>
public static class LlmJson
{
    /// <summary>Kandidaat-JSON-blokken in het antwoord, objecten vóór arrays
    /// (de prompts eisen een object; een kale array is een terugvaloptie).</summary>
    public static IEnumerable<string> Candidates(string raw)
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

    /// <summary>Pakt de JSON uit een LLM-antwoord en geeft de array-items
    /// terug: een object met <paramref name="property"/>, of een kale array
    /// (prompt-afwijkingstolerantie). null als er niets bruikbaars staat.
    /// Gedeeld door de claims- en relatie-parsers (#93/#116): kandidaat-
    /// blokken via <see cref="Candidates"/> — de oude first/last-bracket-
    /// extractie brak op prose rond de JSON met "[1]"-achtige markers
    /// (zelfde bug als de scout, PR #87).</summary>
    public static List<JsonElement>? ExtractItems(string raw, string property)
    {
        foreach (var json in Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(property, out var p)
                    && p.ValueKind == JsonValueKind.Array)
                {
                    // Clone: de elementen moeten het JsonDocument overleven.
                    return [.. p.EnumerateArray().Select(e => e.Clone())];
                }
                // Kale array — tolerantie voor prompt-afwijking, maar alleen
                // als er echt item-objecten in staan (of hij leeg is): "[1]"
                // uit een bronvermelding is géén item-lijst (scout-les, #87).
                if (root.ValueKind == JsonValueKind.Array
                    && (root.GetArrayLength() == 0
                        || root.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object)))
                {
                    return [.. root.EnumerateArray().Select(e => e.Clone())];
                }
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    /// <summary>Rauwe LLM-respons plat en afgekapt voor één run_log-regel
    /// (diagnosepatroon van PR #87): whitespace samengevouwen, maximaal
    /// <paramref name="max"/> tekens. De respons is LLM-output uit rb-ai en
    /// bevat geen secrets (rb-api kent geen API-keys).</summary>
    public static string Snippet(string raw, int max)
    {
        var flat = string.Join(' ',
            raw.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) return "(leeg antwoord)";
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
