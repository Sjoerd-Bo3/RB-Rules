using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de nachtelijke ongelimiteerde run (#245). Defaults: AAN,
/// venster 00:00–11:00 Europe/Amsterdam. De omgeving is sinds #254 de
/// BOOTSTRAP-DEFAULT (<see cref="FromEnvironment"/>, <c>NIGHTLY_ENABLED</c>,
/// <c>NIGHTLY_START_HOUR</c>, <c>NIGHTLY_END_HOUR</c>, <c>NIGHTLY_TZ</c>); beheer kan
/// er via de <c>setting</c>-tabel overheen schrijven
/// (<see cref="WithOverrides"/>). Niet meer als singleton injecteren: vraag hem op
/// het GEBRUIKSMOMENT op bij <see cref="ManagedSettingsService"/>, anders mist een
/// toggle zijn effect tot de volgende herstart. Overdag ongewijzigd: dit stuurt alleen
/// wanneer/of de <c>nachtrun</c>-job automatisch start.</summary>
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

    /// <summary>Leg de beheerde overrides (#254) over deze (env-)basiswaarden heen.
    /// Een ONTBREKENDE sleutel laat het bestaande veld ongemoeid — dat is de harde
    /// eis: lege <c>setting</c>-tabel ⇒ exact het env-gedrag. Onleesbare waarden
    /// (handmatig in de DB gerommeld) vallen per veld terug op de basiswaarde in
    /// plaats van de hele instelling te laten ontsporen.</summary>
    public NightlyRunSettings WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0) return this;

        var enabled = overrides.TryGetValue(SettingKeys.NightlyEnabled, out var rawEnabled)
            && ManagedSettingsCatalog.ParseBool(rawEnabled) is { } e ? e : Enabled;
        var start = OverrideHour(overrides, SettingKeys.NightlyStartHour, StartHour);
        var end = OverrideHour(overrides, SettingKeys.NightlyEndHour, EndHour);
        var tz = overrides.TryGetValue(SettingKeys.NightlyTimeZone, out var rawTz)
            && rawTz.Length > 0 ? rawTz : TimeZoneId;

        // Zelfde vangnet als FromEnvironment: alleen dezelfde-dag-vensters. Het
        // admin-endpoint weigert een ongeldig venster al mét uitleg, dus dit dekt
        // alleen een direct in de DB gezette combinatie — die valt terug op het
        // basisvenster, niet op "nooit draaien". De aan/uit-vlag staat daar los van.
        return start < end
            ? this with { StartHour = start, EndHour = end, TimeZoneId = tz, Enabled = enabled }
            : this with { TimeZoneId = tz, Enabled = enabled };
    }

    /// <summary>De BEDOELDE vensteruren uit de overrides, vóór het
    /// ongeldig-venster-vangnet van <see cref="WithOverrides"/>. Beheer heeft dit nodig
    /// om een ongeldige combinatie te WEIGEREN: na het vangnet ziet zo'n combinatie er
    /// geldig uit (hij is dan al teruggevallen), en dan zou de knop stilletjes niets
    /// doen.</summary>
    public (int Start, int End) IntendedWindow(IReadOnlyDictionary<string, string> overrides) =>
        (OverrideHour(overrides, SettingKeys.NightlyStartHour, StartHour),
         OverrideHour(overrides, SettingKeys.NightlyEndHour, EndHour));

    private static int OverrideHour(
        IReadOnlyDictionary<string, string> overrides, string key, int fallback) =>
        overrides.TryGetValue(key, out var raw) && int.TryParse(raw, out var v)
            && v is >= 0 and <= 23 ? v : fallback;

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
