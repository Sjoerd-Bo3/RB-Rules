using System.Text.Json;

namespace RbRules.Domain;

public record ExtractedBan(string Name, string Kind);
public record ExtractedErratum(string CardName, string NewText);

/// <summary>Prompts + parsers voor LLM-extractie van de banlijst en errata uit
/// officiële pagina-tekst. Puur en getest; de LLM-call loopt via rb-ai.</summary>
public static class BanErrataExtractor
{
    public const string BanSystemPrompt = """
        Je krijgt tekst van de officiële Riftbound Rules Hub. Extraheer de
        ACTUELE banlijst voor constructed. Antwoord UITSLUITEND met JSON:
        [{"name": "...", "kind": "card"}] — kind ∈ card | battlefield.
        Alleen daadwerkelijk verboden kaarten/battlefields; geen voorbeelden of
        historische vermeldingen. Lege lijst [] als er geen bans staan.
        Geen tekst buiten de JSON.
        """;

    public const string ErrataSystemPrompt = """
        Je krijgt tekst van een Riftbound errata-pagina; de eerste regel
        ("Bron: …") vermeldt om welke bron/set het gaat. Extraheer per kaart de
        actuele (post-errata) rules-tekst. Antwoord UITSLUITEND met JSON:
        [{"cardName": "...", "newText": "..."}]. Alleen kaarten waarvan de
        tekst daadwerkelijk gewijzigd is. Lege lijst [] als er geen errata staan.
        Geen tekst buiten de JSON.
        """;

    /// <summary>Prompt-invoer past ruim in het cheap-model-budget; errata-
    /// en banlijst-pagina's zijn korter dan dit.</summary>
    public const int MaxPageTextLength = 12000;

    /// <summary>Invoer voor de errata-extractie (#94): de bronnaam als eerste
    /// regel — de per-set-pagina's benoemen hun set niet altijd expliciet in
    /// de lopende tekst, de bronnaam ("Origins Card Errata …") draagt die
    /// context — gevolgd door de (afgekapte) paginatekst.</summary>
    public static string BuildErrataInput(string sourceName, string pageText) =>
        $"Bron: {sourceName}\n\n{pageText[..Math.Min(pageText.Length, MaxPageTextLength)]}";

    public static IReadOnlyList<ExtractedBan> ParseBans(string raw) =>
        ParseArray(raw, item =>
        {
            var name = GetString(item, "name");
            if (name is null) return null;
            var kind = GetString(item, "kind");
            return new ExtractedBan(name, kind is "battlefield" ? "battlefield" : "card");
        });

    public static IReadOnlyList<ExtractedErratum> ParseErrata(string raw) =>
        ParseArray(raw, item =>
        {
            var name = GetString(item, "cardName");
            var text = GetString(item, "newText");
            return name is null || text is null ? null : new ExtractedErratum(name, text);
        });

    private static IReadOnlyList<T> ParseArray<T>(string raw, Func<JsonElement, T?> map)
        where T : class
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            return [.. doc.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(map)
                .OfType<T>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;
}
