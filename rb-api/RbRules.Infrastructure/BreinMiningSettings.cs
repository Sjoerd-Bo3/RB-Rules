namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de brein-mining (#279). Singleton in DI, uit de omgeving
/// gelezen (<see cref="FromEnvironment"/>) — zelfde patroon als
/// <see cref="NightlyRunSettings"/>.
///
/// De mining was sequentieel: één kaart tegelijk, ~40s per kaart aan rb-ai-tijd. Dat
/// maakt van 40 kaarten een half uur en van een ongecapte nachtrun (~900 kaarten) tien
/// uur, terwijl de sidecar meerdere sessies tegelijk aankan. <see cref="Concurrency"/>
/// zet hoeveel focus-kaarten/subjecten er gelijktijdig door de extractie gaan.
///
/// De default (3) is GEEN vrije keuze maar het spiegelbeeld van rb-ai's
/// achtergrond-deelcap (<c>AI_MAX_CONCURRENCY</c> 5 − <c>AI_INTERACTIVE_RESERVE</c> 2
/// = 3, zie rb-ai/src/concurrency.ts). Precies zoveel workers als er
/// achtergrond-permits zijn betekent: elke worker krijgt meteen een slot, er wordt
/// nooit in de rij gewacht, en er blijven per constructie 2 permits vrij voor /ask.
/// Hoger zetten levert daarom geen doorvoer op — het levert wachtrij op, en boven de
/// wachttijd-cap 429's (die sinds #279 als <c>AiCallOutcome.ConcurrencyLimited</c>
/// zichtbaar worden in het run-detail). Bijstellen doe je aan BEIDE kanten, samen met
/// het geheugenplafond van de rb-ai-container.</summary>
/// <param name="Concurrency">Aantal kaarten/subjecten dat gelijktijdig door de
/// extractie gaat. 1 = het oude sequentiële gedrag.</param>
public sealed record BreinMiningSettings(int Concurrency)
{
    /// <summary>Bovengrens op de instelbare waarde: een typfout in de <c>.env</c>
    /// (<c>BREIN_MINING_CONCURRENCY=300</c>) mag geen 300 gelijktijdige aanvragen op
    /// de sidecar loslaten. rb-ai's semaphore zou ze netjes in de rij zetten en na de
    /// wachttijd-cap afwijzen, maar dan betaalt de run wél de uitval.</summary>
    public const int MaxConcurrency = 16;

    public static readonly BreinMiningSettings Default = new(3);

    public static BreinMiningSettings FromEnvironment() =>
        new(ParseConcurrency("BREIN_MINING_CONCURRENCY", Default.Concurrency));

    /// <summary>Onzin of een waarde buiten 1..<see cref="MaxConcurrency"/> valt terug
    /// op de default: een kapotte instelling mag de mining niet stilleggen (0) en niet
    /// laten ontsporen — hetzelfde principe als de venster-parse in
    /// <see cref="NightlyRunSettings"/>.</summary>
    private static int ParseConcurrency(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v)
            && v >= 1 && v <= MaxConcurrency
            ? v
            : fallback;
}
