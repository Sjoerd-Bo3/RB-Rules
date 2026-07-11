using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RbRules.Infrastructure;

/// <summary>Eén NDJSON-frame uit de rb-ai-stream (#31):
/// delta (tekststukje), done (volledig antwoord) of error.</summary>
public record AiStreamFrame(string Type, string? Text = null, string? Answer = null, string? Error = null)
{
    /// <summary>Parse één NDJSON-regel; null bij lege of onleesbare regels
    /// (kapotte frames zijn gedegradeerd gedrag, geen crash).</summary>
    public static AiStreamFrame? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Prop(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            var type = Prop("type");
            return type is null ? null : new AiStreamFrame(type, Prop("text"), Prop("answer"), Prop("error"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Client voor de rb-ai sidecar (Claude Agent SDK op abonnement).
/// Best-effort: AI-uitval mag een scan nooit breken.</summary>
public class RbAiClient(HttpClient http, ILogger<RbAiClient> logger)
{
    /// <summary>Gedeelde fallback-tekst bij AI-uitval (één plek, #44).</summary>
    public const string UnavailableAnswer = "AI is niet beschikbaar — probeer het later opnieuw.";

    public record AiImage(string MediaType, string Data);

    public async Task<string?> AskAsync(
        string prompt, string? system = null, string task = "cheap",
        IReadOnlyList<AiImage>? images = null, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                prompt, system, task,
                images = images?.Select(i => new { mediaType = i.MediaType, data = i.Data }),
            };
            var res = await http.PostAsJsonAsync("/ask", payload, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<AskResponse>(ct);
            return string.IsNullOrWhiteSpace(body?.Answer) ? null : body.Answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Annulering hoort door te bubbelen; andere fouten zijn
            // gedegradeerd gedrag maar nooit onzichtbaar (review-fix).
            logger.LogWarning(ex, "rb-ai-aanroep mislukt (task={Task})", task);
            return null;
        }
    }

    /// <summary>Streamende variant van <see cref="AskAsync"/> (#31): levert de
    /// NDJSON-frames van rb-ai's /ask/stream één voor één op. Uitval is —
    /// net als bij AskAsync — verwacht pad: de enumeratie eindigt dan met een
    /// error-frame in plaats van een exception, zodat de aanroeper netjes kan
    /// degraderen (zelfde antwoordtekst als de niet-streamende route).</summary>
    public async IAsyncEnumerable<AiStreamFrame> AskStreamAsync(
        string prompt, string? system = null, string task = "cheap",
        IReadOnlyList<AiImage>? images = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var res = await OpenStreamAsync(prompt, system, task, images, ct);
        if (res is null)
        {
            yield return new AiStreamFrame("error", Error: "rb-ai onbereikbaar");
            yield break;
        }
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (true)
        {
            string? line = null;
            var broken = false;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Verbinding halverwege weggevallen: degradeer, verlies niets stil.
                logger.LogWarning(ex, "rb-ai-stream afgebroken (task={Task})", task);
                broken = true;
            }
            if (broken)
            {
                yield return new AiStreamFrame("error", Error: "rb-ai-stream afgebroken");
                yield break;
            }
            if (line is null) yield break;
            if (AiStreamFrame.Parse(line) is { } frame) yield return frame;
        }
    }

    /// <summary>Verbinding opzetten met rb-ai's /ask/stream; null bij uitval
    /// (gelogd) — apart van de enumerator omdat yield niet in een catch mag.</summary>
    private async Task<HttpResponseMessage?> OpenStreamAsync(
        string prompt, string? system, string task,
        IReadOnlyList<AiImage>? images, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                prompt, system, task,
                images = images?.Select(i => new { mediaType = i.MediaType, data = i.Data }),
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "/ask/stream")
            {
                Content = JsonContent.Create(payload),
            };
            // ResponseHeadersRead: frames doorgeven zodra ze binnenkomen, niet
            // pas als de hele response klaar is — anders streamt er niets.
            var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode) return res;
            logger.LogWarning("rb-ai /ask/stream gaf {Status} (task={Task})", (int)res.StatusCode, task);
            res.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "rb-ai-streamaanroep mislukt (task={Task})", task);
            return null;
        }
    }

    private record AskResponse(string? Answer);
}
