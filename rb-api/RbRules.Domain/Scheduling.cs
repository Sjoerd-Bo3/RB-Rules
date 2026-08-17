namespace RbRules.Domain;

/// <summary>Audit-fix: 'cadence' was dode configuratie in de PoP — de scanner
/// negeerde het veld. Hier bepaalt het echt wanneer een bron aan de beurt is.</summary>
public static class Scheduling
{
    /// <summary>Kleine marge zodat een uurlijkse/dagelijkse tick het venster
    /// niet nét mist — anders schuift elke run een tick verder op.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(30);

    public static bool IsDue(string cadence, DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        var interval = cadence switch
        {
            "weekly" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1), // daily (default)
        };
        return IsWindowDue(interval, lastChecked, now);
    }

    /// <summary>Periodieke zelfverrijking (#122): is het venster sinds de
    /// laatste run verstreken? lastRun komt uit het run_log-grootboek
    /// (JobLedger), zodat een handmatige run het venster ook vult en een
    /// container-herstart geen dubbele run veroorzaakt.</summary>
    public static bool IsWindowDue(TimeSpan window, DateTimeOffset? lastRun, DateTimeOffset now)
        => lastRun is null || now - lastRun.Value >= window - Margin;
}
