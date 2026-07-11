using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace RbRules.Infrastructure;

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

    private record AskResponse(string? Answer);
}
