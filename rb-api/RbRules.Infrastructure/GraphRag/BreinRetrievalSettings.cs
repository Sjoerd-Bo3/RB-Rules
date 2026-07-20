using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Infrastructure.GraphRag;

/// <summary>Instellingen voor de brein-GraphRAG-retrieval in /ask (fase
/// ask-retrieval, #228). De omgeving is sinds #254 de BOOTSTRAP-DEFAULT
/// (<see cref="FromEnvironment"/>); beheer schrijft er via de <c>setting</c>-tabel
/// overheen (<see cref="WithOverrides"/>). Niet als singleton injecteren: vraag hem
/// op het gebruiksmoment op bij <see cref="ManagedSettingsService"/>.
/// <see cref="Enabled"/> is de DEFAULT-UIT feature-flag (<see cref="BreinRetrievalGate"/>):
/// staat hij uit, dan raakt /ask deze laag nooit aan.
///
/// De budgetten (token, latency) en de GDS-warmte-vlag reizen als
/// <see cref="GraphRagContext"/> de orchestrator in; de epoch-stempels
/// (<see cref="EmbeddingRev"/>/<see cref="PromptVersion"/>) belanden op de
/// <see cref="AnswerTrace"/> zodat "zouden we dit vandaag nog zo zeggen?" (§6) later
/// beantwoordbaar is.</summary>
public sealed record BreinRetrievalSettings(
    bool Enabled,
    int TokenBudget = 4000,
    double LatencyBudgetMs = 4000,
    bool GdsWarm = false,
    string? EmbeddingRev = "bge-m3",
    string? PromptVersion = "ask-graphrag-v1")
{
    /// <summary>Alles uit — de veilige default (geen flag gezet). Ook wat de meeste
    /// tests en de niet-brein-constructors impliciet krijgen.</summary>
    public static readonly BreinRetrievalSettings Disabled = new(Enabled: false);

    /// <summary>Lees de flag + budgetten uit de omgeving. Onbekende/afwezige waarden
    /// vallen terug op de veilige defaults; de flag is default UIT.</summary>
    public static BreinRetrievalSettings FromEnvironment()
    {
        var enabled = BreinRetrievalGate.Parse(
            Environment.GetEnvironmentVariable(BreinRetrievalGate.EnvVar));
        return new BreinRetrievalSettings(
            Enabled: enabled,
            TokenBudget: ParseInt("BREIN_RETRIEVAL_TOKEN_BUDGET", 4000),
            LatencyBudgetMs: ParseInt("BREIN_RETRIEVAL_LATENCY_MS", 4000),
            // GDS-named-graph is default koud: k-shortest (Path) draait pas na een
            // expliciete warm-up (beslissing #232). Zet de vlag pas aan als de
            // named-graph bij startup geprojecteerd wordt.
            GdsWarm: BreinRetrievalGate.Parse(
                Environment.GetEnvironmentVariable("BREIN_RETRIEVAL_GDS_WARM")));
    }

    /// <summary>Leg de beheerde override (#254) over de env-basiswaarde heen. Alleen
    /// de FLAG is beheerbaar — de budgetten blijven env/code, die horen niet in een
    /// knop. Ontbrekende sleutel ⇒ ongewijzigd (lege <c>setting</c>-tabel = env-gedrag).</summary>
    public BreinRetrievalSettings WithOverrides(IReadOnlyDictionary<string, string> overrides) =>
        overrides.TryGetValue(SettingKeys.BreinRetrievalEnabled, out var raw)
            ? this with { Enabled = BreinRetrievalGate.Parse(raw) }
            : this;

    private static int ParseInt(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v > 0 ? v : fallback;
}
