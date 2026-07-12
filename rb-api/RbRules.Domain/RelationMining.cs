using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eén door de LLM voorgesteld relatievoorstel (#116), na validatie
/// tegen de aangeboden refs maar vóór dedupe tegen de database.</summary>
public record ExtractedRelation(string FromRef, string ToRef, string Kind, string Explanation);

/// <summary>Prompt + parser + kind-normalisatie voor de relatie-mining (#116).
/// Puur en getest; de LLM-calls lopen via rb-ai. Zelfde discipline als
/// ClaimMiner (#93-lessen): tolerante JSON-vondst via de gedeelde LlmJson,
/// uitval of onzin-output ⇒ null en de aanroeper logt de rauwe respons.
/// Kern-invariant: from/to moeten refs zijn die de prompt zélf aanbood —
/// gehallucineerde knopen komen de database nooit in.</summary>
public static partial class RelationMiner
{
    /// <summary>Cap per extractie-call: liever een handvol sterke relaties
    /// dan een lijst die de reviewqueue verstopt.</summary>
    public const int MaxRelations = 12;
    public const int MaxExplanationLength = 300;
    public const int MaxKindLength = 40;

    /// <summary>Basis-vocabulaire van relatie-kinds (issue #116); groeit via
    /// geaccepteerde RelationKinds (patroon MechanicMiner.SeedVocabulary).
    /// Alles genormaliseerd (kleine letters).</summary>
    public static readonly string[] SeedKinds =
    [
        "counters", "enables", "versterkt", "wordt beperkt door",
        "vereist", "verduidelijkt",
    ];

    public const string ExtractionSystemPrompt = """
        Je ontdekt relaties in de kennisbank van Riftbound, het League of
        Legends trading card game van Riot Games. Je krijgt een context met
        knopen (elk met een ref zoals mechanic:Deflect of section:bron/7.4)
        en brontekst. Stel betekenisvolle relaties tussen die knopen voor.
        Antwoord UITSLUITEND met JSON:
        {"relations": [{"from": "<ref>", "to": "<ref>", "kind": "...", "explanation": "..."}]}
        - from/to: EXACT een ref uit de meegegeven lijst — verzin er geen
        - kind: de relatiesoort, kort en herbruikbaar, kleine letters.
          Gebruik bij voorkeur: {KINDS}. Alleen als geen daarvan past mag je
          één nieuw, generiek kind introduceren.
        - explanation: 1-2 zinnen Nederlands (Engelse speltermen onvertaald)
          die de relatie onderbouwen vanuit de meegegeven context
        - alleen relaties die de context aantoonbaar onderbouwt; geen open
          deuren die de graph al kent (een kaart heeft zijn eigen mechaniek,
          een concept legt zijn eigen secties al uit)
        - maximaal 12 sterke relaties; niets bruikbaars? {"relations": []}
        Geen tekst buiten de JSON.
        """;

    /// <summary>Effectief kind-vocabulaire: seed + geaccepteerde kinds,
    /// genormaliseerd en gededuped (patroon MechanicMiner.Vocabulary).</summary>
    public static IReadOnlyList<string> KindVocabulary(IEnumerable<string>? accepted = null)
    {
        if (accepted is null) return SeedKinds;
        var seen = new HashSet<string>(SeedKinds, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(SeedKinds);
        foreach (var kind in accepted)
        {
            if (NormalizeKind(kind) is { } k && seen.Add(k)) result.Add(k);
        }
        return result;
    }

    public static string GetSystemPrompt(IEnumerable<string>? acceptedKinds = null) =>
        ExtractionSystemPrompt.Replace(
            "{KINDS}", string.Join(", ", KindVocabulary(acceptedKinds)));

    /// <summary>Vergelijk-/opslagvorm van een kind-label: kleine letters,
    /// samengevouwen whitespace (underscores tellen als spaties), zonder
    /// leestekens aan de randen. null = onbruikbaar (leeg of te lang) — een
    /// kind is een kort, herbruikbaar label, geen zin.</summary>
    public static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var normalized = WhitespaceRegex()
            .Replace(kind.Replace('_', ' '), " ")
            .Trim()
            .Trim('.', ',', '!', '?', '"', '\'', ':', ';')
            .Trim()
            .ToLowerInvariant();
        return normalized.Length is 0 or > MaxKindLength ? null : normalized;
    }

    /// <summary>Prompt voor één mining-eenheid: de contextregels dragen per
    /// knoop de ref + een korte beschrijving; de parser accepteert daarna
    /// alléén die refs.</summary>
    public static string BuildPrompt(string anchorLabel, IEnumerable<string> contextLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Anker: {anchorLabel}");
        sb.AppendLine();
        sb.AppendLine("Knopen en context:");
        foreach (var line in contextLines) sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>Tolerante extractie van relatievoorstellen. null bij
    /// mislukking (geen bruikbare JSON — de aanroeper logt de rauwe respons,
    /// #93); een lege lijst betekent "geparsed, niets gevonden". Voorstellen
    /// met refs buiten <paramref name="offeredRefs"/>, zelf-relaties, een
    /// onbruikbaar kind of zonder uitleg vallen weg; duplicaten binnen het
    /// antwoord (zelfde from|to|kind) ook. Refs worden gecanonicaliseerd naar
    /// de aangeboden spelling zodat de graph-projectie ze exact matcht.</summary>
    public static IReadOnlyList<ExtractedRelation>? ParseRelations(
        string raw, IReadOnlyCollection<string> offeredRefs)
    {
        var items = LlmJson.ExtractItems(raw, "relations");
        if (items is null) return null;

        var offered = new HashSet<string>(offeredRefs, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExtractedRelation>();
        foreach (var item in items)
        {
            if (result.Count >= MaxRelations) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var from = GetString(item, "from");
            var to = GetString(item, "to");
            var kind = NormalizeKind(GetString(item, "kind"));
            var explanation = GetString(item, "explanation");
            if (from is null || to is null || kind is null || explanation is null) continue;
            if (explanation.Length > MaxExplanationLength)
                explanation = explanation[..MaxExplanationLength];

            // Alleen aangeboden refs (canonieke spelling wint) — nooit
            // gehallucineerde knopen, nooit een relatie met zichzelf.
            if (!offered.TryGetValue(from, out var fromCanonical)) continue;
            if (!offered.TryGetValue(to, out var toCanonical)) continue;
            if (string.Equals(fromCanonical, toCanonical, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(DedupeKey(fromCanonical, toCanonical, kind))) continue;

            result.Add(new ExtractedRelation(fromCanonical, toCanonical, kind, explanation));
        }
        return result;
    }

    /// <summary>Idempotentie-sleutel over runs heen: gericht (A counters B is
    /// niet B counters A), case-ongevoelig op de refs, kind al genormaliseerd.
    /// Eerder verworpen relaties blijven zo verworpen — zelfde voorstel komt
    /// niet opnieuw de queue in (MechanicKeyword-afspraak).</summary>
    public static string DedupeKey(string fromRef, string toRef, string kind) =>
        $"{fromRef.ToLowerInvariant()}|{toRef.ToLowerInvariant()}|{kind}";

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim())
            : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>Projectie-filter voor de graph-rebuild (#116): welke relaties
/// worden een RELATES_TO-edge. Puur en getest — de architectuurregel
/// ("LLM-relaties gaan nooit rechtstreeks de graph in") krijgt hier zijn
/// poort: rejected nooit; accepted en unreviewed (mét status-label als
/// edge-property) alleen wanneer hun kind in het geaccepteerde vocabulaire
/// (seed + geaccepteerde RelationKinds) staat.</summary>
public static class RelationProjection
{
    public static bool ShouldProject(
        string status, string kind, IReadOnlySet<string> acceptedKinds) =>
        status is "accepted" or "unreviewed" && acceptedKinds.Contains(kind);

    /// <summary>Geaccepteerd vocabulaire als set voor <see cref="ShouldProject"/>
    /// (case-ongevoelig; kinds zijn genormaliseerd opgeslagen).</summary>
    public static IReadOnlySet<string> AcceptedKindSet(IEnumerable<string> acceptedKinds) =>
        new HashSet<string>(RelationMiner.KindVocabulary(acceptedKinds), StringComparer.OrdinalIgnoreCase);
}
