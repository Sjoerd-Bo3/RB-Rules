using System.Net.Http.Json;

namespace RbRules.Infrastructure;

/// <summary>Client voor de rb-ai sidecar (Claude Agent SDK op abonnement).
/// Best-effort: AI-uitval mag een scan nooit breken.</summary>
public class RbAiClient(HttpClient http)
{
    public async Task<string?> AskAsync(
        string prompt, string? system = null, string task = "cheap",
        CancellationToken ct = default)
    {
        try
        {
            var res = await http.PostAsJsonAsync("/ask", new { prompt, system, task }, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<AskResponse>(ct);
            return string.IsNullOrWhiteSpace(body?.Answer) ? null : body.Answer;
        }
        catch
        {
            return null;
        }
    }

    private record AskResponse(string? Answer);
}
