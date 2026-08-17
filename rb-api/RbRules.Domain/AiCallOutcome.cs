namespace RbRules.Domain;

/// <summary>De uitkomst van één rb-ai-aanroep (#251) — precies het onderscheid dat
/// een kale <c>null</c> weggooide. Elke mining-run meldde structureel ~45-47%
/// "rb-ai-uitval" zonder dat iemand kon zien of dat rate-limits, timeouts,
/// serverfouten of onbruikbare antwoorden waren; zonder die meting is elke fix
/// gokwerk.</summary>
public enum AiCallOutcome
{
    /// <summary>Bruikbaar antwoord (mogelijk met nul kandidaten — dat is geen
    /// uitval, zie <see cref="Empty"/>).</summary>
    Ok,

    /// <summary>HTTP 429 ZONDER <c>concurrency_limit</c>-code: rate-limit op het
    /// abonnement. Plausibel bij lange runs en een van de gevallen waar wachten-en-
    /// opnieuw-proberen zin heeft.</summary>
    RateLimited,

    /// <summary>HTTP 429 mét <c>{"code":"concurrency_limit"}</c>: ONZE EIGEN
    /// sidecar-cap wees de aanvraag af, niet Anthropic (#279). Het onderscheid is de
    /// hele reden dat deze uitkomst bestaat: op één hoop met <see cref="RateLimited"/>
    /// meet een parallelle mining-run zijn eigen semaphore als "het abonnement
    /// throttlet" — precies de verkeerde conclusie, want de fix is dan de cap of het
    /// aantal workers bijstellen (rb-ai <c>AI_MAX_CONCURRENCY</c> /
    /// <c>BREIN_MINING_CONCURRENCY</c>), niet minder vragen stellen. Deelt wel het
    /// backoff-pad: een slot komt vanzelf vrij.</summary>
    ConcurrencyLimited,

    /// <summary>HTTP 503: rb-ai even vol of aan het opstarten. Deelt het backoff-pad
    /// met <see cref="RateLimited"/> — herstel-gedrag identiek — maar is een EIGEN
    /// uitkomst (#251-review): een nachtrun met 40 sidecar-503's meldde anders "429
    /// rate-limit×40", waaruit de beheerder concludeert dat het abonnement throttlet
    /// en de doorvoer verlaagt, terwijl de container herstartte. Precies de samenval
    /// die #251 wilde opheffen.</summary>
    Overloaded,

    /// <summary>De aanroep liep in de client-timeout (geen annulering door de
    /// aanroeper).</summary>
    Timeout,

    /// <summary>HTTP 5xx — rb-ai's eigen uitvalpad (tool niet geroepen, run gefaald).</summary>
    ServerError,

    /// <summary>Overige non-success statuscode (4xx behalve 429).</summary>
    ClientError,

    /// <summary>De verbinding zelf mislukte (DNS/socket/TLS) — rb-ai onbereikbaar.</summary>
    Transport,

    /// <summary>HTTP 200, maar de body is geen bruikbare JSON — een parse-/schema-
    /// fout, géén netwerk- of capaciteitsprobleem.</summary>
    Unparseable,

    /// <summary>HTTP 200 met een geldige, LEGE body. Bewust apart: dit is een
    /// geslaagde aanroep zonder kandidaten, geen uitval — het onderscheid moet
    /// bewaard blijven (zie de bestaande regressietest op '200 + lege lijst').</summary>
    Empty,
}

/// <summary>Telt de <see cref="AiCallOutcome"/>-uitslagen van één run en vat ze samen
/// voor het run_log/de cockpit (#251). Puur (Domain, geen IO) en bewust mutabel: een
/// mining-lus telt honderden aanroepen op.</summary>
public sealed class AiOutcomeTally
{
    private readonly Dictionary<AiCallOutcome, int> _counts = [];

    /// <summary>Fijnmazige uitvalsoort per uitkomst (#281), zoals rb-ai die in zijn
    /// foutbody meestuurt (<c>reason</c>: <c>max_turns</c>, <c>spawn</c>,
    /// <c>api_error</c>, <c>no_tool_call</c>, …). <see cref="AiCallOutcome"/> zegt op
    /// welke LAAG het misging (5xx = "rb-ai's eigen uitvalpad"); de reden zegt WAAROM,
    /// en dat is een andere knop. Ze staan bewust niet als extra enum-waarden in
    /// <see cref="AiCallOutcome"/>: de vocabulaire hoort bij rb-ai en mag daar
    /// groeien zonder dat rb-api meemigreert — onbekende redenen worden gewoon
    /// meegeteld en getoond.</summary>
    private readonly Dictionary<(AiCallOutcome Outcome, string Reason), int> _reasons = [];

