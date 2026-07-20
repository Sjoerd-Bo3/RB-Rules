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
/// Tenzij het pad <see cref="PathDefinition.ContinueOnError"/> zet (#258): dan
/// wordt de fout wél als "error" gelogd, maar loopt de keten door met de
/// volgende stap en draagt de samenvatting "FOUT — …" voor die stap. Dat is de
/// best-effort-semantiek die de "alles"-keten en de nachtrun altijd al hadden
/// (CONVENTIONS: "een haperende externe dienst stopt nooit de hele run") en
/// die ze bij het opgaan in dit mechanisme moesten houden. Een AFGEBROKEN run
/// (#253) stopt ook een best-effort pad: cancellation is een beslissing, geen
/// storing.
///
/// Een stap met <see cref="PathStep.Uncapped"/> draait via
/// <see cref="JobDefinition.RunUncapped"/> en krijgt de pad-deadline mee (het
/// nachtrun-venster); heeft de job geen ongecapte variant, dan valt de stap
/// terug op de gewone, gecapte Run — JobPathsTests maakt zo'n combinatie
/// zichtbaar in plaats van hem stil te laten degraderen.
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
    /// <summary><paramref name="deadline"/> (#258) begrenst de ongecapte
    /// stappen (<see cref="PathStep.Uncapped"/>) in de tijd — het einde van het
    /// nachtvenster. Null = geen deadline: een handmatige volledige drain, of
    /// een pad zonder ongecapte stappen.</summary>
    public static async Task<JobOutcome> RunAsync(
        PathDefinition path, IServiceProvider sp, Action<string> report, CancellationToken ct,
        Func<string, JobDefinition?>? findJob = null, DateTimeOffset? deadline = null)
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
                // Bewust zonder token (#253): bij een afbreking is `ct` al
                // gecanceld, en juist dán moet de stap-regel nog landen —
                // anders verdwijnt het spoor van hoe ver het pad kwam.
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // Zie hierboven: best-effort.
            }
        }

        var stepSummaries = new List<string>();

        for (var i = 0; i < path.Steps.Count; i++)
        {
            // Afbreekpunt (#253): een stap die de token zelf niet fijnmazig
            // doorgeeft, laat het pad in elk geval tussen stappen stoppen.
            ct.ThrowIfCancellationRequested();
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
                    // Ongecapte stap (#258): de per-run cap gaat eraf en de
                    // pad-deadline erin. Heeft de job geen ongecapte variant,
                    // dan draait gewoon de gecapte Run (JobPathsTests bewaakt
                    // dat die combinatie niet per ongeluk ontstaat).
                    outcome = step.Uncapped && job.RunUncapped is { } runUncapped
                        ? await runUncapped(sp, p => report($"{stepLabel} — {p}"), deadline, ct)
                        : await job.Run(sp, p => report($"{stepLabel} — {p}"), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Afgebroken door beheer (#253): geen fout van de stap —
                    // apart gelogd zodat de historie het onderscheid toont.
                    // JobRunner sluit de hele padrun af als "cancelled". Ook een
                    // best-effort pad (#258) stopt hier: afbreken is een
                    // beslissing, geen storing die je kunt overslaan.
                    await LogAsync(step.JobName, "cancelled",
                        step.Drain ? $"run {attempt}: afgebroken via beheer" : "afgebroken via beheer");
                    throw;
                }
                catch (Exception ex)
                {
                    await LogAsync(step.JobName, "error",
                        step.Drain ? $"run {attempt}: {ex.Message}" : ex.Message);
                    if (!path.ContinueOnError) throw;
                    // Best-effort pad (#258): de fout is gelogd en gaat als
                    // "FOUT — …" de samenvatting in (zelfde vorm als de oude
                    // RunAllAsync), maar de keten loopt door. Ook de drain-lus
                    // stopt hier: een stap die net kapot ging opnieuw draaien
                    // is precies de verspilling die de vers-werk-semantiek
                    // van #190 wil vermijden.
                    outcome = new JobOutcome($"FOUT — {ex.Message}");
                    break;
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
