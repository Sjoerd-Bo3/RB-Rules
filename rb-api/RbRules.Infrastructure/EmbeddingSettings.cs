namespace RbRules.Infrastructure;

/// <summary>Hoeveel werk er per embed-verzoek naar Ollama gaat (#282).
///
/// AANLEIDING: de cgroup-OOM-killer schoot <c>llama-server</c> af op ~2,5 GB — precies
/// het plafond van <c>rb-v2-ollama</c>. Het plafond verhogen kon niet: Postgres, Neo4j,
/// Ollama (2,5 GiB) en rb-ai (2,44 GiB) delen één 8 GB-VM, en rb-ai claimde in #279 al
/// de laatste vrije ruimte. Dus begrenzen we het GEBRUIK. Idle houdt Ollama ~69 MiB
/// vast; de piek zit volledig in het verzoek, waar llama.cpp de activaties van alle
/// sequenties tegelijk vasthoudt.
///
/// TWEE GRENZEN, want een telling alleen zegt niets over de kosten (zie
/// <see cref="Domain.EmbedBatching"/>): 16 kaartteksten (~300 tekens) is een fractie
/// van 16 regel-secties (tot 2400 tekens). Het tekenbudget is wat de wórst case
/// afknijpt; de telling houdt ook korte teksten in toom.
///
/// ENV, NIET BEHEER (afwijking van de #254-voorkeur, bewust): dit is geen
/// gedragsknop maar een geheugen-knop die vastzit aan de <c>memory:</c>-cap van
/// <c>rb-v2-ollama</c> in de compose-file — net als <c>AI_MAX_CONCURRENCY</c> bij de
/// cap van <c>rb-v2-ai</c>. De een verzetten zonder de ander is per definitie fout,
/// dus horen ze naast elkaar te staan in dezelfde file, mét de meting erbij.
/// En zoals de <c>NIGHTLY_ENABLED</c>-les (#268) voorschrijft: beide vlaggen staan
/// óók echt onder <c>environment:</c> van de rb-api-service, niet alleen in de
/// VM-<c>.env</c> — anders bereiken ze de container nooit.</summary>
/// <param name="BatchSize">Maximaal aantal teksten per <c>/api/embed</c>-verzoek.</param>
/// <param name="BatchChars">Ongeveer het maximale aantal tekens per verzoek.</param>
/// <param name="MaxConsecutiveFailures">Na hoeveel opeenvolgende gefaalde batches een
/// pijplijn stopt in plaats van de rest ook nog te proberen.</param>
public sealed record EmbeddingSettings(
    int BatchSize, int BatchChars, int MaxConsecutiveFailures = 3)
{
    /// <summary>Was 16 tot #282 — gehalveerd omdat de kill in de embed-run zelf zat.
    /// Doorvoer kost dit nauwelijks: bge-m3-verzoeken zijn kort en de pijplijnen
    /// draaien in de achtergrond, terwijl een OOM-kill de hele stap wegvaagt.</summary>
    public const int DefaultBatchSize = 8;

    /// <summary>Het hoogste aantal tekens dat op productie GEMETEN nog een HTTP 200
    /// gaf (#293). Meetreeks tegen <c>rb-v2-ollama</c> (<c>POST /api/embed</c>, bge-m3):
    /// 500 / 2400 / 3908 / 4500 / 5000 / 6000 / 7000 → 200; 8000 → 400 in 3 van de 3
    /// pogingen; 20000 → 400. De klip ligt dus deterministisch tussen 7000 en 8000.
    /// Dit is een MEETWAARDE, geen ontwerpkeuze: hij hoort alleen te verschuiven als
    /// er opnieuw gemeten is (en dan samen met de <c>memory:</c>-cap van de
    /// ollama-service in de compose-file, die de klip veroorzaakt).</summary>
    public const int MeasuredSafeMaxBatchChars = 7000;

    /// <summary>De laagst gemeten waarde die STUKGING (#293) — 8000 tekens gaf 3 van de
    /// 3 keer een 400, met foutbody <c>do embedding request: … EOF</c>: het
    /// <c>llama-server</c>-kindproces sterft. Bevestigd met de OOM-teller
    /// (<c>dmesg | grep -c llama-server</c> ging tijdens de meetreeks van 10 naar 30 —
    /// elke mislukte aanroep is één OOM-kill). Staat hier zodat de regressietest kan
    /// aantonen dat de default er niet alleen ónder ligt, maar er met marge onder.</summary>
    public const int MeasuredFailingBatchChars = 8000;

    /// <summary>6000 tekens. Was 8000 tot #293 — en dat bleek exact de waarde waarop
    /// llama-server omvalt (zie <see cref="MeasuredFailingBatchChars"/>): de begrenzing
    /// die #282 introduceerde stond precies op de klip in plaats van eronder, dus de
    /// card-errata-bron viel elke run om.
    ///
    /// WAAROM 6000 EN NIET 7000: 7000 is de hoogste waarde die het gehaald heeft, dus
    /// dat is de klifrand zelf en geen veilige plek — de exacte grens ligt ergens in
    /// (7000, 8000] en verschuift met wat Postgres/Neo4j/rb-ai op dat moment van de
    /// 8 GB-VM claimen. 6000 houdt ~15% marge onder de laatste geslaagde meting en
    /// blijft ruim boven de zwaarste chunk die we in de praktijk zien (Card Errata,
    /// 3908 tekens), dus het kost geen extra verzoeken waar het pijn doet.
    ///
    /// Doorvoer is hier het goedkope goed: bge-m3-verzoeken zijn kort, de pijplijnen
    /// draaien in de achtergrond, en een OOM-kill wist een hele bron.</summary>
    public const int DefaultBatchChars = 6000;

    /// <summary>Het hoogste dat <c>EMBED_BATCH_CHARS</c> via de env mag worden: 6300,
    /// oftewel 10% onder <see cref="MeasuredSafeMaxBatchChars"/> (#303).
    ///
    /// WAAROM NIET 7000. #293 verlaagde het plafond van 100000 naar de meetwaarde 7000
    /// — een grote verbetering, maar het zette de knop precies op de waarde die diezelfde
    /// PR "de klifrand zelf, en geen veilige plek" noemt: 7000 is de laatste waarde die
    /// het HAALDE, dus de echte grens ligt daar ergens boven en schuift mee met wat
    /// Postgres/Neo4j/rb-ai op dat moment van de 8 GB-VM claimen. De default kreeg
    /// daarom marge (6000) en de handmatige knop niet — inconsistent, en juist de knop
    /// wordt gebruikt op een moment dat het al misgaat.
    ///
    /// WAAROM NIET GELIJK AAN DE DEFAULT. Dan is de knop alleen nog een noodrem omlaag
    /// en kan een beheerder niet meer bijstellen zonder een deploy. 6300 laat een
    /// beperkte marge omhoog en houdt tegelijk ~10% onder de laatste geslaagde meting.
    /// Écht hoger willen betekent de <c>memory:</c>-cap van <c>rb-v2-ollama</c>
    /// verzetten — een compose-wijziging, en dan verhuizen deze constanten mee.</summary>
    public const int MaxConfigurableBatchChars = MeasuredSafeMaxBatchChars * 9 / 10;

    /// <summary>Na 3 opeenvolgende gefaalde batches ligt Ollama eruit, niet één batch.
    /// Doorgaan kost dan alleen tijd: bij een timeout is dat 5 minuten per batch, en
    /// de pijplijn draait synchroon in de scheduler-lus én achter de één-job-gate van
    /// <c>JobRunner</c>. 3 (niet 1) zodat één hik een lange run niet afkapt — een
    /// geslaagde batch zet de teller terug.</summary>
    public const int DefaultMaxConsecutiveFailures = 3;

    public static readonly EmbeddingSettings Default = new(
        DefaultBatchSize, DefaultBatchChars, DefaultMaxConsecutiveFailures);

    /// <summary><c>EMBED_BATCH_SIZE</c> / <c>EMBED_BATCH_CHARS</c>. Onzin of een
    /// ontbrekende waarde valt terug op de default: een typfout in de <c>.env</c> mag
    /// de embed-pijplijn niet stilletjes op 1 tekst per verzoek zetten (traag) of
    /// ontgrendelen (OOM).
    ///
    /// Het plafond van <c>EMBED_BATCH_CHARS</c> is sinds #293 geen ruime 100000 meer:
    /// boven de klip is de knop geen experimenteerruimte maar een garantie op een
    /// OOM-kill. Sinds #303 is dat plafond <see cref="MaxConfigurableBatchChars"/> en
    /// niet meer de meetwaarde zelf — die is de klifrand, geen veilige plek. Hoger
    /// willen heeft alleen zin ná het verhogen van de <c>memory:</c>-cap van
    /// <c>rb-v2-ollama</c> — wat sowieso een compose-wijziging is, dus dan mogen deze
    /// constanten meeverhuizen. Omláág bijstellen (de noodrem) kan gewoon via de env.</summary>
    /// <param name="warn">Krijgt een regel per genegeerde waarde. In een PR over
    /// stille degradatie mag een `EMBED_BATCH_SIZE=100` niet zonder één woord op 8
    /// terugvallen — dan denk je dat je iets hebt bijgesteld terwijl er niets
    /// veranderde (dezelfde klasse fout als de NIGHTLY_ENABLED-noodrem, #268).</param>
    public static EmbeddingSettings FromEnvironment(Action<string>? warn = null) => new(
        Parse("EMBED_BATCH_SIZE", DefaultBatchSize, 1, 64, warn),
        Parse("EMBED_BATCH_CHARS", DefaultBatchChars, 500, MaxConfigurableBatchChars, warn),
        DefaultMaxConsecutiveFailures);

    private static int Parse(
        string envVar, int fallback, int min, int max, Action<string>? warn)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw, out var v) && v >= min && v <= max) return v;
        warn?.Invoke(
            $"{envVar}='{raw}' is genegeerd (geen geheel getal tussen {min} en {max}) "
            + $"— de embed-pijplijn draait op de standaardwaarde {fallback}.");
        return fallback;
    }
}
