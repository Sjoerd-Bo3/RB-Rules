using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

public record MinedCard(string Id, string[] Mechanics, string[] Triggers, string[] Effects);

/// <summary>Tekstfragment rond een keyword-voorkoming (#123); Match is de
/// volledige bracketed vorm ("[Assault 2]") zodat de UI die kan markeren.</summary>
public record KeywordSnippet(string Before, string Match, string After);

/// <summary>Prompt + parser voor LLM-mining van kaartmechanieken (F3).
/// De LLM-call zelf loopt via rb-ai; dit deel is puur en getest.
/// Het vocabulaire = seed + door de beheerder geaccepteerde keywords (#52);
/// het normaliseert casing/aliassen. Niet-gelist maar duidelijk als keyword
/// gebruikte mechanieken mogen ook — beheer cureert via de kandidatenqueue.</summary>
public static partial class MechanicMiner
{
    /// <summary>Bekende Riftbound-mechanieken (basislijst; groeit via
    /// geaccepteerde MechanicKeywords, zie Vocabulary).</summary>
    public static readonly string[] SeedVocabulary =
    [
        "Accelerate", "Tank", "Deflect", "Hidden", "Shield", "Legion",
        "Deathknell", "Reaction", "Action", "Temporary", "Recycle",
    ];

    public const string SystemPrompt = """
        Je analyseert Riftbound TCG kaartteksten. Extraheer per kaart:
        - "mechanics": keyword-mechanieken die in de tekst voorkomen of het gedrag
          van de kaart bepalen. Gebruik bij voorkeur exact deze spelling:
          {VOCAB}. Een duidelijk als keyword gebruikte mechaniek buiten deze
          lijst mag ook (exacte spelling uit de kaarttekst). GEEN facties,
          champions of subtypes (dus niet: Mech, Piltover, Jinx).
        - "triggers": condities die iets laten gebeuren, kort genormaliseerd in
          het Engels (bv. "when I conquer", "when a unit dies", "when played").
        - "effects": wat de kaart doet, kort genormaliseerd in het Engels
          (bv. "kill a unit", "draw a card", "buff might", "move a unit").
        Antwoord UITSLUITEND met een JSON-array:
        [{"id": "...", "mechanics": [...], "triggers": [...], "effects": [...]}]
        Eén element per kaart, zelfde ids als de input. Lege arrays zijn prima.
        Geen tekst buiten de JSON.
        """;

    /// <summary>Effectief vocabulaire: seed + geaccepteerde keywords,
    /// gededupliceerd (case-insensitive, seed-spelling wint).</summary>
    public static IReadOnlyList<string> Vocabulary(IEnumerable<string>? accepted = null)
    {
        if (accepted is null) return SeedVocabulary;
        var seen = new HashSet<string>(SeedVocabulary, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(SeedVocabulary);
        foreach (var term in accepted)
        {
            var t = term.Trim();
            if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    public static string GetSystemPrompt(IEnumerable<string>? acceptedKeywords = null) =>
        SystemPrompt.Replace("{VOCAB}", string.Join(", ", Vocabulary(acceptedKeywords)));

    public static string BuildPrompt(IEnumerable<Card> cards)
    {
        var sb = new StringBuilder("Kaarten:\n");
        foreach (var c in cards)
        {
            sb.AppendLine($"- id: {c.RiftboundId}");
            sb.AppendLine($"  naam: {c.Name} ({c.Type ?? "?"})");
            sb.AppendLine($"  tekst: {c.TextPlain ?? "(geen tekst)"}");
        }
        return sb.ToString();
    }

    public static IReadOnlyList<MinedCard> ParseBatch(
        string raw, IEnumerable<string>? acceptedKeywords = null)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        var vocabulary = Vocabulary(acceptedKeywords);
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var results = new List<MinedCard>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                results.Add(new MinedCard(
                    id!,
                    Strings(item, "mechanics", vocabulary),
                    Strings(item, "triggers", vocabulary),
                    Strings(item, "effects", vocabulary)));
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Keyword-kandidaten in een kaarttekst (#52): bracketed termen
    /// ("[Ganking]", "[Assault 2]") die niet in het vocabulaire staan. Puur en
    /// deterministisch — geen LLM nodig. Numerieke parameters worden gestript
    /// ("Assault 2" → "Assault"); ruis als "[&gt;]" (icoon-pijl) en "[NO TEXT]"
    /// valt af doordat een keyword met een hoofdletter + kleine letter begint
    /// en verder alleen uit letters/spaties/koppeltekens bestaat.</summary>
    public static IReadOnlyList<string> ExtractKeywordCandidates(
        string? textPlain, IEnumerable<string> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(textPlain)) return [];
        var known = new HashSet<string>(vocabulary, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (Match m in BracketedTerm().Matches(textPlain))
        {
            var term = NumericParameter().Replace(m.Groups[1].Value.Trim(), "");
            if (!KeywordShape().IsMatch(term) || term.Length > 30) continue;
            if (known.Add(term)) found.Add(term); // dedupe + vocab-filter ineen
        }
        return found;
    }

    /// <summary>Kort tekstfragment rond de eerste bracketed voorkoming van een
    /// keyword (#123): zelfde herkenning als ExtractKeywordCandidates — de
    /// numerieke parameter hoort bij de match ("[Assault 2]" is bewijs voor
    /// term "Assault") en de vergelijking is case-insensitive, net als de
    /// dedupe in de kandidaten-harvest. Drie delen (voor/match/na) zodat de
    /// UI de term kan markeren zonder {@html}.</summary>
    public static KeywordSnippet? SnippetFor(string? textPlain, string term, int context = 60)
    {
        if (string.IsNullOrWhiteSpace(textPlain) || string.IsNullOrWhiteSpace(term)) return null;
        foreach (Match m in BracketedTerm().Matches(textPlain))
        {
            var inner = NumericParameter().Replace(m.Groups[1].Value.Trim(), "");
            if (!inner.Equals(term.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var start = Math.Max(0, m.Index - context);
            var end = Math.Min(textPlain.Length, m.Index + m.Length + context);
            return new(
                (start > 0 ? "…" : "") + textPlain[start..m.Index],
                m.Value,
                textPlain[(m.Index + m.Length)..end] + (end < textPlain.Length ? "…" : ""));
        }
        return null;
    }

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex BracketedTerm();

    /// <summary>Trailing numeriek argument van een keyword ("Deflect 2").</summary>
    [GeneratedRegex(@"\s+\d+$")]
    private static partial Regex NumericParameter();

    [GeneratedRegex(@"^[A-Z][a-z][A-Za-z' -]*$")]
    private static partial Regex KeywordShape();

    private static string[] Strings(
        JsonElement obj, string key, IReadOnlyList<string> vocabulary)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return [.. arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Normalize(s, vocabulary))
            .Distinct()];
    }

    /// <summary>Normaliseer tegen het vocabulaire (case-insensitive).</summary>
    private static string Normalize(string value, IReadOnlyList<string> vocabulary)
    {
        var match = vocabulary.FirstOrDefault(
            v => v.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? value.Trim();
    }
}
