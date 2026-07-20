using System.Net;
using System.Net.Http.Json;
using Pgvector;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Eén embed-aanroep, met de oorzaak erbij (#282). <see cref="Vectors"/> is
/// alléén gevuld bij <see cref="EmbedCallOutcome.Ok"/> — nooit een half resultaat, om
/// dezelfde reden als bij <see cref="AiExtraction"/>: een deels gevulde uitslag zou
/// vectoren aan de verkeerde entiteit koppelen.</summary>
/// <param name="Capped">Hoeveel van de aangeboden teksten door de budget-kap zijn
/// ingekort vóór verzending (#301). 0 = de normale toestand. Dit is de enige manier
/// waarop een aanroeper kán weten dat zijn vector op een deel van de invoer slaat —
/// zonder dit veld zou de kap op servicenniveau precies de stille degradatie zijn die
/// #282/#284 wegnamen.</param>
/// <param name="CappedAt">Op hoeveel tekens er gekapt is — het budget van dat moment,
/// zodat de aanroeper het op de rij kan vastleggen zonder zelf de instellingen te
/// hoeven kennen. 0 als er niets gekapt is.</param>
/// <param name="LongestOriginal">Lengte van de langste ORIGINELE tekst in deze aanroep,
/// zodat de melding "hoe ver eroverheen" kan zeggen (#302).</param>
public sealed record EmbedBatchResult(
    Vector[]? Vectors, EmbedCallOutcome Outcome, int? StatusCode, string? Error,
    int Capped = 0, int CappedAt = 0, int LongestOriginal = 0)
{
    public bool Ok => Outcome == EmbedCallOutcome.Ok && Vectors is not null;
}

/// <summary>Embeddings via lokale Ollama (bge-m3 — meertalig, NL↔EN).
/// Dimensie-guard: een antwoord met de verkeerde dimensie is een harde fout
/// (audit-fix: nooit meer stille dimensie-mixen in pgvector).
///
/// HIER LIGT DE GEHEUGENGARANTIE (#301). #293 zette de kap op het budget in
/// <c>CardEmbeddingPipeline</c> en <c>RuleChunkPipeline</c> en noemde dat "élk verzoek
/// blijft binnen het gemeten veilige bereik" — maar dat gold voor 2 van de ~12
/// aanroepplekken. De overige tien gingen via <see cref="EmbedOneAsync"/>/
/// <see cref="EmbedAsync"/> ongelimiteerd naar Ollama. De reële daarvan is de
/// primer-draft-bewerking in <c>AdminEndpoints</c>: die her-embedt <c>Title + Body</c>
/// nadat een reviewer de tekst heeft geplakt, zónder lengtegrens. 8000 tekens is daar
/// geen mislukte embed maar een OOM-kill van <c>llama-server</c> — een VM-breed
/// geheugenincident (Ollama deelt de 8 GB met Postgres, Neo4j en rb-ai), terwijl de
/// <c>catch</c> op die plek het als een hikje laat ogen.
///
/// Vandaar dat <see cref="TryEmbedAsync"/> zélf kapt én splitst. Een garantie die de
/// aanroeper moet naleven is geen garantie; deze kan niemand omzeilen, want er is geen
/// pad naar Ollama dat er niet doorheen gaat. De pijplijnen kappen nog steeds vóóraf —
/// niet omdat het hier nog nodig is (het is er dan een no-op), maar omdat zij moeten
/// weten WELKE rij gekapt is om dat op de rij vast te leggen (#299).</summary>
public class EmbeddingService(HttpClient http, EmbeddingSettings? settings = null)
{
    private readonly EmbeddingSettings _settings = settings ?? EmbeddingSettings.Default;

    private record EmbedResponse(float[][]? Embeddings);

    /// <summary>Embed, met de UITVALS-OORZAAK als data in plaats van als exception
    /// (#282). Voor pijplijnen die per batch willen doorlopen en achteraf per oorzaak
    /// willen rapporteren; interactieve paden (/ask, zoeken) gebruiken gewoon
    /// <see cref="EmbedAsync"/> en degraderen op de exception.
    ///
    /// Begrenst het verzoek op BEIDE assen (#301), met dezelfde
    /// <see cref="EmbeddingSettings"/> die de pijplijnen gebruiken: elke tekst wordt op
    /// <c>BatchChars</c> gekapt (anders duwt één uitschieter llama-server om) én de
    /// aanroep wordt in deelverzoeken van hoogstens <c>BatchSize</c> teksten /
    /// <c>BatchChars</c> tekens geknipt (anders doet een aanroeper die tien teksten
    /// tegelijk aanbiedt dat alsnog). Per-item kappen alléén zou het gat maar half
    /// dichten: <c>AskService</c> stuurt de query-rewrites in één aanroep, en 10 × 6000
    /// is net zo goed 60000 tekens in één verzoek.
    ///
    /// Deelverzoeken zijn ALLES-OF-NIETS: faalt er één, dan komt die uitkomst terug en
    /// blijven de vectoren leeg. Half terugkomen zou de index-koppeling breken, en dat
    /// is precies de fout die <see cref="EmbedBatchResult"/> uitsluit.</summary>
    public async Task<EmbedBatchResult> TryEmbedAsync(
        string[] texts, CancellationToken ct = default)
    {
        var capped = EmbedBatching.CapItems(texts, _settings.BatchChars);
        var ranges = EmbedBatching.Split(
            capped.Texts, _settings.BatchSize, _settings.BatchChars);

        var vectors = new List<Vector>(texts.Length);
        foreach (var range in ranges)
        {
            var (offset, count) = range.GetOffsetAndLength(capped.Texts.Count);
            var part = await PostAsync([.. capped.Texts.Skip(offset).Take(count)], ct);
            // Ook een MISLUKTE aanroep draagt de kap-feiten mee: een aanroeper die op
            // grond van de uitkomst iets op de rij zet mag niet de indruk krijgen dat
            // er niets gekapt is puur omdat Ollama daarna omviel.
            if (!part.Ok)
                return part with
                {
                    Capped = capped.CappedCount,
                    CappedAt = capped.CappedCount > 0 ? _settings.BatchChars : 0,
                    LongestOriginal = capped.CappedCount > 0 ? capped.LongestOriginal : 0,
                };
            vectors.AddRange(part.Vectors!);
        }

        return new([.. vectors], EmbedCallOutcome.Ok, 200, null,
            capped.CappedCount, capped.CappedCount > 0 ? _settings.BatchChars : 0,
            capped.CappedCount > 0 ? capped.LongestOriginal : 0);
    }

    /// <summary>Eén HTTP-verzoek naar Ollama. Alles wat de grootte bewaakt zit in
    /// <see cref="TryEmbedAsync"/>; hier gaat het alleen nog om de uitkomst.</summary>
    private async Task<EmbedBatchResult> PostAsync(
        string[] texts, CancellationToken ct)
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
                // Ollama's eigen foutbody erbij (#293). Zonder deze regel stond er
                // alleen "Ollama antwoordde 400", en dan is de enige aanwijzing ons
                // eigen label — dat in #293 juist de verkeerde kant op wees. De body
                // zei letterlijk `do embedding request: … EOF`, oftewel: het
                // llama-server-kindproces is tijdens dit verzoek gestorven. Lezen mag
                // de meting niet kunnen slopen, dus de status blijft leidend als het
                // uitlezen zelf mislukt.
                var errorBody = await ReadErrorBodyAsync(res, ct);
                return new(null, outcome, status,
                    $"Ollama antwoordde {status}" + (errorBody is null ? "" : $": {errorBody}"));
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

    /// <summary>De ruwe foutbody, kort gehouden en zonder nieuwe regels — hij landt in
    /// een run_log-regel die een beheerder op één regel leest. <c>null</c> als er niets
    /// te lezen viel; het uitlezen van een foutbody mag zelf nooit de fout worden.</summary>
    private static async Task<string?> ReadErrorBodyAsync(
        HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var one = string.Join(' ', raw.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return one.Length <= 300 ? one : one[..300] + "…";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
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
