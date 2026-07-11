using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Gecomponeerde tekst-representatie van een kaart voor embeddings —
/// naam, type, domains, stats, effecttekst en tags in één string, zodat
/// semantisch zoeken op álle facetten matcht.</summary>
public static partial class CardText
{
    /// <summary>Basisnaam zonder printing-suffix: Riot noemt varianten
    /// "Naam (Alternate Art)" / "(Signature)" / "(Overnumbered)" — voor
    /// variant-groepering telt alleen de kaartnaam zelf.</summary>
    public static string BaseName(string name) =>
        PrintingSuffix().Replace(name.Trim(), "");

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex PrintingSuffix();

    /// <summary>Icon-tokens uit Riot-teksten leesbaar maken:
    /// ":rb_energy_2:" → "(2)", ":rb_exhaust:" → "[exhaust]".</summary>
    public static string? HumanizeIcons(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = EnergyToken().Replace(text, "($1)");
        t = IconToken().Replace(t, m => $"[{m.Groups[1].Value.Replace('_', ' ')}]");
        return Regex.Replace(t, @"\s{2,}", " ").Trim();
    }

    [GeneratedRegex(@":rb_energy_(\d+):")]
    private static partial Regex EnergyToken();

    [GeneratedRegex(@":rb_([a-z0-9_]+):")]
    private static partial Regex IconToken();

    public static string Compose(Card c)
    {
        var parts = new List<string> { c.Name };
        var typeLine = string.Join(" ", new[] { c.Supertype, c.Type }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (typeLine.Length > 0) parts.Add(typeLine);
        if (c.Domains.Length > 0) parts.Add($"Domains: {string.Join(", ", c.Domains)}");

        var stats = new List<string>();
        if (c.Energy is not null) stats.Add($"Energy {c.Energy}");
        if (c.Might is not null) stats.Add($"Might {c.Might}");
        if (c.Power is not null) stats.Add($"Power {c.Power}");
        if (stats.Count > 0) parts.Add(string.Join(", ", stats));

        // Leesbare icon-tekst voor het embeddingmodel (geen :rb_…:-tokens).
        if (!string.IsNullOrWhiteSpace(c.TextPlain)) parts.Add(HumanizeIcons(c.TextPlain)!);
        if (c.Tags.Length > 0) parts.Add($"Tags: {string.Join(", ", c.Tags)}");
        return string.Join(" | ", parts);
    }

    /// <summary>Embedding ontbreekt, of is met een ander model gemaakt
    /// (provenance-guard: model-wissel = expliciete her-embed).</summary>
    public static bool NeedsEmbedding(Card c) =>
        c.Embedding is null || c.EmbeddingModel != EmbeddingConfig.Model;

    /// <summary>Canonieke groeps-id van een kaart (variantgroepering).</summary>
    public static string CanonicalId(Card c) => c.VariantOf ?? c.RiftboundId;

    /// <summary>Uniforme kaartbeschrijving voor LLM-prompts (review-fix #44:
    /// bestond op drie plekken met inconsistent resultaat, o.a. rauwe
    /// icon-tokens richting het model).</summary>
    public static string DescribeForPrompt(Card c, bool banned = false, string? effectiveText = null)
    {
        var text = effectiveText ?? c.TextPlain;
        return $"{c.Name} — {string.Join(" ", new[] { c.Supertype, c.Type }.Where(s => s != null))}. " +
               $"Domains: {string.Join(", ", c.Domains)}. " +
               $"Energy {c.Energy?.ToString() ?? "—"}, Might {c.Might?.ToString() ?? "—"}. " +
               (c.Mechanics is { Length: > 0 } m ? $"Mechanieken: {string.Join(", ", m)}. " : "") +
               (banned ? "STAAT OP DE BANLIJST. " : "") +
               (text is null ? "" : $"Tekst: {HumanizeIcons(text[..Math.Min(text.Length, 240)])}");
    }

    /// <summary>Geordend kaartpaar (a &lt; b) — voor interacties en uitleg-cache.</summary>
    public static (string A, string B) OrderedPair(string idA, string idB) =>
        string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);
}
