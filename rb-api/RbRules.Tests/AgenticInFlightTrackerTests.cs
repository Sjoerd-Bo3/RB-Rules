using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Unit-tests op de in-flight-reservering (#153, TOCTOU-fix): twee
/// gelijktijdige beslissingen op het laatste permit mogen er samen maar één
/// laten escaleren als gebruiker. De reservering is puur in-proces (single
/// instance), dus deze tests zijn deterministisch zonder DB.</summary>
public class AgenticInFlightTrackerTests
{
    private const long UserId = 42;

    [Fact]
    public void TryReserve_LaatstePermit_TweeGelijktijdigeBeslissingen_SlechtsEenSlaagt()
    {
        var tracker = new AgenticInFlightTracker();
        // db-teller = quota-1: er is nog precies één permit. Beide requests
        // zien dezelfde db-teller (de eerste heeft zijn metric nog niet
        // geschreven) — precies het gelijktijdigheidsvenster.
        var first = tracker.TryReserve(UserId, dbCountToday: 4, dailyQuota: 5);
        var second = tracker.TryReserve(UserId, dbCountToday: 4, dailyQuota: 5);

        Assert.NotNull(first);
        Assert.Null(second); // de tweede vindt geen tegoed meer → geen escalatie
        Assert.Equal(1, tracker.InFlight(UserId));
    }

    [Fact]
    public void TryReserve_NaVrijgave_PermitWeerBeschikbaar()
    {
        var tracker = new AgenticInFlightTracker();
        var first = tracker.TryReserve(UserId, dbCountToday: 4, dailyQuota: 5);
        Assert.NotNull(first);

        // De eerste run rondt af (metric geschreven) → permit vrij.
        first!.Dispose();
        Assert.Equal(0, tracker.InFlight(UserId));

        // Nu is er weer ruimte voor de volgende (db-teller blijft 4 in dit
        // venster; de vrijgegeven reservering telt niet meer mee).
        var next = tracker.TryReserve(UserId, dbCountToday: 4, dailyQuota: 5);
        Assert.NotNull(next);
    }

    [Fact]
    public void TryReserve_DubbeleDispose_TeltMaarEenKeerAf()
    {
        var tracker = new AgenticInFlightTracker();
        var a = tracker.TryReserve(UserId, dbCountToday: 0, dailyQuota: 5);
        var b = tracker.TryReserve(UserId, dbCountToday: 0, dailyQuota: 5);
        Assert.Equal(2, tracker.InFlight(UserId));

        a!.Dispose();
        a.Dispose(); // idempotent — mag de andere reservering niet wegtellen
        Assert.Equal(1, tracker.InFlight(UserId));
        b!.Dispose();
        Assert.Equal(0, tracker.InFlight(UserId));
    }

    [Fact]
    public void TryReserve_QuotaNul_GeeftNooitEenPermit()
    {
        var tracker = new AgenticInFlightTracker();
        Assert.Null(tracker.TryReserve(UserId, dbCountToday: 0, dailyQuota: 0));
    }
}
