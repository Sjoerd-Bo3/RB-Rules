namespace RbRules.Domain;

/// <summary>Richting van een graph-buurrelatie, bezien vanuit de startknoop.
/// NL conform het contract in docs/BRAIN.md §2.3 ("richting"); de brein-API
/// accepteert "direction" met in/out/both als alias.</summary>
public enum BrainDirection
{
    Beide,
    Uit,
    In,
}

/// <summary>Pure parse-/whitelistlogica voor de brein-API (#105, docs/BRAIN.md
/// §2.3): laag-filter, edge-type-whitelist, richting en de vertaling van
/// route-waarden naar een BrainRef. Alles hier is de poort tussen
/// gebruikersinvoer en query's — Cypher krijgt uitsluitend waarden die door
/// deze whitelists zijn gegaan, nooit rauwe strings.</summary>
public static class BrainQuery
{
    /// <summary>De vijf zoeklagen (§2.3): elk een eigen embedding-tabel.</summary>
    public static readonly string[] Layers = ["rules", "cards", "claims", "primer", "rulings"];

    /// <summary>Alle relatie-types die de graph kent (GraphSyncService +
    /// InteractionService). Whitelist voor het edges-filter: onbekende types
    /// zijn een 400, geen stille lege lijst.</summary>
    public static readonly string[] EdgeTypes =
    [
        "FROM_SET", "HAS_DOMAIN", "HAS_TAG", "HAS_MECHANIC", "INTERACTS_WITH",
        "PART_OF", "EXPLAINS", "ABOUT", "SUPPORTED_BY", "SUPERSEDES", "AFFECTS",
    ];

    /// <summary>Kommagescheiden laag-filter → set van laagnamen. Leeg/afwezig
    /// = alle lagen; een onbekende laag is een fout (de aanroeper hoort te
    /// weten wat hij vraagt — stil negeren verbergt typo's).</summary>
    public static bool TryParseLayers(string? csv, out HashSet<string> layers, out string error)
    {
        layers = [.. Layers];
        error = "";
        if (string.IsNullOrWhiteSpace(csv)) return true;

        var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        var result = new HashSet<string>();
        foreach (var part in parts)
        {
            var match = Layers.FirstOrDefault(l => l.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"onbekende laag '{part}' — geldige lagen: {string.Join(", ", Layers)}";
                return false;
            }
            result.Add(match);
        }
        layers = result;
        return true;
    }

    /// <summary>Kommagescheiden edge-filter → genormaliseerde whitelist-types
    /// (canonieke hoofdletters). Leeg/afwezig = geen filter (lege array).</summary>
    public static bool TryParseEdges(string? csv, out string[] edges, out string error)
    {
        edges = [];
        error = "";
        if (string.IsNullOrWhiteSpace(csv)) return true;

        var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var part in parts)
        {
            var match = EdgeTypes.FirstOrDefault(e => e.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"onbekend edge-type '{part}' — geldige types: {string.Join(", ", EdgeTypes)}";
                return false;
            }
            if (!result.Contains(match)) result.Add(match);
        }
        edges = [.. result];
        return true;
    }

    /// <summary>Richting-parameter (NL per §2.3: in/uit/beide; Engelse
    /// aliassen in/out/both). Leeg/afwezig = beide richtingen.</summary>
    public static bool TryParseRichting(string? value, out BrainDirection direction, out string error)
    {
        direction = BrainDirection.Beide;
        error = "";
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "beide" or "both":
                direction = BrainDirection.Beide;
                return true;
            case "uit" or "out":
                direction = BrainDirection.Uit;
                return true;
            case "in":
                direction = BrainDirection.In;
                return true;
            default:
                error = $"onbekende richting '{value}' — geldig: in, uit, beide";
                return false;
        }
    }

    /// <summary>Neo4j-label per ref-soort. Deze vaste mapping is de enige
    /// tekst die een Cypher-statement in gaat (labels zijn niet
    /// parametriseerbaar) — gebruikersinvoer komt er nooit doorheen.
    /// Ruling heeft bewust geen graph-knoop (geverifieerde rulings leven
    /// alleen in Postgres) → null.</summary>
    public static string? GraphLabel(BrainRefKind kind) => kind switch
    {
        BrainRefKind.Card => "Card",
        BrainRefKind.Mechanic => "Mechanic",
        BrainRefKind.Concept => "Concept",
        BrainRefKind.Section => "RuleSection",
        BrainRefKind.Claim => "Claim",
        BrainRefKind.Source => "Source",
        BrainRefKind.Erratum => "Erratum",
        BrainRefKind.Change => "Change",
        BrainRefKind.Set => "Set",
        BrainRefKind.Domain => "Domain",
        BrainRefKind.Tag => "Tag",
        _ => null,
    };

    /// <summary>Route-waarde → BrainRef. De rb-ai-tools sturen refs
    /// URL-ge-encodeerd in het pad (section:CR/7.4.2 → section%3ACR%2F7.4.2);
    /// ASP.NET decodeert route-values doorgaans al, maar een niet-gedecodeerde
    /// of dubbel-ge-encodeerde variant vangen we hier expliciet af. Let op de
    /// valkuil dat "section:CR%2F101.2" óók letterlijk parsebaar is (met %2F
    /// in de key): een ge-encodeerde slash in de key betekent altijd dat er
    /// nog gedecodeerd moet worden — geen enkele echte key bevat er een.</summary>
    public static bool TryParseRouteRef(string? raw, out BrainRef result)
    {
        var literal = BrainRef.TryParse(raw, out result);
        if (literal && !result.Key.Contains("%2F", StringComparison.OrdinalIgnoreCase))
            return true;
        if (raw is not null && raw.Contains('%') &&
            BrainRef.TryParse(Uri.UnescapeDataString(raw), out var decoded))
        {
            result = decoded;
            return true;
        }
        return literal;
    }
}
