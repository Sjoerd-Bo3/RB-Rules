using Microsoft.Extensions.DependencyInjection;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Draait een <see cref="PathDefinition"/> (#190) als één
/// JobRunner-run onder de padnaam — dezelfde éénjob-gate, dezelfde live-
/// Progress en dezelfde run_log-afronding (Kind="job", Ref=padnaam) als een
/// losse job, want <c>JobRunner.TryStart</c> ziet geen verschil tussen een
/// gewone job en een pad (allebei zijn gewoon een
/// <c>Func&lt;IServiceProvider, Action&lt;string&gt;, CancellationToken,
/// Task&lt;JobOutcome&gt;&gt;</c>).
///
/// Per stap (en per herhaling bij Drain) een eigen run_log-regel
/// (Kind=padnaam, Ref=stapnaam) zodat de historie precies laat zien welke
/// stap wanneer draaide, hoe vaak gedraineerd is en waar het (eventueel)
/// strandde. Faalt een stap (exception) → het pad stopt daar: de regel wordt
/// als "error" gelogd en de exception gaat verder omhoog, zodat JobRunner de
/// hele padrun als "error" afsluit — de rest van de stappen draait niet
/// (geen half werk om terug te draaien; de al-gedraaide stappen blijven
/// gewoon staan, want de onderliggende jobs zijn zelf idempotent).</summary>
public static class PathRunner
{
    /// <summary><paramref name="findJob"/> is de test-seam (patroon
    /// ClaimMiningService.CheckOfficialAsync): productiecode laat hem weg en
    /// krijgt <see cref="JobCatalog.Find"/>, tests kunnen een kleine gestubde
    /// job-set injecteren zonder de hele DI-graaf van échte jobs (rb-ai,
    /// Ollama, Neo4j) op te hoeven tuigen.</summary>
    public static async Task<JobOutcome> RunAsync(
        PathDefinition path, IServiceProvider sp, Action<string> report, CancellationToken ct,
        Func<string, JobDefinition?>? findJob = null)
    {
        findJob ??= JobCatalog.Find;
        var db = sp.GetRequiredService<RbRulesDbContext>();
        var stepSummaries = new List<string>();

        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            var job = findJob(step.JobName)
                ?? throw new InvalidOperationException(
                    $"pad '{path.Name}': stap '{step.JobName}' bestaat niet in de JobCatalog");

            var attempt = 0;
            var drainedNote = "";
            JobOutcome outcome;
            while (true)
            {
                attempt++;
                var stepLabel = $"stap {i + 1}/{path.Steps.Count}: {step.JobName}"
                    + (step.Drain ? $" (run {attempt})" : "");
                report(stepLabel);

                try
                {
                    outcome = await job.Run(sp, p => report($"{stepLabel} — {p}"), ct);
                }
                catch (Exception ex)
                {
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = path.Name, Ref = step.JobName, Status = "error",
                        Detail = step.Drain ? $"run {attempt}: {ex.Message}" : ex.Message,
                    });
                    await db.SaveChangesAsync(ct);
                    throw;
                }

                db.RunLogs.Add(new RunLog
                {
                    Kind = path.Name, Ref = step.JobName, Status = "ok",
                    Detail = step.Drain ? $"run {attempt}: {outcome.Detail}" : outcome.Detail,
                });
                await db.SaveChangesAsync(ct);

                if (!step.Drain || outcome.Drained) break;
                if (attempt >= step.MaxRepeats)
                {
                    // Vangrail geraakt (#190): de stap zelf faalde niet, maar
                    // bleef na MaxRepeats runs op zijn cap stuiten — zichtbaar
                    // maken zonder het hele pad te laten falen (de volgende
                    // pad-run pakt de rest vanzelf op, de jobs zijn idempotent).
                    drainedNote = $" (max {step.MaxRepeats} herhalingen bereikt, mogelijk nog werk over)";
                    break;
                }
            }

            stepSummaries.Add($"{step.JobName}: {outcome.Detail}{drainedNote}");
        }

        return new(string.Join(" · ", stepSummaries));
    }
}
