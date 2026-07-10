namespace RbRules.Domain;

/// <summary>Gecomponeerde tekst-representatie van een kaart voor embeddings —
/// naam, type, domains, stats, effecttekst en tags in één string, zodat
/// semantisch zoeken op álle facetten matcht.</summary>
public static class CardText
{
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

        if (!string.IsNullOrWhiteSpace(c.TextPlain)) parts.Add(c.TextPlain!);
        if (c.Tags.Length > 0) parts.Add($"Tags: {string.Join(", ", c.Tags)}");
        return string.Join(" | ", parts);
    }

    /// <summary>Embedding ontbreekt, of is met een ander model gemaakt
    /// (provenance-guard: model-wissel = expliciete her-embed).</summary>
    public static bool NeedsEmbedding(Card c) =>
        c.Embedding is null || c.EmbeddingModel != EmbeddingConfig.Model;
}