    /// <summary>Verdient deze uitkomst een backoff-herhaling? Rate-limit,
    /// overbelasting én een volle sidecar-cap delen dat pad (een piek mag geen uitval
    /// worden); de rest faalt bij een directe herhaling vrijwel zeker opnieuw.</summary>
    public static bool IsRetryable(AiCallOutcome outcome) =>
        outcome is AiCallOutcome.RateLimited or AiCallOutcome.Overloaded
            or AiCallOutcome.ConcurrencyLimited;

    /// <summary>Tel één uitkomst, optioneel met de fijnmazige reden die rb-ai
    /// meestuurde (#281). Een lege/afwezige reden telt alleen op de uitkomst — dat is
    /// exact het gedrag van vóór #281, zodat een oudere rb-ai (of een pad zonder
    /// reden) dezelfde samenvatting oplevert als altijd.</summary>
    public void Add(AiCallOutcome outcome, string? reason = null)
    {
        _counts[outcome] = _counts.GetValueOrDefault(outcome) + 1;
        if (string.IsNullOrWhiteSpace(reason)) return;
        var key = (outcome, reason.Trim());
        _reasons[key] = _reasons.GetValueOrDefault(key) + 1;
    }

    public int Count(AiCallOutcome outcome) => _counts.GetValueOrDefault(outcome);

    /// <summary>Hoe vaak deze uitkomst met déze reden voorkwam (#281).</summary>
    public int Count(AiCallOutcome outcome, string reason) =>
        _reasons.GetValueOrDefault((outcome, reason));

    /// <summary>Alle aanroepen die géén bruikbaar antwoord opleverden. <see
    /// cref="AiCallOutcome.Empty"/> telt NIET mee: een geldige lege uitslag is
    /// geslaagd werk, geen uitval.</summary>
    public int Failures =>
        _counts.Where(kv => kv.Key is not (AiCallOutcome.Ok or AiCallOutcome.Empty))
            .Sum(kv => kv.Value);

    /// <summary>Menselijke uitsplitsing van de uitval ("429×12, timeout×4"), of een
    /// lege string als er niets misging. Deterministische volgorde (aflopend aantal,
    /// dan naam) zodat run-details vergelijkbaar blijven.
    ///
    /// Droeg rb-ai een reden mee (#281), dan staat die tussen haakjes achter de
    /// uitkomst: <c>5xx×22 (max_turns×14, spawn×8)</c>. Dát is het verschil tussen
    /// "meer dan de helft van de kaarten faalde" en een aanwijsbare knop — precies
    /// wat #281 miste toen de containerlog één regel bevatte. Zonder reden blijft de
    /// tekst byte-gelijk aan die van vóór #281.</summary>
    public string Summary => string.Join(", ", _counts
        .Where(kv => kv.Key is not (AiCallOutcome.Ok or AiCallOutcome.Empty) && kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => Label(kv.Key), StringComparer.Ordinal)
        .Select(kv => $"{Label(kv.Key)}×{kv.Value}{ReasonSuffix(kv.Key)}"));

    /// <summary>De redenen achter één uitkomst, of een lege string als rb-ai er geen
    /// meestuurde. Zelfde deterministische ordening als de hoofd-uitsplitsing.</summary>
    private string ReasonSuffix(AiCallOutcome outcome)
    {
        var parts = _reasons
            .Where(kv => kv.Key.Outcome == outcome && kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Reason, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key.Reason}×{kv.Value}")
            .ToList();
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static string Label(AiCallOutcome outcome) => outcome switch
    {
        AiCallOutcome.RateLimited => "429 rate-limit",
        AiCallOutcome.ConcurrencyLimited => "429 AI-slots vol",
        AiCallOutcome.Overloaded => "503 overbelast",
        AiCallOutcome.Timeout => "timeout",
        AiCallOutcome.ServerError => "5xx",
        AiCallOutcome.ClientError => "4xx",
        AiCallOutcome.Transport => "onbereikbaar",
        AiCallOutcome.Unparseable => "onleesbaar antwoord",
        _ => outcome.ToString(),
    };
}
