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
    /// Half-open: het end-uur (bv. 11) valt al buiten het venster. Het venster valt
    /// binnen één kalenderdag (start &lt; end — afgedwongen door NightlyRunSettings);
    /// een middernacht-kruisend venster wordt bewust NIET ondersteund omdat de
    /// kalenderdag-dedup van <see cref="RanToday"/> daar niet voor klopt (#245-review).</summary>
    public static bool InWindow(DateTimeOffset nowUtc, TimeZoneInfo tz, int startHour, int endHour)
    {
        var h = TimeZoneInfo.ConvertTime(nowUtc, tz).Hour;
        return h >= startHour && h < endHour;
    }

    /// <summary>De deadline (UTC) waarop de nachtrun stopt: het eerstvolgende
    /// <paramref name="endHour"/> in lokale wandkloktijd op/na <paramref name="nowUtc"/>.
    /// De wandkloktijd wordt via de tz-regels ZELF naar UTC omgezet, zodat de offset
    /// van het EIND-uur telt (niet die van 'now') — op een DST-overgangsdag scheelt
    /// dat anders een uur (#245-review).</summary>
    public static DateTimeOffset Deadline(DateTimeOffset nowUtc, TimeZoneInfo tz, int endHour)
    {
        var localDate = TimeZoneInfo.ConvertTime(nowUtc, tz).Date;
        var endWall = new DateTime(localDate.Year, localDate.Month, localDate.Day, endHour, 0, 0,
            DateTimeKind.Unspecified);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endWall, tz);
        if (endUtc <= nowUtc.UtcDateTime)
            endUtc = TimeZoneInfo.ConvertTimeToUtc(endWall.AddDays(1), tz);
        return new DateTimeOffset(endUtc, TimeSpan.Zero);
    }

    /// <summary>Is de nachtrun vandaag (lokale kalenderdag) al gedraaid? Voorkomt een
    /// tweede start binnen hetzelfde venster — correct omdat het venster binnen één
    /// kalenderdag valt (<see cref="InWindow"/>, start &lt; end). <paramref
    /// name="lastRunUtc"/> = laatste voltooiing (JobLedger/run_log); null = nog nooit.</summary>
    public static bool RanToday(DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        if (lastRunUtc is null) return false;
        var last = TimeZoneInfo.ConvertTime(lastRunUtc.Value, tz).Date;
        var now = TimeZoneInfo.ConvertTime(nowUtc, tz).Date;
        return last == now;
    }
}
