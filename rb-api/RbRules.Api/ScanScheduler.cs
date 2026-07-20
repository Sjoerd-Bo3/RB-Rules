using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>In-app scheduler (audit-fix: geen handmatige crontab meer).
/// Elk uur: scan bronnen die volgens hun cadence aan de beurt zijn.
/// Elke week: kaart-sync (nieuwe sets/errata). Periodieke zelfverrijking
/// (#122): relatie-mining nachtelijk, de bronnen-scout wekelijks, de
/// Piltover-decks-verversing (#15 fase 3, spoor C) elke paar uur en de
/// FAQ-/clarificatie-concept-extractie nachtelijk (#177), als gewone
/// JobRunner-jobs op het run_log-grootboek.</summary>
public class ScanScheduler(
    IServiceScopeFactory scopeFactory, JobRunner jobs, ManagedSettingsService settings,
    ILogger<ScanScheduler> logger)
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
    // Piltover-decks (#15 fase 3, spoor C): DeckIngestService cap't op 400
    // pagina's/run en throttlet ~1,5s/request (netiquette, #148) — een run
    // duurt daardoor tot ~10 minuten. De sitemap meldt ~10.186 decks, dus de
    // eenmalige backfill vraagt ~26 runs; bij een venster van 3 uur is die
    // in ruim 3 dagen binnen zonder PA te bestoken (elke andere periodieke
    // job hierboven is dagelijks of wekelijks — decks is de enige met een
    // grote eenmalige achterstand). Ná de backfill kost een run bijna niets
    // meer: het run_log-grootboek + de sitemap-lastmod-check laten alleen
    // nieuwe of gewijzigde decks nog door, dus vers spul komt met dezelfde
    // 3-uurscadans vanzelf binnen — geen handmatige job meer nodig.
    private static readonly TimeSpan DecksInterval = TimeSpan.FromHours(3);
    // FAQ-/clarificatie-concept-extractie (#177): zelfde nachtritme als de
    // claims-harvest — nieuwe FAQ-artikelen verschijnen niet vaker dan
    // dagelijks, en de run is idempotent (ClarifiedAt + exacte-tekst-toets).
    private static readonly TimeSpan ClarifyMineInterval = TimeSpan.FromDays(1);
    // Changeconsolidatie (#206, review-fix finding 5): de uurlijkse scan
    // maakt de duplicaten, dus de consolidatie moet ook zonder handmatig
    // ingest-pad draaien — elke tick (venster = ticklengte). Goedkoop:
    // zonder verse changes levert de kandidaat-poort niets op, en de
    // pair-memo's (run_log "pair:{a}-{b}" rejected) voorkomen dat een
    // overlevend paar ooit twee keer aan de LLM wordt voorgelegd.
    private static readonly TimeSpan ConsolidateChangesInterval = TimeSpan.FromHours(1);

    /// <summary>Periodieke JobCatalog-jobs (#122-mechaniek): declaratief,
    /// zodat de registratie testbaar is (ScanSchedulerScheduleTests valideert
    /// dat elke naam in de JobCatalog bestaat — zelfde vangnet als
    /// JobPathsTests voor padstappen).</summary>
    public static readonly IReadOnlyList<(string JobName, TimeSpan Window)> JobSchedules =
    [
        ("relations", RelationsMineInterval),
        ("scout", ScoutInterval),
        ("decks", DecksInterval),
        ("clarify", ClarifyMineInterval),
        ("consolidatechanges", ConsolidateChangesInterval),
    ];

    // Paden periodiek inplanbaar (#190): zelfde venster-mechanisme als de
    // losse jobs hierboven (JobLedger bepaalt het venster, TryStartPeriodicPathAsync
    // is het pad-equivalent van TryStartPeriodicJobAsync), maar bewust LEEG —
    // dit voegt alleen de MOGELIJKHEID toe om een pad in te plannen. De
    // bestaande nachtelijke/wekelijkse cadans hierboven (relations/scout/
    // decks/clarify als losse jobs) verandert niet; een pad hier inplannen is
    // een latere, bewuste keuze (een nieuwe entry), geen gedragswijziging van
    // deze PR.
    private static readonly IReadOnlyList<(string PathName, TimeSpan Window)> PathSchedules = [];
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

                // Periodieke zelfverrijking (#122, #15 fase 3 spoor C, #206):
                // relatie-mining nachtelijk, de bronnen-scout wekelijks, de
                // Piltover-decks-verversing elke 3 uur, clarify nachtelijk en
                // de changeconsolidatie uurlijks. Alle draaien als gewone
                // JobRunner-job — dezelfde éénjob-gate als handmatige jobs
                // (nooit twee tegelijk), dezelfde live-voortgang in beheer en
                // dezelfde degradatiepaden in de services zelf. Het
                // run_log-grootboek (kind "job", door JobRunner geschreven
                // bij elke afronding) bepaalt het venster: een handmatige run
                // gisteren of een container-herstart veroorzaakt geen dubbele
                // run. "decks" hergebruikt DeckIngestService ongewijzigd
                // (sitemap → pagina's → opslag, #148) en hervat de backfill
                // vanzelf waar het grootboek bleef.
                // Nachtrun (#245) VÓÓR de periodieke jobs: de volledige ONGECAPTE keten
                // in een klok-venster (default 00:00–11:00 lokaal). Anders dan de
                // interval-schedules hieronder is dit klok-gebaseerd — de grote run moet
                // 's nachts vallen. Bewust als EERSTE gate-poging in de tick: een
                // elke-tick-due periodieke job (consolidatechanges) grijpt anders de
                // single-job-gate vóór de nachtrun en starveert 'm een hele nacht
                // (review #245). De inline stappen hierboven gebruiken de gate niet en
                // blokkeren de nachtrun dus niet.
                await TryStartNightlyAsync(scope.ServiceProvider, ct);

                foreach (var (jobName, window) in JobSchedules)
                    await TryStartPeriodicJobAsync(scope.ServiceProvider, jobName, window, ct);

                // Paden (#190): PathSchedules is leeg (zie hierboven) — deze
                // lus is vandaag een no-op en verandert dus niets aan de
                // bestaande cadans; de mogelijkheid staat wel klaar.
                foreach (var (pathName, window) in PathSchedules)
                    await TryStartPeriodicPathAsync(scope.ServiceProvider, pathName, window, ct);
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

    /// <summary>Nachtrun (#245): start de volledige ongecapte keten binnen het
    /// KLOK-venster (default 00:00–11:00 lokaal), maximaal één keer per lokale
    /// kalenderdag (<see cref="NightlyWindow.RanToday"/> op het run_log-grootboek).
    /// Anders dan de interval-schedules hierboven is dit klok-gebaseerd. De
    /// single-job-gate (<see cref="JobRunner.TryStart"/>) voorkomt dubbelstart; een
    /// lopende run houdt het slot vast tot de deadline, dus een tweede tick binnen
    /// het venster start niets (TryStart=false, geen fout).
    ///
    /// Staat de noodrem UIT (beheer → nachtrun, of <c>NIGHTLY_ENABLED=false</c> als
    /// bootstrap-default), dan start de scheduler de nachtrun niet — de rem om de
    /// nachtelijke keten te pauzeren zolang de extractie nog niet deugt (#249/#251).
    /// Handmatig starten via de beheer-knop blijft werken: de vlag zit hier, niet in
    /// de JobCatalog. Sinds #254 wordt de instelling PER TICK gelezen (beheerde
    /// instelling, uurlijks — dus verwaarloosbaar), zodat de rem meteen pakt zonder
    /// herstart of redeploy.</summary>
    private async Task TryStartNightlyAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            var nightly = await settings.NightlyAsync(ct);
            if (!nightly.Enabled) return;
            var tz = NightlyWindow.ResolveTimeZone(nightly.TimeZoneId);
            var now = DateTimeOffset.UtcNow;
            if (!NightlyWindow.InWindow(now, tz, nightly.StartHour, nightly.EndHour)) return;
            var lastRun = await sp.GetRequiredService<JobLedger>().LastRunAsync("nachtrun", ct);
            if (NightlyWindow.RanToday(lastRun, now, tz)) return;
            if (JobCatalog.Find("nachtrun") is not { } job)
            {
                logger.LogError("Nachtrun-job 'nachtrun' bestaat niet in de JobCatalog");
                return;
            }
            if (jobs.TryStart("nachtrun", job.Run))
                logger.LogInformation(
                    "Nachtrun gestart (venster {Start}:00–{End}:00 {Tz}, vorige run: {LastRun})",
                    nightly.StartHour, nightly.EndHour, nightly.TimeZoneId,
                    lastRun?.ToString("u") ?? "nog nooit");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nachtrun niet gestart");
        }
    }

    /// <summary>Pad-equivalent van <see cref="TryStartPeriodicJobAsync"/>
    /// (#190): zelfde venster-/gate-logica, maar start een heel
    /// <see cref="PathDefinition"/> via <see cref="PathRunner"/>. Padrun-
    /// afrondingen landen (via JobRunner) op dezelfde Kind="job"-regel als een
    /// losse job, dus <see cref="JobLedger.LastRunAsync"/> werkt hier
    /// ongewijzigd op de padnaam.</summary>
    private async Task TryStartPeriodicPathAsync(
        IServiceProvider sp, string name, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var lastRun = await sp.GetRequiredService<JobLedger>().LastRunAsync(name, ct);
            if (!Scheduling.IsWindowDue(window, lastRun, DateTimeOffset.UtcNow)) return;
            if (JobPaths.Find(name) is not { } path)
            {
                logger.LogError("Periodiek pad '{Name}' bestaat niet in JobPaths", name);
                return;
            }
            if (jobs.TryStart(name, (p, report, token) => PathRunner.RunAsync(path, p, report, token)))
                logger.LogInformation(
                    "Periodiek pad '{Name}' gestart (vorige run: {LastRun})",
                    name, lastRun?.ToString("u") ?? "nog nooit");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Periodiek pad '{Name}' niet gestart", name);
        }
    }
}
