using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>In-app scheduler (audit-fix: geen handmatige crontab meer).
/// Elk uur: scan bronnen die volgens hun cadence aan de beurt zijn.
/// Elke week: kaart-sync (nieuwe sets/errata). Periodieke zelfverrijking
/// (#122): relatie-mining nachtelijk en de bronnen-scout wekelijks, als
/// gewone JobRunner-jobs op het run_log-grootboek.</summary>
public class ScanScheduler(
    IServiceScopeFactory scopeFactory, JobRunner jobs, ILogger<ScanScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    // Dagelijks: nieuwe sets/reveals verschijnen in Riot's gallery en komen zo
    // binnen een dag automatisch binnen (incl. embeddings + mining via de tick).
    private static readonly TimeSpan CardSyncInterval = TimeSpan.FromDays(1);
    // Nachtelijke claims-harvest (#50): community-documenten worden hooguit
    // wekelijks gescand, dus één keer per dag minen is ruim voldoende — en
    // idempotent (al geminede documenten worden overgeslagen).
    private static readonly TimeSpan ClaimsMineInterval = TimeSpan.FromDays(1);
    // Relatie-mining (#116/#122): zelfde nachtritme als de claims-harvest —
    // eens per etmaal met de standaard-cap volstaat; de run is idempotent
    // (markers + dedupe) en elke eenheid logt zelf naar run_log.
    private static readonly TimeSpan RelationsMineInterval = TimeSpan.FromDays(1);
    // Bronnenjacht (#63/#122): wekelijks — elke run kost een research-call
    // van minuten, en het bronnenlandschap verandert niet sneller dan dat.
    private static readonly TimeSpan ScoutInterval = TimeSpan.FromDays(7);
    private DateTimeOffset _lastCardSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastClaimsMine = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Even wachten tot migraties/seed klaar zijn en de stack op stoom is.
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tickStart = DateTimeOffset.UtcNow;
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingest = scope.ServiceProvider.GetRequiredService<IngestService>();
                var results = await ingest.ScanAsync(onlyDue: true, ct: ct);
                if (results.Count > 0)
                    logger.LogInformation("Scan: {Results}",
                        string.Join(", ", results.Select(r => $"{r.SourceId}={r.Status}")));

                // Web-push (#28): meld high-severity wijzigingen (bans/errata/
                // regelwijzigingen) aan abonnees. Best-effort.
                if (results.Any(r => r.Status is "changed"))
                {
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<PushService>()
                            .NotifyHighSeverityAsync(
                                scope.ServiceProvider.GetRequiredService<RbRulesDbContext>(),
                                tickStart, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Push-verzending overgeslagen");
                    }
                }

                // Bij nieuwe/gewijzigde documenten: her-indexeren (sectie-chunks +
                // embeddings) en de banlijst/errata opnieuw structureren.
                // Audit-fix: de index loopt nooit meer stilzwijgend achter op een scan.
                if (results.Any(r => r.Status is "changed" or "new"))
                {
                    try
                    {
                        var rules = scope.ServiceProvider.GetRequiredService<RuleChunkPipeline>();
                        var indexed = await rules.RunAsync(ct: ct);
                        if (indexed.Count > 0)
                            logger.LogInformation("Regel-index: {Detail}",
                                string.Join(", ", indexed.Select(r => $"{r.SourceId}={r.Chunks}")));

                        var bans = scope.ServiceProvider.GetRequiredService<BanErrataSyncService>();
                        var b = await bans.SyncAsync(ct);
                        logger.LogInformation("Bans/errata: {Bans}/{Errata}", b.Bans, b.Errata);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Her-index/bans overgeslagen (Ollama/rb-ai onbereikbaar?)");
                    }
                }

                // Set-release als event (#52): heeft de classifier een
                // set-release herkend die nog niet is afgehandeld — via de
                // scan van zonet óf via de classify-backfill (#58) — dan
                // draait de volledige keten: card-sync, mechanieken
                // (+keyword-kandidaten), embeddings, graph, primer-herziening.
                // Elke tick gecheckt (goedkoop als er niets openstaat); het
                // run_log-grootboek voorkomt dubbele triggers.
                try
                {
                    var setRelease = scope.ServiceProvider.GetRequiredService<SetReleaseService>();
                    var sr = await setRelease.RunForPendingAsync(ct: ct);
                    if (sr.Triggers > 0)
                    {
                        _lastCardSync = DateTimeOffset.UtcNow; // keten deed al een card-sync
                        logger.LogInformation("Set-release-keten ({Triggers} release(s)): {Detail}",
                            sr.Triggers, sr.Detail);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Set-release-keten overgeslagen");
                }

                if (DateTimeOffset.UtcNow - _lastCardSync >= CardSyncInterval)
                {
                    var cards = scope.ServiceProvider.GetRequiredService<CardSyncService>();
                    var r = await cards.SyncAsync(ct: ct);
                    _lastCardSync = DateTimeOffset.UtcNow;
                    logger.LogInformation("Kaart-sync: {Sets} sets, {Summary}",
                        r.Sets, r.CardsSummary);
                }

                // Embed kaarten die het nodig hebben (nieuw/tekst gewijzigd).
                // Best-effort: Ollama-uitval mag de scheduler niet stoppen.
                try
                {
                    var pipeline = scope.ServiceProvider.GetRequiredService<CardEmbeddingPipeline>();
                    var e = await pipeline.RunAsync(ct: ct);
                    if (e.Embedded > 0)
                        logger.LogInformation("Embeddings: {Count} kaarten geembed", e.Embedded);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Embed-pijplijn overgeslagen (Ollama onbereikbaar?)");
                }

                // Nachtelijke claims-harvest (#50): community-interpretatie
                // destilleren uit de register-bronnen. Best-effort — de run
                // logt zelf per bron naar run_log.
                if (DateTimeOffset.UtcNow - _lastClaimsMine >= ClaimsMineInterval)
                {
                    try
                    {
                        var claims = scope.ServiceProvider.GetRequiredService<ClaimMiningService>();
                        var c = await claims.RunAsync(ct: ct);
                        _lastClaimsMine = DateTimeOffset.UtcNow;
                        if (c.Documents > 0) logger.LogInformation("Claims: {Detail}", c.Message);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Claims-harvest overgeslagen (rb-ai/Ollama onbereikbaar?)");
                    }
                }

                // Mine mechanieken voor nieuwe kaarten + sync de graph (best-effort).
                try
                {
                    var mining = scope.ServiceProvider.GetRequiredService<MechanicMiningService>();
                    var m = await mining.RunAsync(maxBatches: 5, ct: ct);
                    if (m.Mined > 0)
                    {
                        logger.LogInformation("Mechanieken: {Mined} kaarten gemined ({Remaining} resterend)",
                            m.Mined, m.Remaining);
                        var graph = scope.ServiceProvider.GetRequiredService<GraphSyncService>();
                        await graph.SyncAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Mining/graph-sync overgeslagen (rb-ai/Neo4j onbereikbaar?)");
                }

                // Periodieke zelfverrijking (#122): relatie-mining nachtelijk,
                // de bronnen-scout wekelijks. Beide draaien als gewone
                // JobRunner-job — dezelfde éénjob-gate als handmatige jobs
                // (nooit twee tegelijk), dezelfde live-voortgang in beheer en
                // dezelfde degradatiepaden in de services zelf. Het run_log-
                // grootboek (kind "job", door JobRunner geschreven bij elke
                // afronding) bepaalt het venster: een handmatige run gisteren
                // of een container-herstart veroorzaakt geen dubbele run.
                await TryStartPeriodicJobAsync(scope.ServiceProvider, "relations", RelationsMineInterval, ct);
                await TryStartPeriodicJobAsync(scope.ServiceProvider, "scout", ScoutInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler-tick faalde");
            }
            await Task.Delay(Tick, ct);
        }
    }

    /// <summary>Start een catalogus-job zodra zijn venster verstreken is én
    /// er geen andere job draait (JobRunner-gate). Een TryStart die false
    /// geeft is geen fout: er draait al iets, en zolang de job niet gedraaid
    /// heeft blijft het venster open — de volgende tick probeert opnieuw.</summary>
    private async Task TryStartPeriodicJobAsync(
        IServiceProvider sp, string name, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var lastRun = await sp.GetRequiredService<JobLedger>().LastRunAsync(name, ct);
            if (!Scheduling.IsWindowDue(window, lastRun, DateTimeOffset.UtcNow)) return;
            if (JobCatalog.Find(name) is not { } job)
            {
                logger.LogError("Periodieke job '{Name}' bestaat niet in de JobCatalog", name);
                return;
            }
            if (jobs.TryStart(name, job.Run))
                logger.LogInformation(
                    "Periodieke job '{Name}' gestart (vorige run: {LastRun})",
                    name, lastRun?.ToString("u") ?? "nog nooit");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Grootboek even onleesbaar (db-hik): loggen en volgende tick
            // opnieuw — de scheduler zelf mag hier nooit op stoppen.
            logger.LogWarning(ex, "Periodieke job '{Name}' niet gestart", name);
        }
    }
}
