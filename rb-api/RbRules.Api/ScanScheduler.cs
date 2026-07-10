using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>In-app scheduler (audit-fix: geen handmatige crontab meer).
/// Elk uur: scan bronnen die volgens hun cadence aan de beurt zijn.
/// Elke week: kaart-sync (nieuwe sets/errata).</summary>
public class ScanScheduler(IServiceScopeFactory scopeFactory, ILogger<ScanScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    // Dagelijks: nieuwe sets/reveals verschijnen in Riot's gallery en komen zo
    // binnen een dag automatisch binnen (incl. embeddings + mining via de tick).
    private static readonly TimeSpan CardSyncInterval = TimeSpan.FromDays(1);
    private DateTimeOffset _lastCardSync = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Even wachten tot migraties/seed klaar zijn en de stack op stoom is.
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingest = scope.ServiceProvider.GetRequiredService<IngestService>();
                var results = await ingest.ScanAsync(onlyDue: true, ct: ct);
                if (results.Count > 0)
                    logger.LogInformation("Scan: {Results}",
                        string.Join(", ", results.Select(r => $"{r.SourceId}={r.Status}")));

                // Bij nieuwe/gewijzigde documenten: her-indexeren (sectie-chunks +
                // embeddings) en de banlijst/errata opnieuw structureren.
                // Audit-fix: de index loopt nooit meer stilzwijgend achter op een scan.
                if (results.Any(r => r.Status is "changed" or "new"))
                {
                    try
                    {
                        var rules = scope.ServiceProvider.GetRequiredService<RuleChunkPipeline>();
                        var indexed = await rules.RunAsync(ct);
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

                if (DateTimeOffset.UtcNow - _lastCardSync >= CardSyncInterval)
                {
                    var cards = scope.ServiceProvider.GetRequiredService<CardSyncService>();
                    var r = await cards.SyncAsync(ct);
                    _lastCardSync = DateTimeOffset.UtcNow;
                    logger.LogInformation("Kaart-sync: {Sets} sets, {Cards} kaarten via {Source}",
                        r.Sets, r.Cards, r.Source);
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

                // Mine mechanieken voor nieuwe kaarten + sync de graph (best-effort).
                try
                {
                    var mining = scope.ServiceProvider.GetRequiredService<MechanicMiningService>();
                    var m = await mining.RunAsync(maxBatches: 5, ct);
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
}
