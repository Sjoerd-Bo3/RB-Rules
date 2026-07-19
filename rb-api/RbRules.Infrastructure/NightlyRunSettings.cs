namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de nachtelijke ongelimiteerde run (#245). Singleton in
/// DI, uit de omgeving gelezen (<see cref="FromEnvironment"/>) — zelfde patroon als de
/// andere env-settings. Defaults: venster 00:00–11:00 Europe/Amsterdam. Env-
/// overschrijfbaar op de VM-<c>.env</c> zonder code-wijziging (<c>NIGHTLY_START_HOUR</c>,
/// <c>NIGHTLY_END_HOUR</c>, <c>NIGHTLY_TZ</c>). Overdag ongewijzigd: dit stuurt alleen
/// wanneer/of de <c>nachtrun</c>-job automatisch start.</summary>
public sealed record NightlyRunSettings(int StartHour, int EndHour, string TimeZoneId)
{
    public static readonly NightlyRunSettings Default = new(0, 11, "Europe/Amsterdam");

    public static NightlyRunSettings FromEnvironment()
    {
        var start = ParseHour("NIGHTLY_START_HOUR", 0);
        var end = ParseHour("NIGHTLY_END_HOUR", 11);
        var tz = Environment.GetEnvironmentVariable("NIGHTLY_TZ") is { Length: > 0 } t
            ? t
            : "Europe/Amsterdam";
        // Alleen dezelfde-dag-vensters (start < end): de eenmaal-per-dag-dedup
        // (NightlyWindow.RanToday, op kalenderdag) klopt niet voor een middernacht-
        // kruisend venster. Ongeldige config → terug naar de default 00:00–11:00
        // (#245-review).
        return start < end ? new(start, end, tz) : new(0, 11, tz);
    }

    private static int ParseHour(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v is >= 0 and <= 23
            ? v
            : fallback;
}
