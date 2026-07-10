using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>In-app scheduler (audit-fix: geen handmatige crontab meer).
/// Elk uur: scan bronnen die volgens hun cadence aan de beurt zijn.
/// Elke week: kaart-sync (nieuwe sets/errata).</summary>
public class ScanScheduler(IServiceScopeFactory scopeFactory, ILogger<ScanScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private static readonly TimeSpan CardSyncInterval = TimeSpan.FromDays(7);
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

                if (DateTimeOffset.UtcNow - _lastCardSync >= CardSyncInterval)
                {
                    var cards = scope.ServiceProvider.GetRequiredService<CardSyncService>();
                    var r = await cards.SyncAsync(ct);
                    _lastCardSync = DateTimeOffset.UtcNow;
                    logger.LogInformation("Kaart-sync: {Sets} sets, {Cards} kaarten via {Source}",
                        r.Sets, r.Cards, r.Source);
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
