using System.Net.Http.Json;
using Pgvector;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Embeddings via lokale Ollama (bge-m3 — meertalig, NL↔EN).
/// Dimensie-guard: een antwoord met de verkeerde dimensie is een harde fout
/// (audit-fix: nooit meer stille dimensie-mixen in pgvector).</summary>
public class EmbeddingService(HttpClient http)
{
    private record EmbedResponse(float[][]? Embeddings);

    public async Task<Vector[]> EmbedAsync(string[] texts, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("/api/embed",
            new { model = EmbeddingConfig.Model, input = texts }, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<EmbedResponse>(ct)
            ?? throw new InvalidOperationException("Ollama gaf geen antwoord");
        var embeddings = body.Embeddings
            ?? throw new InvalidOperationException(
                $"Ollama gaf geen embeddings — is het model '{EmbeddingConfig.Model}' gepulld?");

        return [.. embeddings.Select(e =>
            e.Length == EmbeddingConfig.Dimensions
                ? new Vector(e)
                : throw new InvalidOperationException(
                    $"Embedding-dimensie {e.Length} ≠ verwacht {EmbeddingConfig.Dimensions} " +
                    $"(model-mismatch? verwacht {EmbeddingConfig.Model})"))];
    }

    public async Task<Vector> EmbedOneAsync(string text, CancellationToken ct = default) =>
        (await EmbedAsync([text], ct))[0];
}
