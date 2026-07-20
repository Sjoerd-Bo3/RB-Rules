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

    /// <summary>Non-success 4xx. De eerste lezing (#282) was "het model is niet
    /// gepulld"; het praktijkgeval (#293) bleek iets ánders en de hint stuurde de
    /// beheerder precies de verkeerde kant op. Gemeten op productie: het model stónd
    /// er (<c>bge-m3:latest</c>, 1,2 GB), en tóch gaf een verzoek van 8000 tekens 3 van
    /// de 3 keer een 400 met foutbody <c>do embedding request: … EOF</c>. Die EOF is
    /// Ollama's kindproces <c>llama-server</c> dat STERFT tijdens het verzoek — de
    /// OOM-teller (<c>dmesg | grep -c llama-server</c>) liep tijdens die meetreeks van
    /// 10 naar 30. Een 4xx van Ollama betekent hier dus "de backend is overleden onder
    /// geheugendruk", en de invoergrootte is de knop die dat bepaalt. Een ontbrekend
    /// model geeft óók een 4xx, dus beide blijven mogelijk — vandaar dat het label
    /// gokt noch verzwijgt en de RUWE foutbody meestuurt.</summary>
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
    /// <summary>Hoeveel tekens ruwe foutbody er per oorzaak in de samenvatting mee mag.
    /// Ollama's fouten zijn kort (<c>do embedding request: … EOF</c> is ~90 tekens);
    /// dit is de rem voor het geval er ooit een HTML-foutpagina uit komt.</summary>
    private const int MaxDetailChars = 160;

    private readonly Dictionary<EmbedCallOutcome, int> _calls = [];
    private readonly Dictionary<EmbedCallOutcome, string> _details = [];
    private int _textsLost;

    /// <summary>Boek één aanroep. <paramref name="texts"/> is het aantal teksten dat
    /// in die aanroep zat: bij uitval zijn dat precies de kaarten/chunks die zónder
    /// embedding blijven staan, en dat getal is wat de beheerder wil zien.</summary>
    /// <param name="detail">De ruwe foutmelding van de poort (#293). Alleen de EERSTE
    /// per oorzaak wordt bewaard: bij 40 identieke OOM-batches wil je één keer lezen
    /// wát Ollama zei, niet veertig keer hetzelfde. De ruwe tekst is hier waardevoller
    /// dan onze duiding ervan — juist het gokken naar een oorzaak ("model niet
    /// gepulld?") kostte in #293 een verkeerde zoekrichting.</param>
    public void Add(EmbedCallOutcome outcome, int texts = 1, string? detail = null)
    {
        _calls[outcome] = _calls.GetValueOrDefault(outcome) + 1;
        if (outcome == EmbedCallOutcome.Ok) return;
        _textsLost += texts;
        if (!string.IsNullOrWhiteSpace(detail) && !_details.ContainsKey(outcome))
            _details[outcome] = Trim(detail);
    }

    private static string Trim(string detail)
    {
        var one = string.Join(' ', detail.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= MaxDetailChars ? one : one[..MaxDetailChars] + "…";
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
    /// <see cref="AiOutcomeTally.Summary"/>. Sinds #293 hangt de ruwe foutbody er per
    /// oorzaak achter, zodat de beheerder Ollama's eigen woorden ziet en niet alleen
    /// onze duiding ervan.</summary>
    public string Summary => string.Join(", ", _calls
        .Where(kv => kv.Key != EmbedCallOutcome.Ok && kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => Label(kv.Key), StringComparer.Ordinal)
        .Select(kv => $"{Label(kv.Key)}×{kv.Value}"
            + (_details.TryGetValue(kv.Key, out var d) ? $" [{d}]" : "")));

    /// <summary>Korte duiding per oorzaak. Bewust ZONDER stellige diagnose waar we er
    /// geen hebben: de 4xx-hint luidde tot #293 "model niet gepulld?", terwijl het
    /// model er gewoon stond en de echte oorzaak een OOM-kill van llama-server was —
    /// een verkeerde gok is duurder dan een open vraag, want de beheerder gaat er
    /// achteraan. Noemt daarom de meest waarschijnlijke oorzaak (backend overleden)
    /// eerst en laat de tweede open; de ruwe foutbody in <see cref="Summary"/> beslist.</summary>
    private static string Label(EmbedCallOutcome outcome) => outcome switch
    {
        EmbedCallOutcome.ServerError => "5xx (model-runner omgevallen?)",
        EmbedCallOutcome.Timeout => "timeout",
        EmbedCallOutcome.Transport => "onbereikbaar",
        EmbedCallOutcome.ClientError => "4xx (backend overleden? te grote invoer?)",
        EmbedCallOutcome.Incomplete => "onvolledig antwoord",
        EmbedCallOutcome.DimensionMismatch => "dimensie-mismatch",
        _ => outcome.ToString(),
    };
}
