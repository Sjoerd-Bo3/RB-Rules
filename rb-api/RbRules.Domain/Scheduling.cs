namespace RbRules.Domain;

/// <summary>Audit-fix: 'cadence' was dode configuratie in de PoP — de scanner
/// negeerde het veld. Hier bepaalt het echt wanneer een bron aan de beurt is.</summary>
public static class Scheduling
{
    public static bool IsDue(string cadence, DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        if (lastChecked is null) return true;
        var interval = cadence switch
        {
            "weekly" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1), // daily (default)
        };
        // Kleine marge zodat een dagelijkse cron/tick niet nét te vroeg valt.
        return now - lastChecked.Value >= interval - TimeSpan.FromMinutes(30);
    }
}
