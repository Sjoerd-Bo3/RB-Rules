using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#251 — de uitvals-oorzaak van een rb-ai-extractie. Elke mining-run
/// meldde structureel ~45-47% "rb-ai-uitval" terwijl <c>RbAiClient</c> zowel bij een
/// HTTP-fout als bij een onbruikbaar antwoord een kale <c>null</c> gaf: rate-limits,
/// timeouts en parsefouten waren niet te onderscheiden. Zonder die meting is elke
/// fix gokwerk — deze tests leggen het onderscheid vast.</summary>
public class RbAiOutcomeTests
{
    // ── Client: oorzaak per uitslag ──────────────────────────────────────────

    [Fact]
    public async Task Extractie_200MetEnvelop_IsOk()
    {
        var ai = Ai(_ => Json(HttpStatusCode.OK, """{"interactions":[]}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Ok, r.Outcome);
        Assert.Equal("""{"interactions":[]}""", r.Raw);
        Assert.Equal(200, r.StatusCode);
    }

    [Fact]
    public async Task Extractie_200MetOnleesbareBody_IsParsefout_GeenServerfout()
    {
        // Tool-forced output die niet valideert kwam vroeger als dezelfde null
        // binnen als een 500 — terwijl de fix een heel andere is (prompt/schema
        // i.p.v. capaciteit).
        var ai = Ai(_ => Json(HttpStatusCode.OK, "Sorry, ik kan die tool niet aanroepen."));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Unparseable, r.Outcome);
        Assert.Null(r.Raw);   // degradatie-contract blijft: geen half feit
    }

    [Fact]
    public async Task Extractie_500_IsServerfout()
    {
        var ai = Ai(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ServerError, r.Outcome);
        Assert.Equal(500, r.StatusCode);
    }

    [Fact]
    public async Task Extractie_Timeout_IsTimeout_GeenTransportfout()
    {
        // HttpClient-timeout = TaskCanceledException zonder geannuleerd token.
        var ai = Ai(_ => throw new TaskCanceledException("timeout"));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Timeout, r.Outcome);
    }

    [Fact]
    public async Task Extractie_VerbindingWeg_IsTransportfout()
    {
        var ai = Ai(_ => throw new HttpRequestException("connection refused"));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Transport, r.Outcome);
    }

    [Fact]
    public async Task Extractie_EchteAnnulering_Bubbelt_Door()
    {
        // Een door de aanroeper geannuleerde run mag NIET als "uitval" verdwijnen.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ai = Ai(_ => throw new TaskCanceledException("geannuleerd"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ai.ExtractStructuredDetailedAsync("/extract/interactions", new { }, cts.Token));
    }

    // ── Gerichte fix: backoff + retry bij rate-limit ─────────────────────────

    [Fact]
    public async Task Extractie_429_WordtOpnieuwGeprobeerd_EnSlaagtAlsnog()
    {
        var calls = 0;
        var ai = Ai(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json(HttpStatusCode.OK, """{"interactions":[]}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(2, calls);
        Assert.Equal(AiCallOutcome.Ok, r.Outcome);
    }

    [Fact]
    public async Task Extractie_AanhoudendeRateLimit_IsBounded_EnMeldt429()
    {
        var calls = 0;
        var ai = Ai(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        // Bounded: een nachtrun mag niet uren op één kaart blijven hangen.
        Assert.Equal(3, calls);
        Assert.Equal(AiCallOutcome.RateLimited, r.Outcome);
        Assert.Null(r.Raw);
    }

    // ── #279: onze eigen cap ≠ Anthropic's rate-limit ────────────────────────
    //
    // Beide komen als 429 binnen. Zonder onderscheid meet een parallelle mining-run
    // zijn eigen semaphore als "het abonnement throttlet" — en dan is de conclusie
    // ("minder vragen stellen") precies verkeerd: de fix is de cap of het aantal
    // workers bijstellen.

    [Fact]
    public async Task Extractie_429MetConcurrencyCode_IsSidecarCap_GeenRateLimit()
    {
        var ai = Ai(_ => Json(HttpStatusCode.TooManyRequests,
            """{"error":"alle AI-slots bezet (cap 5)","code":"concurrency_limit"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ConcurrencyLimited, r.Outcome);
        Assert.Equal(429, r.StatusCode);
        Assert.Null(r.Raw);   // degradatie-contract blijft: geen half feit
    }

    [Fact]
    public async Task Extractie_429ZonderCode_BlijftRateLimit()
    {
        // Anthropic's eigen 429 draagt onze machine-leesbare code niet.
        var ai = Ai(_ => Json(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.RateLimited, r.Outcome);
    }

    [Fact]
    public async Task Extractie_429MetKapotteBody_ValtTerugOpRateLimit()
    {
        // De meet-verfijning mag zelf nooit een uitvalpad worden: onleesbaar =
        // "geen bewijs voor de sidecar-cap", niet "kapot".
        var ai = Ai(_ => Json(HttpStatusCode.TooManyRequests, "<html>nginx</html>"));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.RateLimited, r.Outcome);
    }

    [Fact]
    public async Task Extractie_VolleSlots_WordtOpnieuwGeprobeerd_EnSlaagtAlsnog()
    {
        // Wachten helpt hier per definitie: een slot komt vanzelf vrij.
        var calls = 0;
        var ai = Ai(_ => ++calls == 1
            ? Json(HttpStatusCode.TooManyRequests, """{"code":"concurrency_limit"}""")
            : Json(HttpStatusCode.OK, """{"interactions":[]}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(2, calls);
        Assert.Equal(AiCallOutcome.Ok, r.Outcome);
    }

    [Fact]
    public void Tally_ToontVolleSlotsApartVanRateLimit()
    {
        var tally = new AiOutcomeTally();
        tally.Add(AiCallOutcome.ConcurrencyLimited);
        tally.Add(AiCallOutcome.ConcurrencyLimited);
        tally.Add(AiCallOutcome.RateLimited);

        // Op één hoop zou hier "429 rate-limit×3" staan — de meting die #279 juist
        // moet kunnen weerleggen.
        Assert.Equal("429 AI-slots vol×2, 429 rate-limit×1", tally.Summary);
        Assert.Equal(3, tally.Failures);
    }

    [Fact]
    public async Task Extractie_RetryAfterHeader_WintVanDeEigenBackoff()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;
        var ai = Ai(_ =>
        {
            if (++calls > 1) return Json(HttpStatusCode.OK, "[]");
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(7));
            return res;
        });
        ai.RetryDelay = (d, _) => { waits.Add(d); return Task.CompletedTask; };

        await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(waits));
    }

    [Fact]
    public async Task Extractie_5xxWordtNietHerhaald()
    {
        // Alleen rate-limit is het wachten waard; een 500 faalt bij een directe
        // herhaling vrijwel zeker opnieuw (en kost dan dubbel).
        var calls = 0;
        var ai = Ai(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task KorteOverload_BlijftHetBestaandeContract()
    {
        // De bestaande aanroepers krijgen nog steeds gewoon de body of null.
        Assert.Equal("[]", await Ai(_ => Json(HttpStatusCode.OK, "[]"))
            .ExtractStructuredAsync("/extract/interactions", new { }));
        Assert.Null(await Ai(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .ExtractStructuredAsync("/extract/interactions", new { }));
    }

    // ── Tally: de samenvatting die in het run-detail/de cockpit landt ────────

    [Fact]
    public void Tally_SplitstDeUitvalUit_EnTeltLeegNietAlsUitval()
    {
        var tally = new AiOutcomeTally();
        for (var i = 0; i < 12; i++) tally.Add(AiCallOutcome.RateLimited);
        tally.Add(AiCallOutcome.Timeout);
        tally.Add(AiCallOutcome.Timeout);
        tally.Add(AiCallOutcome.Unparseable);
        tally.Add(AiCallOutcome.Ok);
        tally.Add(AiCallOutcome.Empty);   // geldig, leeg antwoord = geslaagd werk

        Assert.Equal(15, tally.Failures);
        Assert.Equal("429 rate-limit×12, timeout×2, onleesbaar antwoord×1", tally.Summary);
    }

    // ── #251-review: 503 is overbelasting, geen rate-limit ──────────────────────
    // 503 werd bewust op het backoff-pad gezet, maar deelde dáármee ook de MELDING:
    // een nachtrun waarin de sidecar 40× herstartte rapporteerde "429 rate-limit×40",
    // waaruit de beheerder concludeert dat het abonnement throttlet en de doorvoer
    // verlaagt. Precies de samenval die #251 wilde opheffen.
    [Fact]
    public async Task Extractie_503_IsOverbelasting_NietAlsRateLimitGemeld()
    {
        var calls = 0;
        var ai = Ai(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Overloaded, r.Outcome);
        Assert.Equal(503, r.StatusCode);
        // Het herstelgedrag blijft ongewijzigd: 503 deelt het backoff-pad met 429.
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Tally_ScheidtOverbelastingVanRateLimit()
    {
        var tally = new AiOutcomeTally();
        tally.Add(AiCallOutcome.Overloaded);
        tally.Add(AiCallOutcome.Overloaded);
        tally.Add(AiCallOutcome.RateLimited);

        Assert.Equal(3, tally.Failures);
        Assert.Equal("503 overbelast×2, 429 rate-limit×1", tally.Summary);
    }

    [Fact]
    public void Tally_ZonderUitval_HeeftEenLegeSamenvatting()
    {
        var tally = new AiOutcomeTally();
        tally.Add(AiCallOutcome.Ok);
        tally.Add(AiCallOutcome.Empty);

        Assert.Equal(0, tally.Failures);
        Assert.Equal("", tally.Summary);
    }

    // ── testinfra ────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Client met een gestubde handler én een no-op backoff, zodat de
    /// retry-logica zonder echte vertraging getoetst wordt.</summary>
    private static RbAiClient Ai(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance)
        {
            RetryDelay = (_, _) => Task.CompletedTask,
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
