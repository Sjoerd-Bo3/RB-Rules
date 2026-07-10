using System.Text;
using System.Text.Json;

namespace RbRules.Domain;

public record MinedCard(string Id, string[] Mechanics, string[] Triggers, string[] Effects);

/// <summary>Prompt + parser voor LLM-mining van kaartmechanieken (F3).
/// De LLM-call zelf loopt via rb-ai; dit deel is puur en getest.
/// Seed-vocabulaire normaliseert casing/aliassen; niet-gelist maar duidelijk
/// als keyword gebruikte mechanieken mogen ook — beheer kan later cureren.</summary>
public static class MechanicMiner
{
    /// <summary>Bekende Riftbound-mechanieken (uitbreidbaar; alleen normalisatie-hint).</summary>
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

    public static string GetSystemPrompt() =>
        SystemPrompt.Replace("{VOCAB}", string.Join(", ", SeedVocabulary));

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

    public static IReadOnlyList<MinedCard> ParseBatch(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
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
                    Strings(item, "mechanics"),
                    Strings(item, "triggers"),
                    Strings(item, "effects")));
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] Strings(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return [.. arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(Normalize)
            .Distinct()];
    }

    /// <summary>Normaliseer tegen het seed-vocabulaire (case-insensitive).</summary>
    private static string Normalize(string value)
    {
        var match = SeedVocabulary.FirstOrDefault(
            v => v.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? value.Trim();
    }
}
