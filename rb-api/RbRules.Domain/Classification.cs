using System.Text.Json;

namespace RbRules.Domain;

public record Classification(string ChangeType, string Severity, string Summary, string Meaning);

/// <summary>Prompt + parser voor AI-classificatie van een wijziging (rb-ai doet
/// de LLM-call; dit deel is puur en getest).</summary>
public static class Classifier
{
    public const string SystemPrompt = """
        Je bent een Riftbound TCG regels-analist. Je krijgt een diff van een
        regelbron. Classificeer de wijziging en antwoord UITSLUITEND met JSON:
        {"change_type": "...", "severity": "...", "summary": "...", "meaning": "..."}
        - change_type ∈ ban | errata | core-rule | tournament-rule | set-release | editorial
        - severity ∈ high (verandert legaliteit/interactie) | medium (verduidelijking) | low (redactioneel)
        - summary: korte, feitelijke samenvatting (NL)
        - meaning: "wat betekent dit voor spelers" in 1-2 zinnen (NL)
        Geen tekst buiten de JSON.
        """;

    public static string BuildPrompt(string sourceName, string diff) =>
        $"Bron: {sourceName}\n\nDiff:\n{diff[..Math.Min(diff.Length, 4000)]}";

    /// <summary>Tolerante JSON-extractie uit een LLM-antwoord; null bij mislukking.</summary>
    public static Classification? Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            string Get(string key, string fallback) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? fallback
                    : fallback;

            var severity = Get("severity", "medium");
            if (severity is not ("high" or "medium" or "low")) severity = "medium";
            return new Classification(
                Get("change_type", "unknown"),
                severity,
                Get("summary", ""),
                Get("meaning", ""));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
