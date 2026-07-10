using System.Text;
using System.Text.Json;

namespace RbRules.Domain;

public record InteractionCandidate(Card A, Card B, string Reason);
public record VerifiedInteraction(string AId, string BId, string Kind, string Explanation);

/// <summary>S3-kandidaatgeneratie + verificatie-parsing. Kandidaten komen uit
/// goedkope overlap-heuristieken (géén brute-force over ~450k paren); alleen
/// LLM-geverifieerde paren worden opgeslagen. Puur en getest.</summary>
public static class InteractionMiner
{
    public const string VerifySystemPrompt = """
        Je beoordeelt mogelijke kaart-interacties in Riftbound TCG. Per paar:
        is er een echte, noemenswaardige interactie? Antwoord UITSLUITEND met JSON:
        [{"a": "<id>", "b": "<id>", "interacts": true|false,
          "kind": "combo|synergy|counter|nonbo", "explanation": "<NL, 1-2 zinnen>"}]
        - combo: samen sterker dan de som (keten/loop)
        - synergy: versterken elkaar duidelijk
        - counter: de één ontkracht de ander
        - nonbo: werken onbedoeld slecht samen
        Wees streng: generieke "past in hetzelfde deck" is GEEN interactie.
        Geen tekst buiten de JSON.
        """;

    /// <summary>Kandidaten: effect van A matcht trigger van B (of vice versa),
    /// of A en B delen een specifieke mechanic. Deterministische volgorde,
    /// gededuped, begrensd.</summary>
    public static IReadOnlyList<InteractionCandidate> FindCandidates(
        IReadOnlyList<Card> cards, int max = 200)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<InteractionCandidate>();

        void Add(Card a, Card b, string reason)
        {
            if (a.RiftboundId == b.RiftboundId) return;
            var key = string.CompareOrdinal(a.RiftboundId, b.RiftboundId) < 0
                ? (a.RiftboundId, b.RiftboundId)
                : (b.RiftboundId, a.RiftboundId);
            if (result.Count < max && seen.Add(key)) result.Add(new(a, b, reason));
        }

        // 1. effect(A) ↔ trigger(B): "kill a unit" → "when a unit dies"
        var byTrigger = new Dictionary<string, List<Card>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cards)
        foreach (var t in c.Triggers ?? [])
        foreach (var keyword in TriggerKeywords(t))
        {
            if (!byTrigger.TryGetValue(keyword, out var list)) byTrigger[keyword] = list = [];
            list.Add(c);
        }
        foreach (var a in cards)
        foreach (var e in a.Effects ?? [])
        foreach (var keyword in TriggerKeywords(e))
        {
            if (!byTrigger.TryGetValue(keyword, out var listeners)) continue;
            foreach (var b in listeners)
                Add(a, b, $"effect '{e}' ↔ trigger-woord '{keyword}'");
        }

        // 2. Gedeelde specifieke mechanics (niet de allergangbaarste)
        var byMechanic = new Dictionary<string, List<Card>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cards)
        foreach (var m in c.Mechanics ?? [])
        {
            if (!byMechanic.TryGetValue(m, out var list)) byMechanic[m] = list = [];
            list.Add(c);
        }
        foreach (var (mechanic, group) in byMechanic.Where(g => g.Value.Count is >= 2 and <= 12))
        foreach (var (a, b) in group.SelectMany((a, i) => group.Skip(i + 1).Select(b => (a, b))))
            Add(a, b, $"gedeelde mechanic '{mechanic}'");

        return result;
    }

    /// <summary>Betekenisdragende woorden uit een trigger/effect-clausule
    /// (stopwoorden eruit) — de match-sleutels tussen effecten en triggers.</summary>
    public static IEnumerable<string> TriggerKeywords(string clause)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "when", "a", "an", "the", "i", "you", "your", "is", "are", "my",
            "to", "of", "this", "that", "it", "on", "at", "in", "or", "and",
        };
        return clause
            .Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stop.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct();
    }

    public static string BuildVerifyPrompt(IEnumerable<InteractionCandidate> batch)
    {
        var sb = new StringBuilder("Kandidaat-paren:\n");
        foreach (var c in batch)
        {
            sb.AppendLine($"- a: {c.A.RiftboundId} ({c.A.Name}) — {c.A.TextPlain ?? "(geen tekst)"}");
            sb.AppendLine($"  b: {c.B.RiftboundId} ({c.B.Name}) — {c.B.TextPlain ?? "(geen tekst)"}");
            sb.AppendLine($"  reden: {c.Reason}");
        }
        return sb.ToString();
    }

    public static IReadOnlyList<VerifiedInteraction> ParseVerified(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var result = new List<VerifiedInteraction>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!(item.TryGetProperty("interacts", out var i) && i.ValueKind == JsonValueKind.True))
                    continue;
                var a = Str(item, "a");
                var b = Str(item, "b");
                var explanation = Str(item, "explanation");
                if (a is null || b is null || explanation is null) continue;
                var kind = Str(item, "kind") is { } k && k is "combo" or "synergy" or "counter" or "nonbo"
                    ? k : "synergy";
                result.Add(new(a, b, kind, explanation));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;
}
