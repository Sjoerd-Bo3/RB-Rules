using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Zoekmateriaal dat de LLM van een gebruikersvraag maakt (#66):
/// een genormaliseerde Engelse zoekzin, 1-3 semantische zoekqueries en
/// lexicale termen voor kaartteksten.</summary>
public record QueryRewrite(
    string NormalizedQuestion,
    string[] SearchQueries,
    string[] LexicalTerms);

/// <summary>Prompt + parser voor de LLM-query-herformulering vóór retrieval
/// (#66). De LLM-call loopt via rb-ai; dit deel is puur en getest. Uitval of
/// onzin-output ⇒ null, en de aanroeper valt terug op de rauwe vraag —
/// nooit slechter dan zonder rewrite.</summary>
public static class QueryRewriter
{
    public const int MaxQueries = 3;
    public const int MaxTerms = 6;
    private const int MaxNormalizedLength = 300;
    private const int MaxItemLength = 100;

    public const string SystemPrompt = """
        Je bent de zoek-voorbewerker van een Riftbound TCG rules-vraagbaak.
        Je krijgt een gebruikersvraag (Nederlands of Engels, mogelijk met
        typo's of spreektaal). Maak er zoekmateriaal van. De regels en
        kaartteksten zijn Engels, dus alle output is Engels.
        Antwoord UITSLUITEND met JSON:
        {"normalized": "...", "queries": ["..."], "terms": ["..."]}
        - normalized: de vraag met typo's en speltermen gecorrigeerd,
          herschreven als één heldere Engelse zoekzin.
        - queries: 1 tot 3 korte semantische zoekqueries, inclusief
          synoniemen/varianten van speltermen (removal, kill, destroy,
          recall, negate, exhaust, ...).
        - terms: 0 tot 6 korte lexicale zoektermen zoals ze letterlijk in
          kaartteksten staan (bv. "kill a gear", "destroy target gear").
          Alleen bij vragen over kaarten of kaarteffecten; anders [].
        Geen tekst buiten de JSON.
        """;

    public static string BuildPrompt(string question) => $"Vraag: {question}";

    /// <summary>Tolerante JSON-extractie uit een LLM-antwoord; null bij
    /// mislukking of onzin-output (geen zoekzin én geen queries).</summary>
    public static QueryRewrite? Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var normalized =
                root.TryGetProperty("normalized", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()?.Trim()
                    : null;
            if (normalized?.Length > MaxNormalizedLength)
                normalized = normalized[..MaxNormalizedLength];
            var queries = Strings(root, "queries", MaxQueries);
            var terms = Strings(root, "terms", MaxTerms);

            // Onzin-guard: zonder zoekzin én zonder queries valt er niets te
            // verbeteren — de aanroeper gebruikt dan de rauwe vraag.
            if (string.IsNullOrWhiteSpace(normalized) && queries.Length == 0) return null;
            return new QueryRewrite(
                string.IsNullOrWhiteSpace(normalized) ? queries[0] : normalized!,
                queries, terms);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] Strings(JsonElement obj, string key, int max)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return [.. arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim())
            .Where(s => s.Length > 0 && s.Length <= MaxItemLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)];
    }
}
