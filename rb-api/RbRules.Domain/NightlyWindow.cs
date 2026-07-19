namespace RbRules.Domain;

/// <summary>Nachtrun-tijdlogica (#245): puur en testbaar — elke functie neemt 'now'
/// als parameter, geen klok-injectie nodig. Het venster is KLOK-gebaseerd (lokale
/// uren), niet interval-gebaseerd zoals <see cref="Scheduling.IsWindowDue"/>: de grote
/// ongelimiteerde run moet 's nachts in een vast venster vallen, niet "X uur sinds de
/// vorige run". De deadline begrenst de duur zodat de run niet de werkdag in loopt.</summary>
public static class NightlyWindow
{
    /// <summary>Ongelimiteerde nachtrun-caps: ruim boven elke realistische kaart-/
    /// entiteit-telling, maar veilig onder <see cref="int.MaxValue"/> zodat de
    /// <c>Take(N + 1)</c>- en <c>N * BatchSize</c>-rekensommen in de mining-services
    /// niet overlopen (int.MaxValue + 1 = overflow).</summary>
    public const int UncappedItems = 1_000_000;
    public const int UncappedBatches = 100_000;

    /// <summary>De tijdzone, met veilige terugval op UTC als de IANA-id op deze host
    /// onbekend is (het venster draait dan in UTC — te loggen, geen crash).</summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    /// <summary>Valt de lokale tijd van <paramref name="nowUtc"/> binnen [start, end)?
    /// Half-open: het end-uur (bv. 11) valt al buiten het venster. Vensters die
    /// middernacht kruisen (start &gt; end, bv. 22–06) worden ondersteund.</summary>
    public static bool InWindow(DateTimeOffset nowUtc, TimeZoneInfo tz, int startHour, int endHour)
    {
        var h = TimeZoneInfo.ConvertTime(nowUtc, tz).Hour;
        return startHour <= endHour
            ? h >= startHour && h < endHour
            : h >= startHour || h < endHour; // venster kruist middernacht
    }

    /// <summary>De deadline (UTC) waarop de nachtrun stopt: het eerstvolgende
    /// <paramref name="endHour"/> in lokale tijd op/na <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset Deadline(DateTimeOffset nowUtc, TimeZoneInfo tz, int endHour)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var endToday = new DateTimeOffset(local.Year, local.Month, local.Day, endHour, 0, 0, local.Offset);
        if (endToday <= local) endToday = endToday.AddDays(1);
        return endToday.ToUniversalTime();
    }

    /// <summary>Is de nachtrun vandaag (lokale kalenderdag) al gedraaid? Voorkomt een
    /// tweede start binnen hetzelfde venster. <paramref name="lastRunUtc"/> = laatste
    /// voltooiing (JobLedger/run_log); null = nog nooit.</summary>
    public static bool RanToday(DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        if (lastRunUtc is null) return false;
        var last = TimeZoneInfo.ConvertTime(lastRunUtc.Value, tz).Date;
        var now = TimeZoneInfo.ConvertTime(nowUtc, tz).Date;
        return last == now;
    }
}
