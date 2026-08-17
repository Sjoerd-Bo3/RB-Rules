using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Per-kaart-uitslag uit het batch-done-frame van rb-ai (#323).
/// <paramref name="RawInteractions"/> is de rauwe JSON-array van díe kaart —
/// bewust rauw doorgegeven, zodat de bestaande tweede muur
/// (<see cref="InteractionExtraction.ParseDetailed"/>) ONGEWIJZIGD per kaart
/// tegen het vocabulaire van die kaart draait; deze parser valideert alleen de
/// envelop-vorm. Null bij <paramref name="Ok"/> = false.</summary>
public sealed record BatchCardResult(
    string Code, bool Ok, string? Reason, string? RawInteractions);

/// <summary>Het geparste done-frame van <c>/extract/interactions/batch</c>:
/// per kaart een uitslag, plus de sessie-maten (geweigerde onbekende codes,
/// token-usage). <paramref name="InputTokens"/>/<paramref name="OutputTokens"/>
/// zijn null wanneer rb-ai geen usage meldde — onbekend, niet 0.</summary>
public sealed record BatchDoneEnvelope(
    IReadOnlyList<BatchCardResult> Results, int UnknownCode,
    long? InputTokens, long? OutputTokens);

/// <summary>De envelop-parser voor het batch-antwoord (#323) — dezelfde
/// defensieve rol als <see cref="InteractionExtraction.ParseDetailed"/> op het
/// losse pad: een afgekapte body of schema-drift levert <c>null</c> (uitval,
/// géén leeg resultaat), zodat de mining-lus dat als
/// <c>AiCallOutcome.Unparseable</c> telt en GEEN watermark zet.</summary>
public static class InteractionBatchExtraction
{
    public static BatchDoneEnvelope? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<BatchCardResult>();
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var code = Str(item, "code");
                if (code is null) return null;
                var ok = item.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.True;
                if (ok)
                {
                    // De rauwe array reist door naar de per-kaart-parser; een
                    // ok-kaart ZONDER array is envelop-drift → hele parse kapot,
                    // want stil "geen kandidaten" van maken zou uitval maskeren.
                    if (!item.TryGetProperty("interactions", out var ix)
                        || ix.ValueKind != JsonValueKind.Array)
                        return null;
                    list.Add(new(code, true, null, ix.GetRawText()));
                }
                else
                {
                    list.Add(new(code, false, Str(item, "reason"), null));
                }
            }

            var unknown = root.TryGetProperty("unknownCode", out var u)
                && u.ValueKind == JsonValueKind.Number && u.TryGetInt32(out var n)
                ? n : 0;

            long? input = null, output = null;
            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("inputTokens", out var i)
                    && i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out var iv))
                    input = iv;
                if (usage.TryGetProperty("outputTokens", out var o)
                    && o.ValueKind == JsonValueKind.Number && o.TryGetInt64(out var ov))
                    output = ov;
            }

            return new(list, unknown, input, output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}
