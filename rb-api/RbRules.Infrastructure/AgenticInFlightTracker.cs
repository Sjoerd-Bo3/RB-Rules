namespace RbRules.Infrastructure;

/// <summary>In-proces teller van nu-lopende, door-de-gebruiker-geforceerde
/// agentic-runs per account (#153, TOCTOU-fix). De persistente teller
/// (<see cref="UserAccountService.UsageTodayAsync"/>) is een kale COUNT die
/// pas ná de dure agent-run een rij ziet; vijf gelijktijdige Grondig-requests
/// zien daardoor allemaal hetzelfde getal en zouden allemaal escaleren — het
/// quotum is dan met een paar tabbladen te omzeilen. Deze reservering vult dat
/// venster: de quota-check wordt (db-teller + nu-lopende reserveringen) onder
/// één korte lock, en een geslaagde reservering blijft staan tot de run (en
/// zijn metric-write) klaar is.
///
/// Single-instance-aanname: er draait één rb-api-container op de VM, dus een
/// in-proces reservering volstaat en is pragmatisch. Multi-instance zou een
/// gedeelde (DB-)reservering vergen — YAGNI nu.</summary>
public sealed class AgenticInFlightTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, int> _inFlight = [];

    /// <summary>Probeert onder de lock een permit te reserveren: slaagt alleen
    /// als <paramref name="dbCountToday"/> plus de al-lopende reserveringen voor
    /// deze gebruiker nog onder <paramref name="dailyQuota"/> blijven. De lock
    /// omvat bewust alléén de check + increment — nooit de agent-call zelf.
    /// Geeft een <see cref="IDisposable"/> terug die bij Dispose de permit weer
    /// vrijgeeft; null = geen tegoed meer.</summary>
    public IDisposable? TryReserve(long userId, int dbCountToday, int dailyQuota)
    {
        lock (_gate)
        {
            var current = _inFlight.GetValueOrDefault(userId);
            if (dbCountToday + current >= dailyQuota) return null;
            _inFlight[userId] = current + 1;
        }
        return new Reservation(this, userId);
    }

    /// <summary>Nu-lopende reserveringen voor een gebruiker — alleen voor tests
    /// en diagnose.</summary>
    public int InFlight(long userId)
    {
        lock (_gate) return _inFlight.GetValueOrDefault(userId);
    }

    private void Release(long userId)
    {
        lock (_gate)
        {
            var current = _inFlight.GetValueOrDefault(userId);
            if (current <= 1) _inFlight.Remove(userId);
            else _inFlight[userId] = current - 1;
        }
    }

    private sealed class Reservation(AgenticInFlightTracker owner, long userId) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner.Release(userId);
        }
    }
}
