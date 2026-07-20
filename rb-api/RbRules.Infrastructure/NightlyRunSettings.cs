namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de nachtelijke ongelimiteerde run (#245). Singleton in
/// DI, uit de omgeving gelezen (<see cref="FromEnvironment"/>) — zelfde patroon als de
/// andere env-settings. Defaults: AAN, venster 00:00–11:00 Europe/Amsterdam. Env-
/// overschrijfbaar op de VM-<c>.env</c> zonder code-wijziging (<c>NIGHTLY_ENABLED</c>,
/// <c>NIGHTLY_START_HOUR</c>, <c>NIGHTLY_END_HOUR</c>, <c>NIGHTLY_TZ</c>). Overdag
/// ongewijzigd: dit stuurt alleen wanneer/of de <c>nachtrun</c>-job automatisch
/// start.</summary>
/// <param name="Enabled">Mag de <c>ScanScheduler</c> de nachtrun AUTOMATISCH starten?
/// Default true; <c>NIGHTLY_ENABLED=false</c> pauzeert de automatische run zolang de
/// extractie nog niet deugt (#249/#251) — zonder de code aan te raken en zonder de
/// beheer-knop te blokkeren: handmatig starten blijft altijd werken.</param>
public sealed record NightlyRunSettings(
    int StartHour, int EndHour, string TimeZoneId, bool Enabled = true)
{
    public static readonly NightlyRunSettings Default = new(0, 11, "Europe/Amsterdam");

    public static NightlyRunSettings FromEnvironment()
    {
        var start = ParseHour("NIGHTLY_START_HOUR", 0);
        var end = ParseHour("NIGHTLY_END_HOUR", 11);
        var tz = Environment.GetEnvironmentVariable("NIGHTLY_TZ") is { Length: > 0 } t
            ? t
            : "Europe/Amsterdam";
        var enabled = ParseEnabled("NIGHTLY_ENABLED");
        // Alleen dezelfde-dag-vensters (start < end): de eenmaal-per-dag-dedup
        // (NightlyWindow.RanToday, op kalenderdag) klopt niet voor een middernacht-
        // kruisend venster. Ongeldige config → terug naar de default 00:00–11:00
        // (#245-review). De aan/uit-vlag staat daar los van: een ongeldig VENSTER
        // mag de bewuste keuze om te pauzeren niet ongedaan maken.
        return start < end ? new(start, end, tz, enabled) : new(0, 11, tz, enabled);
    }

    private static int ParseHour(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v is >= 0 and <= 23
            ? v
            : fallback;

    /// <summary>Alleen een expliciete uit-waarde schakelt de nachtrun uit
    /// (false/0/no/off, hoofdletterongevoelig). Onzin of een lege waarde valt terug
    /// op AAN: een typfout in de <c>.env</c> mag de nachtelijke keten niet stilletjes
    /// stilleggen — uitzetten moet een bewuste, herkenbare handeling zijn.</summary>
    private static bool ParseEnabled(string envVar) =>
        (Environment.GetEnvironmentVariable(envVar) ?? "").Trim().ToLowerInvariant()
            is not ("false" or "0" or "no" or "off");
}
