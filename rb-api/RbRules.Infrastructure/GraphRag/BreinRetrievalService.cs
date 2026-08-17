using Microsoft.Extensions.Logging;
using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Infrastructure.GraphRag;

/// <summary>Het resultaat van één brein-verrijking (fase ask-retrieval, #228): het
/// prompt-BLOK dat de bestaande /ask-context aanvult, plus de volledige
/// <see cref="GraphRagOutcome"/> (met de <see cref="AnswerTrace"/> die AskService in
/// het slot best-effort persisteert). Een leeg blok is legitiem — dan verrijkt de
/// retrieval niets en draait /ask ongewijzigd door.</summary>
public sealed record BreinEnrichment(string PromptBlock, GraphRagOutcome Outcome);

/// <summary>De dunne naad tussen AskService (live gebruikerspad) en de fase-4
/// <see cref="RetrievalOrchestrator"/> (fase ask-retrieval, #228, §4). Verantwoordelijk
/// voor precies één ding: de retrieval draaien ACHTER de feature-flag en ELKE fout
/// netjes wegslikken zodat /ask nooit een 500 krijgt van de brein-laag.
///
/// KRITIEK (rode draad #236 / werkafspraak): flag UIT ⇒ <see cref="EnrichAsync"/>
/// doet niets en raakt de orchestrator (en dus Neo4j/pgvector) nóóit aan — geen
/// extra latency, geen gedragswijziging. Sinds #254 wordt de flag op het
/// GEBRUIKSMOMENT gelezen (beheerde instelling, env als bootstrap-default), zodat de
/// knop in beheer direct werkt; de lezing zelf is een cache-hit. Flag AAN ⇒ verrijk, maar elke
/// retrieval-fout (Neo4j weg, pgvector weg, timeout) → null terug (AskService valt
/// terug op de bestaande flow). Alleen een échte client-annulering bubbelt door.</summary>
public sealed class BreinRetrievalService(
    RetrievalOrchestrator orchestrator,
    ManagedSettingsService managed,
    ILogger<BreinRetrievalService> logger)
{
    /// <summary>Verrijk de context met de brein-subgraaf/paden/trust-gelabelde
    /// citaties. Returnt null als (a) de flag uit staat of (b) de retrieval faalt —
    /// in beide gevallen draait /ask ongewijzigd verder. Nooit een exception naar
    /// AskService (behalve client-abort).</summary>
    public async Task<BreinEnrichment?> EnrichAsync(
        string question, QuestionType type, CancellationToken ct = default)
    {
        // De flag-poort staat vóór ELKE andere aanroep: uit ⇒ de orchestrator (en dus
        // Neo4j/pgvector) wordt niet aangeraakt.
        var settings = await managed.BreinRetrievalAsync(ct).ConfigureAwait(false);
        if (!settings.Enabled) return null;

        try
        {
            var context = new GraphRagContext(
                TokenBudget: settings.TokenBudget,
                GdsWarm: settings.GdsWarm,
                LatencyBudget: new LatencyBudget(settings.LatencyBudgetMs),
                Epoch: new TraceEpoch(
                    EmbeddingRev: settings.EmbeddingRev,
                    PromptVersion: settings.PromptVersion));

            var outcome = await orchestrator
                .RetrieveAsync(question, type, context, ct: ct)
                .ConfigureAwait(false);

            var block = BreinContextFormatter.Format(outcome);
            return new BreinEnrichment(block, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // de vrager zelf haakte af — niet maskeren
        }
        catch (Exception ex)
        {
            // AI/graaf-uitval is een verwacht pad (§7): log en degradeer naar de
            // bestaande /ask-flow. NOOIT een 500 voor de gebruiker.
            logger.LogWarning(ex,
                "brein-GraphRAG-retrieval mislukt — /ask valt terug op de bestaande retrieval");
            return null;
        }
    }
}
