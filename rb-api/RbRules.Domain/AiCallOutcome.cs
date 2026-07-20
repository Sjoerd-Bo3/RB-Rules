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

    /// <summary>HTTP 429: rate-limit op het abonnement. Plausibel bij lange runs en
    /// het enige geval waar wachten-en-opnieuw-proberen zin heeft.</summary>
    RateLimited,

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

    public void Add(AiCallOutcome outcome) =>
        _counts[outcome] = _counts.GetValueOrDefault(outcome) + 1;

    public int Count(AiCallOutcome outcome) => _counts.GetValueOrDefault(outcome);

    /// <summary>Alle aanroepen die géén bruikbaar antwoord opleverden. <see
    /// cref="AiCallOutcome.Empty"/> telt NIET mee: een geldige lege uitslag is
    /// geslaagd werk, geen uitval.</summary>
    public int Failures =>
        _counts.Where(kv => kv.Key is not (AiCallOutcome.Ok or AiCallOutcome.Empty))
            .Sum(kv => kv.Value);

    /// <summary>Menselijke uitsplitsing van de uitval ("429×12, timeout×4"), of een
    /// lege string als er niets misging. Deterministische volgorde (aflopend aantal,
    /// dan naam) zodat run-details vergelijkbaar blijven.</summary>
    public string Summary => string.Join(", ", _counts
        .Where(kv => kv.Key is not (AiCallOutcome.Ok or AiCallOutcome.Empty) && kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => Label(kv.Key), StringComparer.Ordinal)
        .Select(kv => $"{Label(kv.Key)}×{kv.Value}"));

    private static string Label(AiCallOutcome outcome) => outcome switch
    {
        AiCallOutcome.RateLimited => "429 rate-limit",
        AiCallOutcome.Timeout => "timeout",
        AiCallOutcome.ServerError => "5xx",
        AiCallOutcome.ClientError => "4xx",
        AiCallOutcome.Transport => "onbereikbaar",
        AiCallOutcome.Unparseable => "onleesbaar antwoord",
        _ => outcome.ToString(),
    };
}
