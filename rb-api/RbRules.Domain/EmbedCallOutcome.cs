namespace RbRules.Domain;

/// <summary>De uitkomst van één Ollama-embed-aanroep (#282) — hetzelfde onderscheid
/// dat <see cref="AiCallOutcome"/> in #251 voor rb-ai maakte, nu voor de embed-poort.
/// Aanleiding: de kernel schoot <c>llama-server</c> af op ~2,5 GB (de cgroup-cap van
/// <c>rb-v2-ollama</c>), waarna de embed-stap stilviel. Dat werd alleen zichtbaar
/// doordat iemand toevallig <c>dmesg</c> las: de aanroeper ving een kale exception en
/// logde hooguit "Ollama onbereikbaar?" naar de containerlog. Zonder oorzaak-meting
/// is elke fix gokwerk — een OOM-kill (5xx/afgebroken verbinding), een niet-gepulld
/// model (404) en een dimensie-mismatch vragen elk een ándere ingreep.</summary>
public enum EmbedCallOutcome
{
    /// <summary>Bruikbare vectoren terug, in de juiste dimensie en het juiste aantal.</summary>
    Ok,

    /// <summary>HTTP 5xx. Het typische OOM-gezicht: Ollama zelf leeft nog, maar zijn
    /// model-runner is door de cgroup-OOM-killer afgeschoten en het verzoek faalt.</summary>
    ServerError,

    /// <summary>De aanroep liep in de client-timeout (geen annulering door de
    /// aanroeper). Een model dat na een kill opnieuw geladen moet worden kan hier
    /// landen.</summary>
    Timeout,

    /// <summary>De verbinding zelf mislukte (DNS/socket/reset) — de container is weg
    /// of werd midden in het verzoek herstart. Onderscheiden van <see
    /// cref="ServerError"/>: dít is de container-kill, dát is de runner-kill.</summary>
    Transport,

    /// <summary>Non-success 4xx: verkeerd verzoek of — het praktijkgeval — het model
    /// is niet gepulld. Wachten helpt hier niet, pullen wel.</summary>
    ClientError,

    /// <summary>HTTP 200, maar Ollama gaf geen of te weinig embeddings terug voor de
    /// aangeboden teksten. Géén capaciteitsprobleem maar een contractbreuk; stil
    /// doorlopen zou vectoren aan de verkeerde kaart plakken.</summary>
    Incomplete,

    /// <summary>HTTP 200 met vectoren in een ANDERE dimensie dan
    /// <see cref="EmbeddingConfig.Dimensions"/>. De provenance-guard: model + dimensie
    /// zijn heilig, dus dit is een harde, zichtbare fout en nooit een degradatie.</summary>
    DimensionMismatch,
}

/// <summary>Telt de <see cref="EmbedCallOutcome"/>-uitslagen van één embed-run en vat
/// ze samen voor het run_log (#282) — dezelfde vorm als <see cref="AiOutcomeTally"/>,
/// zodat een run-detail van beide poorten hetzelfde leest. Puur (Domain, geen IO) en
/// bewust mutabel: een pijplijn telt tientallen batches op.</summary>
public sealed class EmbedOutcomeTally
{
    private readonly Dictionary<EmbedCallOutcome, int> _calls = [];
    private int _textsLost;

    /// <summary>Boek één aanroep. <paramref name="texts"/> is het aantal teksten dat
    /// in die aanroep zat: bij uitval zijn dat precies de kaarten/chunks die zónder
    /// embedding blijven staan, en dat getal is wat de beheerder wil zien.</summary>
    public void Add(EmbedCallOutcome outcome, int texts = 1)
    {
        _calls[outcome] = _calls.GetValueOrDefault(outcome) + 1;
        if (outcome != EmbedCallOutcome.Ok) _textsLost += texts;
    }

    public int Count(EmbedCallOutcome outcome) => _calls.GetValueOrDefault(outcome);

    /// <summary>Aantal aanroepen dat geen bruikbare vectoren opleverde.</summary>
    public int Failures =>
        _calls.Where(kv => kv.Key != EmbedCallOutcome.Ok).Sum(kv => kv.Value);

    /// <summary>Aantal teksten (kaarten/chunks) dat door die uitval zónder embedding
    /// bleef en dus bij een volgende run opnieuw aan de beurt komt.</summary>
    public int TextsLost => _textsLost;

    public bool HasFailures => Failures > 0;

    /// <summary>Menselijke uitsplitsing van de uitval ("5xx×3, onbereikbaar×1"), of een
    /// lege string als er niets misging. Deterministische volgorde (aflopend aantal,
    /// dan naam) zodat run-details vergelijkbaar blijven — zelfde afspraak als
    /// <see cref="AiOutcomeTally.Summary"/>.</summary>
    public string Summary => string.Join(", ", _calls
        .Where(kv => kv.Key != EmbedCallOutcome.Ok && kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => Label(kv.Key), StringComparer.Ordinal)
        .Select(kv => $"{Label(kv.Key)}×{kv.Value}"));

    private static string Label(EmbedCallOutcome outcome) => outcome switch
    {
        EmbedCallOutcome.ServerError => "5xx (model-runner omgevallen?)",
        EmbedCallOutcome.Timeout => "timeout",
        EmbedCallOutcome.Transport => "onbereikbaar",
        EmbedCallOutcome.ClientError => "4xx (model niet gepulld?)",
        EmbedCallOutcome.Incomplete => "onvolledig antwoord",
        EmbedCallOutcome.DimensionMismatch => "dimensie-mismatch",
        _ => outcome.ToString(),
    };
}
