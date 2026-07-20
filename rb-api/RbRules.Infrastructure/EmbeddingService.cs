using System.Net;
using System.Net.Http.Json;
using Pgvector;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Eén embed-aanroep, met de oorzaak erbij (#282). <see cref="Vectors"/> is
/// alléén gevuld bij <see cref="EmbedCallOutcome.Ok"/> — nooit een half resultaat, om
/// dezelfde reden als bij <see cref="AiExtraction"/>: een deels gevulde uitslag zou
/// vectoren aan de verkeerde entiteit koppelen.</summary>
public sealed record EmbedBatchResult(
    Vector[]? Vectors, EmbedCallOutcome Outcome, int? StatusCode, string? Error)
{
    public bool Ok => Outcome == EmbedCallOutcome.Ok && Vectors is not null;
}

/// <summary>Embeddings via lokale Ollama (bge-m3 — meertalig, NL↔EN).
/// Dimensie-guard: een antwoord met de verkeerde dimensie is een harde fout
/// (audit-fix: nooit meer stille dimensie-mixen in pgvector).</summary>
public class EmbeddingService(HttpClient http)
{
    private record EmbedResponse(float[][]? Embeddings);

    /// <summary>Embed, met de UITVALS-OORZAAK als data in plaats van als exception
    /// (#282). Voor pijplijnen die per batch willen doorlopen en achteraf per oorzaak
    /// willen rapporteren; interactieve paden (/ask, zoeken) gebruiken gewoon
    /// <see cref="EmbedAsync"/> en degraderen op de exception.</summary>
    public async Task<EmbedBatchResult> TryEmbedAsync(
        string[] texts, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsJsonAsync("/api/embed",
                new { model = EmbeddingConfig.Model, input = texts }, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Client-timeout, geen annulering door de aanroeper. Een model dat na een
            // OOM-kill opnieuw geladen moet worden landt hier.
            return new(null, EmbedCallOutcome.Timeout, null, "verzoek liep in de timeout");
        }
        catch (HttpRequestException ex)
        {
            // Socket/DNS/reset: de container is weg of werd midden in het verzoek
            // herstart — het gezicht van een container-OOM-kill.
            return new(null, EmbedCallOutcome.Transport, null, ex.Message);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                var status = (int)res.StatusCode;
                var outcome = res.StatusCode >= HttpStatusCode.InternalServerError
                    ? EmbedCallOutcome.ServerError
                    : EmbedCallOutcome.ClientError;
                return new(null, outcome, status, $"Ollama antwoordde {status}");
            }

            EmbedResponse? body;
            try
            {
                body = await res.Content.ReadFromJsonAsync<EmbedResponse>(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(null, EmbedCallOutcome.Incomplete, (int)res.StatusCode, ex.Message);
            }

            var embeddings = body?.Embeddings;
            if (embeddings is null || embeddings.Length < texts.Length)
                return new(null, EmbedCallOutcome.Incomplete, (int)res.StatusCode,
                    $"Ollama gaf {embeddings?.Length ?? 0} embeddings voor {texts.Length} teksten " +
                    $"— is het model '{EmbeddingConfig.Model}' gepulld?");

            // Provenance-guard: model + dimensie zijn heilig. Dit blijft een harde
            // fout, nooit een degradatie — liever geen embedding dan een vector in de
            // verkeerde dimensie naast de bestaande vector(1024)-kolom.
            foreach (var e in embeddings)
                if (e.Length != EmbeddingConfig.Dimensions)
                    return new(null, EmbedCallOutcome.DimensionMismatch, (int)res.StatusCode,
                        $"Embedding-dimensie {e.Length} ≠ verwacht {EmbeddingConfig.Dimensions} " +
                        $"(model-mismatch? verwacht {EmbeddingConfig.Model})");

            return new([.. embeddings.Take(texts.Length).Select(e => new Vector(e))],
                EmbedCallOutcome.Ok, (int)res.StatusCode, null);
        }
    }

    /// <summary>Embed of gooi. Ongewijzigd contract voor de interactieve aanroepers
    /// (/ask, regels-zoek, kaart-zoek), die de exception al opvangen en netjes naar
    /// alleen-FTS degraderen.</summary>
    public async Task<Vector[]> EmbedAsync(string[] texts, CancellationToken ct = default)
    {
        var r = await TryEmbedAsync(texts, ct);
        return r.Vectors
            ?? throw new InvalidOperationException(
                $"Embedding mislukt ({r.Outcome}): {r.Error ?? "onbekende fout"}");
    }

    public async Task<Vector> EmbedOneAsync(string text, CancellationToken ct = default) =>
        (await EmbedAsync([text], ct))[0];
}
