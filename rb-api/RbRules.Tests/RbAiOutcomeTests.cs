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

    // ── #281: rb-ai's uitvalsoort reist mee naar het run-detail ──────────────
    //
    // Een mining-run over 40 kaarten meldde "22 rb-ai-uitval (5xx×22)" terwijl
    // `docker logs rb-v2-ai` één regel bevatte. #251 gaf ons de LAAG (5xx), maar niet
    // de oorzaak; rb-ai stuurt die nu als `reason` mee en RbAiClient gooide de
    // foutbody tot nu toe ongelezen weg.

    [Fact]
    public async Task Extractie_500MetReden_DraagtDeRedenMee()
    {
        var ai = Ai(_ => Json(HttpStatusCode.InternalServerError,
            """{"error":"extractie mislukt","reason":"max_turns","detail":"subtype=error_max_turns turns=3"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ServerError, r.Outcome);
        Assert.Equal("max_turns", r.Reason);
        Assert.Null(r.Raw);   // degradatie-contract blijft: geen half feit
    }

    [Fact]
    public async Task Extractie_500ZonderReden_BlijftHetOudeGedrag()
    {
        // Een oudere rb-ai (of een pad zonder reden) mag niets breken.
        var ai = Ai(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ServerError, r.Outcome);
        Assert.Null(r.Reason);
    }

    [Fact]
    public async Task Extractie_KapotteFoutbody_LeestGeenReden_EnBreektNiet()
    {
        // Zelfde defensieve regel als bij de concurrency-code: onleesbaar betekent
        // "geen extra informatie", nooit "kapot".
        var ai = Ai(_ => Json(HttpStatusCode.InternalServerError, "<html>502 nginx</html>"));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ServerError, r.Outcome);
        Assert.Null(r.Reason);
    }

    [Fact]
    public async Task Extractie_429MetCodeEnReden_LeestBeideUitEenLezing()
    {
        // `code` (#279, de sidecar-cap) en `reason` (#281) zitten in dezelfde body;
        // ze mogen elkaar niet in de weg zitten.
        var ai = Ai(_ => Json(HttpStatusCode.TooManyRequests,
            """{"error":"alle AI-slots bezet","code":"concurrency_limit","reason":"concurrency_limit"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.ConcurrencyLimited, r.Outcome);
        Assert.Equal("concurrency_limit", r.Reason);
    }

    // ── #281: een afgekapte extractie is een TIMEOUT, geen generieke 5xx ─────
    //
    // Op productie gereproduceerd: dezelfde kaarttekst, alleen het aantal aangeboden
    // refs verschilt — 3 refs → 200 na 49,0 s, 39 refs → 500 na 92,1 s. Die 500 was
    // onze eigen 90 s-timeout, maar op de draad niet te onderscheiden van "het model
    // riep de tool niet" of "er ging echt iets stuk". rb-ai geeft een afgekapte run
    // nu een 504 + code; deze tests falen zodra dat weer samenvalt.

    [Fact]
    public async Task Extractie_504_IsTimeout_GeenServerfout()
    {
        var ai = Ai(_ => Json(HttpStatusCode.GatewayTimeout,
            """{"error":"extractie afgebroken op de tijdslimiet","code":"extract_timeout","reason":"timeout","detail":"extractie afgebroken na 90s (harde timeout)"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(AiCallOutcome.Timeout, r.Outcome);
        Assert.NotEqual(AiCallOutcome.ServerError, r.Outcome);
        Assert.Equal(504, r.StatusCode);
        Assert.Null(r.Raw);   // degradatie-contract blijft: geen half feit
    }

    [Fact]
    public async Task Extractie_Timeout_WordtNietOpnieuwGeprobeerd()
    {
        // Een run die op de tijdslimiet strandde faalt bij een directe herhaling
        // vrijwel zeker opnieuw — en kost dan twee keer 90 s uit de nachtrun.
        var calls = 0;
        var ai = Ai(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        });

        await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Tally_TimeoutStaatOpZichzelf_NietWeggestoptOnder5xx()
    {
        // Dit is de regressie die #281 moet voorkomen: 22 afgekapte runs die als
        // "5xx×22" gemeld worden sturen de beheerder naar de verkeerde knop.
        var tally = new AiOutcomeTally();
        for (var i = 0; i < 22; i++) tally.Add(AiCallOutcome.Timeout);

        Assert.Equal("timeout×22", tally.Summary);
        Assert.DoesNotContain("5xx", tally.Summary);
    }

    [Fact]
    public async Task Extractie_504MetRedenTimeout_HerhaaltZichzelfNietInDeSamenvatting()
    {
        // "timeout×22 (timeout×22)" is ruis; een reden telt alleen als hij iets
        // TOEVOEGT aan de uitkomst.
        var ai = Ai(_ => Json(HttpStatusCode.GatewayTimeout,
            """{"error":"afgebroken","code":"extract_timeout","reason":"timeout"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });
        var tally = new AiOutcomeTally();
        tally.Add(r.Outcome, r.Reason);

        Assert.Null(r.Reason);
        Assert.Equal("timeout×1", tally.Summary);
    }

    [Fact]
    public async Task Extractie_504MetUpstreamReden_BehoudtDieWel()
    {
        // Wél informatief: de run liep in ONZE timeout, maar de SDK zat al die tijd
        // op een aanhoudende API-fout te wachten. Dat wijst naar een andere knop.
        var ai = Ai(_ => Json(HttpStatusCode.GatewayTimeout,
            """{"error":"afgebroken","code":"extract_timeout","reason":"api_error","detail":"8 SDK-retries"}"""));

        var r = await ai.ExtractStructuredDetailedAsync("/extract/interactions", new { });
        var tally = new AiOutcomeTally();
        tally.Add(r.Outcome, r.Reason);

        Assert.Equal("api_error", r.Reason);
        Assert.Equal("timeout×1 (api_error×1)", tally.Summary);
    }

    [Fact]
    public void Tally_SplitstDeUitvalUitPerReden()
    {
        var tally = new AiOutcomeTally();
        for (var i = 0; i < 14; i++) tally.Add(AiCallOutcome.ServerError, "max_turns");
        for (var i = 0; i < 8; i++) tally.Add(AiCallOutcome.ServerError, "spawn");
        tally.Add(AiCallOutcome.Timeout);

        // Dit is het verschil tussen "22 kaarten faalden" en een aanwijsbare knop.
        Assert.Equal("5xx×22 (max_turns×14, spawn×8), timeout×1", tally.Summary);
        Assert.Equal(23, tally.Failures);
        Assert.Equal(14, tally.Count(AiCallOutcome.ServerError, "max_turns"));
    }

    [Fact]
    public void Tally_ZonderReden_BlijftByteGelijkAanVoor281()
    {
        // De bestaande run-detail-teksten (en de tests die ze vastleggen) mogen niet
        // verschuiven zolang rb-ai geen reden meestuurt.
        var tally = new AiOutcomeTally();
        tally.Add(AiCallOutcome.ServerError);
        tally.Add(AiCallOutcome.ServerError);
        tally.Add(AiCallOutcome.RateLimited, "   ");   // witruimte telt niet als reden

        Assert.Equal("5xx×2, 429 rate-limit×1", tally.Summary);
    }

    [Fact]
    public void Tally_RedenenVanVerschillendeUitkomsten_LopenNietDoorElkaar()
    {
        var tally = new AiOutcomeTally();
        tally.Add(AiCallOutcome.ServerError, "spawn");
        tally.Add(AiCallOutcome.Timeout, "spawn");

        Assert.Equal("5xx×1 (spawn×1), timeout×1 (spawn×1)", tally.Summary);
        Assert.Equal(1, tally.Count(AiCallOutcome.ServerError, "spawn"));
        Assert.Equal(0, tally.Count(AiCallOutcome.ServerError, "max_turns"));
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
