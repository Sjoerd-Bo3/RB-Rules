using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Eén brein-extractie-aanroep mét haar uitslag (#251): de rauwe body
/// (<c>null</c> bij uitval — het bestaande degradatie-contract), de OORZAAK, en de
/// statuscode waar er een was. Zo kan de mining-orkestratie de uitval per oorzaak
/// optellen in plaats van alleen te tellen dát het misging.
///
/// <paramref name="Reason"/> (#281) is rb-ai's eigen, fijnmazige uitvalsoort uit de
/// foutbody (<c>max_turns</c>, <c>spawn</c>, <c>api_error</c>, <c>no_tool_call</c>,
/// <c>timeout</c>, …). <see cref="AiCallOutcome"/> zegt op welke laag het misging,
/// de reden zegt waarom — zonder dat laatste was "5xx×22" alles wat een run-detail
/// over 55% uitval kon melden. Null bij een geslaagde call of een oudere rb-ai.</summary>
public sealed record AiExtraction(
    string? Raw, AiCallOutcome Outcome, int? StatusCode, string? Reason = null);

/// <summary>Echte token-tellingen van één rb-ai-call (#121): input telt de
/// cache-tokens mee (rb-ai's volume-maat), bij multi-turn-taken opgeteld over
/// alle beurten. Null waar usage hoort betekent: rb-ai (of een oudere versie
/// ervan) gaf niets terug — onbekend, niet 0.</summary>
public record AiUsage(long InputTokens, long OutputTokens);

/// <summary>Eén NDJSON-frame uit de rb-ai-stream (#31):
/// delta (tekststukje), done (volledig antwoord, met usage #121) of error.</summary>
public record AiStreamFrame(
    string Type, string? Text = null, string? Answer = null, string? Error = null,
    AiUsage? Usage = null)
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
            return type is null
                ? null
                : new AiStreamFrame(
                    type, Prop("text"), Prop("answer"), Prop("error"), ParseUsage(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Usage uit het done-frame — best-effort (#121): een frame
    /// zonder (of met een kapot) usage-object blijft gewoon bruikbaar.</summary>
    private static AiUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        return u.TryGetProperty("inputTokens", out var input)
               && input.ValueKind == JsonValueKind.Number && input.TryGetInt64(out var inputTokens)
               && u.TryGetProperty("outputTokens", out var output)
               && output.ValueKind == JsonValueKind.Number && output.TryGetInt64(out var outputTokens)
            ? new AiUsage(inputTokens, outputTokens)
            : null;
    }
}

/// <summary>Client voor de rb-ai sidecar (Claude Agent SDK op abonnement).
/// Best-effort: AI-uitval mag een scan nooit breken.</summary>
public class RbAiClient(HttpClient http, ILogger<RbAiClient> logger)
{
    /// <summary>Gedeelde fallback-tekst bij AI-uitval (één plek, #44).</summary>
    public const string UnavailableAnswer = "AI is niet beschikbaar — probeer het later opnieuw.";

    public record AiImage(string MediaType, string Data);

    /// <summary>Antwoord + token-usage van één /ask-call (#121). Usage is
    /// null bij een oude rb-ai zonder usage-veld — geen breuk.</summary>
    public record AiAnswer(string Answer, AiUsage? Usage);

    public async Task<string?> AskAsync(
        string prompt, string? system = null, string task = "cheap",
        IReadOnlyList<AiImage>? images = null, CancellationToken ct = default,
        string? model = null) =>
        (await AskWithUsageAsync(prompt, system, task, images, ct, model))?.Answer;

    /// <summary>Als <see cref="AskAsync"/>, maar mét de token-usage uit de
    /// respons (#121) — voor aanroepers die per vraag kosten boeken
    /// (AskService → ask_metric). Overige aanroepers blijven op AskAsync.
    /// <paramref name="model"/> is de model-sweep-override (#174): alleen
    /// gezet door een benchmarkrun (AskService.AskOptions.Model) — null
    /// betekent het bestaande gedrag (rb-ai's MODEL[task]).</summary>
    public async Task<AiAnswer?> AskWithUsageAsync(
        string prompt, string? system = null, string task = "cheap",
        IReadOnlyList<AiImage>? images = null, CancellationToken ct = default,
        string? model = null)
    {
        try
        {
            var payload = new
            {
                prompt, system, task, model,
                images = images?.Select(i => new { mediaType = i.MediaType, data = i.Data }),
            };
            var res = await http.PostAsJsonAsync("/ask", payload, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<AskResponse>(ct);
            return string.IsNullOrWhiteSpace(body?.Answer)
                ? null
                : new AiAnswer(body.Answer, body.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Annulering hoort door te bubbelen; andere fouten zijn
            // gedegradeerd gedrag maar nooit onzichtbaar (review-fix).
            logger.LogWarning(ex, "rb-ai-aanroep mislukt (task={Task})", task);
            return null;
        }
    }

    /// <summary>Tool-forced brein-extractie (#226, §3.1): POST naar een van de
    /// rb-ai-extractie-endpoints (<c>/extract/interactions</c>, <c>/extract/predicates</c>)
    /// en geef de rauwe JSON-body terug (de <c>{"interactions":[…]}</c>/<c>{"predicates":[…]}</c>-
    /// envelop die de Domain-parser als tweede muur consumeert). Best-effort: elke
    /// non-success of exception → <c>null</c>, zodat de mining-orkestratie netjes
    /// degradeert (null → geen half feit). De endpoint zelf antwoordt met 500 bij
    /// AI-uitval (tool niet geroepen/run gefaald) en met 200 + lege lijst wanneer er
    /// simpelweg geen kandidaten waren — dat onderscheid blijft bewaard: 200 → een
    /// (mogelijk lege) parse, 500/null → degradatie.
    ///
    /// Aanroepers die WÍLLEN weten waarom het misging gebruiken
    /// <see cref="ExtractStructuredDetailedAsync"/> (#251); deze overload blijft de
    /// korte vorm voor wie alleen de body nodig heeft.</summary>
    public async Task<string?> ExtractStructuredAsync(
        string path, object payload, CancellationToken ct = default) =>
        (await ExtractStructuredDetailedAsync(path, payload, ct)).Raw;

    /// <summary>Als <see cref="ExtractStructuredAsync"/>, maar met de UITVALSOORZAAK
    /// erbij (#251). Elke mining-run meldde structureel ~45-47% "rb-ai-uitval" zonder
    /// dat te zien was of dat rate-limits, timeouts, serverfouten of onbruikbare
    /// antwoorden waren — en zonder die meting is elke fix gokwerk. De uitslag komt
    /// als <see cref="AiCallOutcome"/> terug (met de statuscode waar die er is) zodat
    /// de orkestratie hem kan optellen en in het run-detail/de cockpit tonen.
    ///
    /// Eén gerichte fix zit hier meteen in: 429 (rate-limit) en 503 worden met een
    /// bounded backoff opnieuw geprobeerd — dat zijn de enige uitslagen waar wachten
    /// zin heeft, en <c>Retry-After</c> van rb-ai wint van onze eigen backoff. Alle
    /// andere oorzaken worden alleen gemeten, niet geraden: de vervolgfix volgt de
    /// meting.</summary>
    public async Task<AiExtraction> ExtractStructuredDetailedAsync(
        string path, object payload, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                res = await http.PostAsJsonAsync(path, payload, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient-timeout manifesteert zich als TaskCanceledException
                // ZONDER geannuleerd token (zelfde onderscheid als het agent-pad).
                // Echte annulering door de aanroeper bubbelt door.
                logger.LogWarning("rb-ai {Path} verlopen op de HttpClient-timeout", path);
                return new(null, AiCallOutcome.Timeout, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "rb-ai-extractie mislukt (path={Path})", path);
                return new(null, AiCallOutcome.Transport, null);
            }

            using (res)
            {
                var status = (int)res.StatusCode;
                if (res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    // 200 met een onleesbare body is een parse-/schemafout, geen
                    // capaciteitsprobleem — dat onderscheid is precies waar #251 om
                    // vraagt. Een geldige maar lege envelop blijft Ok: de parser
                    // maakt er nul kandidaten van, en dat is geen uitval.
                    if (!LooksLikeJson(body))
                    {
                        logger.LogWarning(
                            "rb-ai {Path} gaf 200 met een onleesbare body ({Length} tekens)",
                            path, body?.Length ?? 0);
                        return new(null, AiCallOutcome.Unparseable, status);
                    }
                    return new(body, AiCallOutcome.Ok, status);
                }

                var outcome = Classify(res.StatusCode);
                // De foutbody één keer lezen en er beide machine-leesbare velden uit
                // halen (#281): `code` (de sidecar-cap, #279) en `reason` (rb-ai's
                // uitvalsoort). Best-effort — een onleesbare body levert simpelweg
                // geen van beide op.
                var error = await ReadErrorAsync(res, ct);
                // 429 komt uit twee heel verschillende bronnen (#279): Anthropic's
                // rate-limit op het abonnement, óf onze eigen semaphore in rb-ai die
                // alle slots bezet zag. Alleen die laatste draagt een machine-leesbare
                // code — zonder dit onderscheid meet een parallelle mining-run zijn
                // eigen cap als "de LLM is overbelast".
                if (outcome == AiCallOutcome.RateLimited && error.Code == ConcurrencyLimitCode)
                    outcome = AiCallOutcome.ConcurrencyLimited;
                // Alleen rate-limit/overbelasting is het wachten waard; de rest faalt
                // bij een directe herhaling vrijwel zeker opnieuw.
                if (AiOutcomeTally.IsRetryable(outcome) && attempt < MaxAttempts)
                {
                    var wait = RetryAfter(res) ?? Backoff(attempt);
                    logger.LogWarning(
                        "rb-ai {Path} gaf {Status}; poging {Attempt}/{Max} na {Wait}s",
                        path, status, attempt, MaxAttempts, wait.TotalSeconds);
                    await RetryDelay(wait, ct);
                    continue;
                }

                // De reden hoort in de logregel (#281): een kale "gaf 500" was
                // precies wat het diagnosticeren van 55% uitval onmogelijk maakte.
                // rb-ai redacteert zijn detail-tekst al (werkafspraak 7).
                logger.LogWarning(
                    "rb-ai {Path} gaf {Status} (reden={Reason}: {Detail})",
                    path, status, error.Reason ?? "onbekend", error.Detail ?? "-");
                return new(null, outcome, status, Distinct(outcome, error.Reason));
            }
        }
    }

    /// <summary>Aantal pogingen bij een rate-limit (de eerste meegeteld). Bewust laag:
    /// een lange nachtrun mag niet uren op één kaart blijven hangen — wat blijft
    /// falen komt gewoon de volgende run terug (het watermark bewaart de voortgang).</summary>
    private const int MaxAttempts = 3;

    /// <summary>Test-seam voor de backoff-wachttijd; productie wacht echt. Een
    /// unit-test zet hier een no-op zodat de retry-logica zonder vertraging
    /// getoetst kan worden.</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelay { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>rb-ai's eigen <c>Retry-After</c> wint van onze backoff — die weet
    /// beter wanneer het venster weer open is. Absurde waarden worden gekapt zodat
    /// een kapotte header geen run kan gijzelen.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        var after = res.Headers.RetryAfter;
        var delay = after?.Delta
            ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return delay is { } d && d > TimeSpan.Zero
            ? (d > MaxRetryAfter ? MaxRetryAfter : d)
            : null;
    }

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>De machine-leesbare velden uit een rb-ai-foutbody: <c>code</c> (de
    /// sidecar-cap, #279) en <c>reason</c>/<c>detail</c> (de uitvalsoort, #281). Alle
    /// drie mogen ontbreken.</summary>
    private readonly record struct AiError(string? Code, string? Reason, string? Detail);

    /// <summary>Lees de foutbody van rb-ai uit. Best-effort en bewust defensief: een
    /// onleesbare of lege body betekent alleen "geen extra informatie" en laat de
    /// uitslag staan zoals de statuscode hem al classificeerde — een meet-verfijning
    /// mag nooit zelf een uitvalpad worden. Eén lezing voor beide velden, zodat er
    /// geen tweede pad ontstaat dat de body opnieuw moet buffer'en.</summary>
    private static async Task<AiError> ReadErrorAsync(
        HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!LooksLikeJson(body)) return default;
            using var doc = JsonDocument.Parse(body!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            string? Text(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString() : null;
            return new(Text("code"), Text("reason"), Text("detail"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    /// <summary>De machine-leesbare code die rb-ai's <c>ConcurrencyLimitError</c>
    /// meestuurt (rb-ai/src/concurrency.ts) — één string, twee kanten.</summary>
    private const string ConcurrencyLimitCode = "concurrency_limit";

    /// <summary>Laat een reden weg die niets toevoegt aan de uitkomst (#281).
    ///
    /// Een 504 met <c>reason: "timeout"</c> zou anders als "timeout×22 (timeout×22)"
    /// in het run-detail landen — ruis die de uitsplitsing juist onleesbaar maakt.
    /// Zodra rb-ai wél iets extra's weet (een timeout die in werkelijkheid een
    /// aanhoudende API-fout was: "timeout×22 (api_error×14)") blijft de reden
    /// gewoon staan. Puur cosmetisch; de meting zelf verandert niet.</summary>
    private static string? Distinct(AiCallOutcome outcome, string? reason) =>
        reason is not null && string.Equals(reason, outcome.ToString(),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : reason;

    private static AiCallOutcome Classify(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.TooManyRequests => AiCallOutcome.RateLimited,
        System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.GatewayTimeout =>
            AiCallOutcome.Timeout,
        // 503 hoort bij 5xx maar is qua herstel een rate-limit-achtig signaal
        // (rb-ai even vol of aan het opstarten) — zelfde backoff-pad, zodat een piek
        // geen uitval wordt, maar een EIGEN uitkomst zodat het run-detail "503
        // overbelast" meldt en niet "429 rate-limit" (#251-review).
        System.Net.HttpStatusCode.ServiceUnavailable => AiCallOutcome.Overloaded,
        >= System.Net.HttpStatusCode.InternalServerError => AiCallOutcome.ServerError,
        _ => AiCallOutcome.ClientError,
    };

    /// <summary>Goedkope vorm-toets: begint de body met een JSON-object of -array?
    /// De echte poort blijft de Domain-parser (defense-in-depth); dit onderscheidt
    /// alleen "rb-ai gaf proza/niets" van "rb-ai gaf een envelop".</summary>
    private static bool LooksLikeJson(string? body)
    {
        var trimmed = body?.TrimStart();
        return trimmed is { Length: > 0 } && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    /// <summary>Antwoord van het agent-pad (#107): het antwoord plus de
    /// brein-stappen (één regel per tool-call) die rb-ai bij task="agentic"
    /// meestuurt — voedt AskTrace.BrainSteps in het beheer. Answer is null
    /// wanneer de agent faalde maar er wél al tool-calls gedaan waren
    /// (rb-ai's fout-body draagt die steps): de aanroeper draait dan het
    /// vangnet, maar de gedane stappen blijven controleerbaar. Relations
    /// (#120) is het rauwe relatievoorstellen-blok dat rb-ai van het antwoord
    /// afsplitste; null als de agent niets achterliet — de aanroeper parseert
    /// en valideert het (AgenticRelationService). Usage (#121) is de
    /// opgetelde token-teller over alle agent-beurten; null bij een oude
    /// rb-ai of wanneer de run faalde vóór het result-bericht.</summary>
    public record AgenticAnswer(
        string? Answer, string? Steps, string? Relations = null, AiUsage? Usage = null);

    /// <summary>Agentic ask (#107, docs/BRAIN.md §2.4): zelfde /ask-koppelvlak
    /// als <see cref="AskAsync"/> maar met task="agentic" én de tool-call-log
    /// uit de respons. Bij uitval, timeout of leeg antwoord is Answer null
    /// (of het hele resultaat null) — de aanroeper (AskService) draait dan
    /// het vangnet: de klassieke single-pass. De harde rem zit in rb-ai zelf
    /// (maxTurns, tool-cap, 120s-timeout); loopt zelfs die vast, dan maakt
    /// de 6-minuten-HttpClient-timeout hier alsnog een vangnet-null van.
    /// <paramref name="model"/> is de model-sweep-override (#174, zie
    /// AskWithUsageAsync) — ook de agentic-gate kan tijdens een benchmarkrun
    /// escaleren (#158 onderdrukt alleen leer-/meetneveneffecten, niet de
    /// escalatie zelf), dus de override reist ook hierheen door.</summary>
    public async Task<AgenticAnswer?> AskAgenticAsync(
        string prompt, string? system = null,
        IReadOnlyList<AiImage>? images = null, CancellationToken ct = default,
        string? model = null)
    {
        try
        {
            var payload = new
            {
                prompt, system, task = "agentic", model,
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
                string.IsNullOrWhiteSpace(body.Relations) ? null : body.Relations,
                body.Usage);
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

    /// <summary>Voorverwarmsignaal (#154): meldt rb-ai dat de /ask-pagina
    /// geladen is zodat de warme-sessie-pool alvast een SDK-subprocess kan
    /// booten. Volledig stil bij uitval — dit mag nooit iets breken — en
    /// intern op 2s gekapt: rb-ai antwoordt direct met een 202 (de boot
    /// loopt daar op de achtergrond), dus langer wachten heeft geen zin.</summary>
    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await http.PostAsync("/prewarm", content: null, cts.Token);
            // Statuscode bewust niet gecheckt: elk antwoord is goed genoeg.
        }
        catch (Exception ex)
        {
            // Ook annulering slikken: een afgebroken paginalaad mag geen
            // exception het request-pad in duwen — prewarm is best-effort.
            logger.LogDebug(ex, "rb-ai /prewarm niet bereikbaar (stil genegeerd)");
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
    /// task="agentic" en blijven bij alle andere taken afwezig; Usage (#121)
    /// komt bij elke taak mee, maar best-effort — een oude rb-ai zonder
    /// usage-veld levert gewoon null op.</summary>
    private record AskResponse(
        string? Answer, string[]? Steps = null, string? Relations = null, AiUsage? Usage = null);
}
