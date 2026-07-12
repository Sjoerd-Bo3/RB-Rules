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

    /// <summary>Antwoord van het agent-pad (#107): het antwoord plus de
    /// brein-stappen (één regel per tool-call) die rb-ai bij task="agentic"
    /// meestuurt — voedt AskTrace.BrainSteps in het beheer. Answer is null
    /// wanneer de agent faalde maar er wél al tool-calls gedaan waren
    /// (rb-ai's fout-body draagt die steps): de aanroeper draait dan het
    /// vangnet, maar de gedane stappen blijven controleerbaar. Relations
    /// (#120) is het rauwe relatievoorstellen-blok dat rb-ai van het antwoord
    /// afsplitste; null als de agent niets achterliet — de aanroeper parseert
    /// en valideert het (AgenticRelationService).</summary>
    public record AgenticAnswer(string? Answer, string? Steps, string? Relations = null);

    /// <summary>Agentic ask (#107, docs/BRAIN.md §2.4): zelfde /ask-koppelvlak
    /// als <see cref="AskAsync"/> maar met task="agentic" én de tool-call-log
    /// uit de respons. Bij uitval, timeout of leeg antwoord is Answer null
    /// (of het hele resultaat null) — de aanroeper (AskService) draait dan
    /// het vangnet: de klassieke single-pass. De harde rem zit in rb-ai zelf
    /// (maxTurns, tool-cap, 120s-timeout); loopt zelfs die vast, dan maakt
    /// de 6-minuten-HttpClient-timeout hier alsnog een vangnet-null van.</summary>
    public async Task<AgenticAnswer?> AskAgenticAsync(
        string prompt, string? system = null,
        IReadOnlyList<AiImage>? images = null, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                prompt, system, task = "agentic",
                images = images?.Select(i => new { mediaType = i.MediaType, data = i.Data }),
            };
            var res = await http.PostAsJsonAsync("/ask", payload, ct);
            if (!res.IsSuccessStatusCode)
            {
                // rb-ai's fout/timeout-pad stuurt de vóór de uitval gedane
                // tool-calls als steps in de fout-body mee (#107): bewaren,
                // zodat de beheerder juist de mislukte run kan inspecteren.
                logger.LogWarning("rb-ai /ask gaf {Status} (task=agentic)", (int)res.StatusCode);
                var errorBody = await TryReadBodyAsync(res, ct);
                return JoinSteps(errorBody?.Steps) is { } partial
                    ? new AgenticAnswer(null, partial)
                    : null;
            }
            var body = await res.Content.ReadFromJsonAsync<AskResponse>(ct);
            if (string.IsNullOrWhiteSpace(body?.Answer))
                return JoinSteps(body?.Steps) is { } steps ? new AgenticAnswer(null, steps) : null;
            return new AgenticAnswer(body.Answer, JoinSteps(body.Steps),
                string.IsNullOrWhiteSpace(body.Relations) ? null : body.Relations);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient-timeout manifesteert zich als TaskCanceledException
            // zónder geannuleerd token; het catch-filter hieronder zou hem
            // laten doorbubbelen en daarmee het vangnet omzeilen (review
            // #107). Voor het agent-pad is timeout juist hét scenario
            // waarvoor het vangnet bestaat — dus null. Echte client-
            // annulering (ct wél geannuleerd) bubbelt door.
            logger.LogWarning("rb-ai-aanroep verlopen op de HttpClient-timeout (task=agentic)");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "rb-ai-aanroep mislukt (task=agentic)");
            return null;
        }
    }

    /// <summary>Fout-body van rb-ai lezen is best-effort: geen (geldige)
    /// JSON betekent alleen géén steps — de fout zelf is al gelogd.</summary>
    private static async Task<AskResponse?> TryReadBodyAsync(
        HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadFromJsonAsync<AskResponse>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? JoinSteps(string[]? steps)
    {
        if (steps is not { Length: > 0 }) return null;
        var joined = string.Join("\n", steps.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
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

    /// <summary>Steps (#107) en Relations (#120) komen alleen mee bij
    /// task="agentic" en blijven bij alle andere taken afwezig — de
    /// respons-vorm van cheap/hard/research is onveranderd.</summary>
    private record AskResponse(string? Answer, string[]? Steps = null, string? Relations = null);
}
