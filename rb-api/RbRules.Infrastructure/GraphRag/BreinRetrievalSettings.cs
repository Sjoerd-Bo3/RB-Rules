using RbRules.Domain.GraphRag;

namespace RbRules.Infrastructure.GraphRag;

/// <summary>Omgevings-instellingen voor de brein-GraphRAG-retrieval in /ask (fase
/// ask-retrieval, #228). Singleton in DI, uit env gelezen (<see cref="FromEnvironment"/>).
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

    private static int ParseInt(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v > 0 ? v : fallback;
}
