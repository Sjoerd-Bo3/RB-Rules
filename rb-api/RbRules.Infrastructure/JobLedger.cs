using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

public record JobRunInfo(string Name, string Status, DateTimeOffset At);

/// <summary>Run_log als grootboek voor jobs (#122): JobRunner schrijft bij
/// elke jobafronding een kind="job"-regel met de jobnaam als ref — voor
/// handmatige én automatische runs. Dit leest dat grootboek terug: de
/// scheduler bepaalt er zijn vensters mee (geen dubbele run binnen het
/// venster, ook niet na een herstart of vlak na een handmatige run) en
/// beheer toont er de laatste run per job mee. Een run met status "error"
/// vult het venster ook — herstel loopt via de handmatige job en de
/// foutdetails in run_log (SetReleaseService-afspraak), niet via elke tick
/// opnieuw proberen.</summary>
public class JobLedger(RbRulesDbContext db)
{
    public async Task<DateTimeOffset?> LastRunAsync(string job, CancellationToken ct = default)
        => await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "job" && l.Ref == job)
            .MaxAsync(l => (DateTimeOffset?)l.CreatedAt, ct);

    /// <summary>Laatste afronding per job (tijd + status), voor de jobs-lijst
    /// in beheer. Twee stappen (Interactions-patroon): eerst het aggregaat in
    /// SQL, dan de status van precies die regels — nooit alle jobregels
    /// materialiseren.</summary>
    public async Task<IReadOnlyList<JobRunInfo>> LastRunsAsync(CancellationToken ct = default)
    {
        var latest = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "job" && l.Ref != null)
            .GroupBy(l => l.Ref!)
            .Select(g => new { Name = g.Key, At = g.Max(l => l.CreatedAt) })
            .ToListAsync(ct);
        if (latest.Count == 0) return [];

        var stamps = latest.Select(x => x.At).ToList();
        var rows = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "job" && l.Ref != null && stamps.Contains(l.CreatedAt))
            .Select(l => new { l.Ref, l.Status, l.CreatedAt })
            .ToListAsync(ct);
        return latest
            .Select(x => new JobRunInfo(
                x.Name,
                rows.FirstOrDefault(r => r.Ref == x.Name && r.CreatedAt == x.At)?.Status ?? "ok",
                x.At))
            .OrderBy(x => x.Name)
            .ToList();
    }
}
