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
/// gewoon staan, want de onderliggende jobs zijn zelf idempotent).
///
/// Twee vangrails op de drain-lus (review-fix #190): de harde
/// <see cref="PathStep.MaxRepeats"/>-grens, én een no-progress-guard die de
/// lus vroegtijdig stopt zodra twee opeenvolgende runs een identiek
/// resultaat geven (zelfde Detail én nog steeds niet Drained) — dan eet iets
/// het per-run-budget op zonder dat er iets landt (bv. een document met méér
/// al-bekende items dan de cap) en zijn verdere herhalingen pure verspilling.
/// Beide vangrails laten het pad gewoon doorlopen naar de volgende stap: de
/// stap faalde niet, en de volgende (nachtelijke of handmatige) run pakt de
/// rest op.</summary>
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

        // Review-fix #190: elke run_log-schrijfactie krijgt een EIGEN, verse
        // scope/DbContext — nooit de scoped context waarin een stap zojuist
        // draaide of crashte. Een vervuilde change-tracker zou anders de
        // error-regel kunnen verliezen óf half werk van de gefaalde stap
        // alsnog meecommitten. Best-effort bovendien (JobRunner-afspraak:
        // "logging mag een job-afronding nooit blokkeren") — een log-hik mag
        // vooral nooit de oorspronkelijke stap-exceptie maskeren.
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        async Task LogAsync(string stepRef, string status, string detail)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RbRulesDbContext>();
                db.RunLogs.Add(new RunLog
                {
                    Kind = path.Name, Ref = stepRef, Status = status, Detail = detail,
                });
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // Zie hierboven: best-effort.
            }
        }

        var stepSummaries = new List<string>();

        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            var job = findJob(step.JobName)
                ?? throw new InvalidOperationException(
                    $"pad '{path.Name}': stap '{step.JobName}' bestaat niet in de JobCatalog");

            var attempt = 0;
            var drainedNote = "";
            string? previousDetail = null;
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
                    await LogAsync(step.JobName, "error",
                        step.Drain ? $"run {attempt}: {ex.Message}" : ex.Message);
                    throw;
                }

                await LogAsync(step.JobName, "ok",
                    step.Drain ? $"run {attempt}: {outcome.Detail}" : outcome.Detail);

                if (!step.Drain || outcome.Drained) break;

                // No-progress-guard (review-fix #190): identiek resultaat als
                // de vorige run terwijl de stap nog steeds niet gedraineerd is
                // ⇒ het budget wordt opgegeten zonder dat er iets verandert
                // (bv. alleen al-bekende items binnen de cap). Verdere
                // herhalingen zijn dan verspilling — stoppen en doorgaan met
                // de volgende stap; de volgende run pakt het vanzelf op.
                if (outcome.Detail == previousDetail)
                {
                    await LogAsync(step.JobName, "info",
                        $"drain maakt geen voortgang na run {attempt} — gestopt "
                        + "(zelfde resultaat als de vorige run, mogelijk nog werk over)");
                    drainedNote = $" (drain gestopt na run {attempt}: geen voortgang)";
                    break;
                }
                previousDetail = outcome.Detail;

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
