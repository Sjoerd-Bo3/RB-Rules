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
public sealed record EmbeddingSettings(int BatchSize, int BatchChars)
{
    /// <summary>Was 16 tot #282 — gehalveerd omdat de kill in de embed-run zelf zat.
    /// Doorvoer kost dit nauwelijks: bge-m3-verzoeken zijn kort en de pijplijnen
    /// draaien in de achtergrond, terwijl een OOM-kill de hele stap wegvaagt.</summary>
    public const int DefaultBatchSize = 8;

    /// <summary>~8000 tekens: ruim drie maximale regel-secties (2400) of ruwweg
    /// twintig kaartteksten. Zo raakt de kaart-pijplijn in de praktijk alleen
    /// <see cref="BatchSize"/> en knijpt het budget precies de zware regel-batches af,
    /// die vóór #282 tot 16×2400 ≈ 38k tekens in één verzoek konden proppen.</summary>
    public const int DefaultBatchChars = 8000;

    public static readonly EmbeddingSettings Default = new(DefaultBatchSize, DefaultBatchChars);

    /// <summary><c>EMBED_BATCH_SIZE</c> / <c>EMBED_BATCH_CHARS</c>. Onzin of een
    /// ontbrekende waarde valt terug op de default: een typfout in de <c>.env</c> mag
    /// de embed-pijplijn niet stilletjes op 1 tekst per verzoek zetten (traag) of
    /// ontgrendelen (OOM). De clamps zijn ruim genoeg om te kunnen experimenteren en
    /// strak genoeg om een 0 of een 100000 te weren.</summary>
    public static EmbeddingSettings FromEnvironment() => new(
        Parse("EMBED_BATCH_SIZE", DefaultBatchSize, 1, 64),
        Parse("EMBED_BATCH_CHARS", DefaultBatchChars, 500, 100_000));

    private static int Parse(string envVar, int fallback, int min, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v >= min && v <= max
            ? v
            : fallback;
}
