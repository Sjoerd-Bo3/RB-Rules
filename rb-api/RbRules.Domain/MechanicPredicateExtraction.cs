using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Eén geëxtraheerd mechanic-predicaat (tool-output, fase 5, #229, §5): het
/// getypeerde (predicaat, object-token) dat de <see cref="HypothesisEngine"/> voedt.
/// Al door de deterministische muur: predicaat ∈ de gesloten set, object genormaliseerd
/// en niet-leeg.</summary>
public sealed record ExtractedPredicate(string Predicate, string ObjectToken);

/// <summary>Fase 5 (#229, §5) — de extractie-VORM voor het minen van getypeerde
/// mechanic-predicaten, in dezelfde tool-forced structured-output-lijn als de fase-2
/// <see cref="InteractionExtraction"/>. Bouwt het <c>emit_mechanic_predicates</c>-schema
/// waarin <c>predicate</c> een harde enum is van de vier predicaten en <c>object</c> een
/// genormaliseerd token (met het advisieve lexicon als hint, niet als slot — een nieuwe
/// set mag nieuwe events/keywords introduceren, review cureert, CLAUDE.md: mee-evolueren).
/// De deterministische parser (<see cref="Parse"/>) is de tweede muur: onbekende
/// predicaten en lege tokens vallen weg. Puur; de live rb-ai-call is een integratie-
/// follow-up (ARCHITECTURE §8).</summary>
public static class MechanicPredicateExtraction
{
    public const string SystemPrompt = """
        Je extraheert getypeerde eigenschappen van één Riftbound-mechaniek/keyword.
        Roep UITSLUITEND de tool `emit_mechanic_predicates` aan. Per eigenschap:
        - predicate: triggers_on | prevents | grants | requires_target
          * triggers_on: de mechaniek vuurt/reageert op een event of status (bv. exhaust)
          * prevents: de mechaniek voorkomt/annuleert een event of status
          * grants: de mechaniek verleent een keyword/eigenschap (bv. tank, hidden)
          * requires_target: de mechaniek vereist een doel van een type (bv. unit)
        - object: één kort, genormaliseerd token (Engels, lowercase), bv. "exhaust",
          "hidden", "unit". Gebruik bij voorkeur een token uit de gegeven hint-lijst,
          maar een duidelijk nieuw token mag ook.
        Geef alleen eigenschappen die uit de regeltekst blijken; verzin niets.
        """;

    /// <summary>De harde predicaat-enum (dezelfde bron als
    /// <see cref="MechanicPredicateKinds.All"/>).</summary>
    public static IReadOnlyList<string> PredicateEnum => MechanicPredicateKinds.All;

    /// <summary>Bouwt het tool-schema. <paramref name="subjectRef"/>/<paramref name="subjectLabel"/>
    /// benoemen de mechaniek die wordt beschreven (context voor het model); de object-hints
    /// zijn het samengevoegde advisieve lexicon over alle predicaten.</summary>
    public static string BuildToolSchema(string subjectRef, string subjectLabel)
    {
        var objectHints = MechanicPredicateKinds.All
            .SelectMany(MechanicPredicateLexicon.SeedFor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var predicateSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["predicate"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = PredicateEnum.Cast<object?>().ToArray(),
                    ["description"] = "Getypeerd predicaat.",
                },
                ["object"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] = "Genormaliseerd object-token. Hint-lijst: " +
                        string.Join(", ", objectHints) + ".",
                },
            },
            ["required"] = new[] { "predicate", "object" },
        };

        var toolSchema = new Dictionary<string, object?>
        {
            ["name"] = "emit_mechanic_predicates",
            ["description"] = $"Emit getypeerde eigenschappen van {subjectLabel} ({subjectRef}).",
            ["input_schema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["predicates"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = predicateSchema,
                    },
                },
                ["required"] = new[] { "predicates" },
            },
        };

        return JsonSerializer.Serialize(toolSchema);
    }

    /// <summary>De tweede muur: parse de tool-output en gooi weg wat de vorm niet haalt
    /// (onbekend predicaat, leeg object-token). Accepteert de tool-envelop
    /// <c>{"predicates":[…]}</c> én een kale array. Bij <paramref name="knownTokensOnly"/>
    /// worden ook tokens buiten het advisieve lexicon van hun predicaat geweigerd
    /// (strengere modus voor auto-review); default lenient (onbekend token → kandidaat
    /// voor menselijke review, nooit stil weg).</summary>
    public static IReadOnlyList<ExtractedPredicate> Parse(string? raw, bool knownTokensOnly = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonSpan(raw));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return []; }

        var array = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("predicates", out var inner) && inner.ValueKind == JsonValueKind.Array
            ? inner
            : root.ValueKind == JsonValueKind.Array ? root : default;
        if (array.ValueKind != JsonValueKind.Array) return [];

        var seen = new HashSet<(string, string)>();
        var result = new List<ExtractedPredicate>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var predicate = MechanicPredicateKinds.Canonicalize(Str(item, "predicate"));
            if (predicate is null) continue;                                   // harde enum-poort
            var token = MechanicPredicateLexicon.Normalize(Str(item, "object") ?? "");
            if (token.Length == 0) continue;
            if (knownTokensOnly)
            {
                var lexicon = MechanicPredicateLexicon.SeedFor(predicate);
                if (lexicon.Count > 0 &&
                    !lexicon.Any(t => MechanicPredicateLexicon.Normalize(t) == token))
                    continue;
            }
            if (!seen.Add((predicate, token))) continue;                       // dedupe
            result.Add(new ExtractedPredicate(predicate, token));
        }
        return result;
    }

    private static string ExtractJsonSpan(string raw)
    {
        var objStart = raw.IndexOf('{');
        var arrStart = raw.IndexOf('[');
        if (objStart < 0 && arrStart < 0) return raw;
        var useObj = objStart >= 0 && (arrStart < 0 || objStart < arrStart);
        var start = useObj ? objStart : arrStart;
        var end = raw.LastIndexOf(useObj ? '}' : ']');
        return end > start ? raw[start..(end + 1)] : raw;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;
}
