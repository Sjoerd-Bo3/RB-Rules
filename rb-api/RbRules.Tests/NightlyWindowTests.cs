using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Nachtrun-venster (#245): pure klok-logica en de config-defaults. Een vaste
/// +2-offset-zone maakt de tests onafhankelijk van host-tzdata en DST.</summary>
public class NightlyWindowTests
{
    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Theory]
    // venster [0,11): lokaal = UTC + 2
    [InlineData(0, true)]    // 00:30 UTC → 02:30 lokaal → uur 2 → in
    [InlineData(9, false)]   // 09:30 UTC → 11:30 lokaal → uur 11 → uit (half-open)
    [InlineData(22, true)]   // 22:30 UTC → 00:30 lokaal → uur 0 → in
    [InlineData(20, false)]  // 20:30 UTC → 22:30 lokaal → uur 22 → uit
    public void InWindow_HalfOpen_00_11(int utcHour, bool expected)
    {
        var now = Utc(2026, 7, 20, utcHour, 30);
        Assert.Equal(expected, NightlyWindow.InWindow(now, Plus2, 0, 11));
    }

    [Fact]
    public void Settings_MiddernachtKruisendVenster_ValtTerugOpDefault()
    {
        // start >= end (bv. 22–06) wordt niet ondersteund (RanToday is kalenderdag-
        // gebaseerd) → FromEnvironment valt terug op de default 00–11 (#245-review).
        var start = Environment.GetEnvironmentVariable("NIGHTLY_START_HOUR");
        var end = Environment.GetEnvironmentVariable("NIGHTLY_END_HOUR");
        try
        {
            Environment.SetEnvironmentVariable("NIGHTLY_START_HOUR", "22");
            Environment.SetEnvironmentVariable("NIGHTLY_END_HOUR", "6");
            var s = NightlyRunSettings.FromEnvironment();
            Assert.Equal(0, s.StartHour);
            Assert.Equal(11, s.EndHour);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NIGHTLY_START_HOUR", start);
            Environment.SetEnvironmentVariable("NIGHTLY_END_HOUR", end);
        }
    }

    [Fact]
    public void Deadline_VandaagElfUurLokaal_AlsVoorHetEinde()
    {
        // 00:30 UTC → 02:30 lokaal; deadline = vandaag 11:00 lokaal = 09:00 UTC.
        var deadline = NightlyWindow.Deadline(Utc(2026, 7, 20, 0, 30), Plus2, 11);
        Assert.Equal(Utc(2026, 7, 20, 9, 0), deadline.ToUniversalTime());
    }

    [Fact]
    public void Deadline_WrapsNaarMorgen_AlsHetEindeAlVoorbij()
    {
        // 12:00 UTC → 14:00 lokaal (na 11:00); deadline = morgen 11:00 lokaal = 09:00 UTC op de 21e.
        var deadline = NightlyWindow.Deadline(Utc(2026, 7, 20, 12, 0), Plus2, 11);
        Assert.Equal(Utc(2026, 7, 21, 9, 0), deadline.ToUniversalTime());
    }

    [Fact]
    public void RanToday_VergelijktLokaleKalenderdag()
    {
        var now = Utc(2026, 7, 20, 1, 0);            // 03:00 lokaal, 20 juli
        Assert.False(NightlyWindow.RanToday(null, now, Plus2));
        Assert.True(NightlyWindow.RanToday(Utc(2026, 7, 19, 23, 30), now, Plus2)); // 20 juli 01:30 lokaal → zelfde dag
        Assert.False(NightlyWindow.RanToday(Utc(2026, 7, 19, 12, 0), now, Plus2)); // 19 juli 14:00 lokaal → vorige dag
    }

    [Fact]
    public void ResolveTimeZone_OnbekendeId_ValtTerugOpUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, NightlyWindow.ResolveTimeZone("Geen/Bestaande_Zone"));
        Assert.Equal("Europe/Amsterdam", NightlyRunSettings.Default.TimeZoneId);
    }

    [Fact]
    public void UncappedConstants_LopenNietOver_InDeMiningRekensommen()
    {
        // De services doen Take(maxFocusCards + 1) en maxBatches * BatchSize(8);
        // int.MaxValue zou overlopen — de sentinels moeten veilig blijven.
        Assert.True(NightlyWindow.UncappedItems + 1 > 0);
        Assert.True((long)NightlyWindow.UncappedBatches * 8 < int.MaxValue);
        Assert.True(NightlyWindow.UncappedItems > 100_000); // ruim boven elke kaart-/entiteit-telling
    }

    [Fact]
    public void Nachtrun_StaatInDeJobCatalog()
    {
        // De scheduler start "nachtrun" op naam — die referentie mag nooit stil breken
        // (zelfde vangnet als ScanSchedulerScheduleTests voor de JobSchedules).
        Assert.NotNull(JobCatalog.Find("nachtrun"));
    }
}
