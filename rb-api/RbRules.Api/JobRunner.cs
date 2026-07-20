using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>Achtergrond-jobs voor admin-acties: POST start direct (202), de
/// status-endpoint toont live wat er draait — inclusief een voortgangsregel
/// die de job zelf bijwerkt. Eén job tegelijk — de acties delen dezelfde
/// data en LLM/Ollama-capaciteit.
///
/// Afbreken (#253): elke run krijgt een eigen <see cref="CancellationTokenSource"/>
/// en dié token gaat het werk in (niet meer <c>CancellationToken.None</c>), zodat
/// beheer een vastgelopen of te lang doorlopende run coöperatief kan stoppen —
/// de services geven de token al door aan EF/HTTP. De afbreking landt als een
/// gewone afronding met status "cancelled" in run_log: cruciaal, want de
/// scheduler leest dat grootboek (<see cref="JobLedger.LastRunAsync"/>,
/// status-agnostisch) om te bepalen of een venster al gevuld is. Zonder die
/// regel denkt hij dat de job vandaag nog niet draaide en herstart hij hem
/// meteen — precies wat er misging toen afbreken alleen kon via
/// <c>docker restart</c>.</summary>
public class JobRunner(IServiceScopeFactory scopeFactory, ILogger<JobRunner> logger)
{
    public record JobState(
        string Name, string Status, DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt, string? Detail, string? Progress = null,
        bool CancelRequested = false);

    private readonly Lock _lock = new();
    private JobState? _current;
    private JobState? _last;
    private CancellationTokenSource? _cts;

    public (JobState? Running, JobState? Last) Snapshot()
    {
        lock (_lock) return (_current, _last);
    }

    public bool TryStart(
        string name,
        Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> work)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_current is not null) return false;
            _current = new(name, "running", DateTimeOffset.UtcNow, null, null);
            _cts = cts = new CancellationTokenSource();
        }

        void Report(string progress)
        {
            lock (_lock)
            {
                if (_current?.Name == name) _current = _current with { Progress = progress };
            }
        }

        _ = Task.Run(async () =>
        {
            var status = "ok";
            string detail;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var outcome = await work(scope.ServiceProvider, Report, cts.Token);
                detail = outcome.Detail;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Afgebroken door beheer (#253) — geen fout, maar wél een
                // afronding: de bereikte voortgang gaat mee zodat zichtbaar
                // is hoe ver de run kwam (rode draad: geen onzichtbare state).
                status = "cancelled";
                detail = CancelDetail(name);
                logger.LogInformation("Job {Name} afgebroken door beheer: {Detail}", name, detail);
            }
            catch (Exception ex)
            {
                status = "error";
                detail = ex.Message;
                logger.LogError(ex, "Job {Name} faalde", name);
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RbRulesDbContext>();
                db.RunLogs.Add(new RunLog { Kind = "job", Ref = name, Status = status, Detail = detail });
                // Bewust zonder token: deze afrondingsregel is juist op het
                // cancel-pad onmisbaar (zie klassecommentaar).
                await db.SaveChangesAsync();
            }
            catch
            {
                // logging mag een job-afronding nooit blokkeren
            }

            lock (_lock)
            {
                _last = _current! with
                {
                    Status = status, FinishedAt = DateTimeOffset.UtcNow,
                    Detail = detail, Progress = null,
                };
                _current = null;
                _cts = null;
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>Breekt de lopende run coöperatief af (#253). Geeft de
    /// (bijgewerkte) staat van de afgebroken run terug, of null als er niets
    /// draait — de endpoint maakt daar een net antwoord van, geen fout.
    /// Idempotent: een tweede aanroep tijdens dezelfde run cancelt opnieuw
    /// (no-op op een al-gecancelde source) en geeft dezelfde staat terug.
    ///
    /// Annuleren gebeurt ónder dezelfde lock als het opruimen aan het eind
    /// van de run: zo kan Cancel nooit een net-gedisposede source raken.</summary>
    public JobState? TryCancel()
    {
        lock (_lock)
        {
            if (_current is null || _cts is null) return null;
            _current = _current with { CancelRequested = true };
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException ex)
            {
                // Een cancel-callback (EF/HTTP) die zelf struikelt mag de
                // afbreking niet als 500 terugkaatsen: de token staat op dat
                // moment al op cancelled, dus de run stopt hoe dan ook.
                logger.LogWarning(ex, "Cancel-callback faalde bij afbreken van {Name}", _current.Name);
            }
            return _current;
        }
    }

    /// <summary>Detailregel voor run_log bij een afbreking: hoe lang de run
    /// draaide en hoe ver hij kwam (laatste voortgangsregel).</summary>
    private string CancelDetail(string name)
    {
        JobState? state;
        lock (_lock) state = _current?.Name == name ? _current : null;
        var elapsed = state is null
            ? ""
            : $" na {(DateTimeOffset.UtcNow - state.StartedAt).TotalSeconds:F0}s";
        var progress = string.IsNullOrWhiteSpace(state?.Progress)
            ? "geen voortgang gerapporteerd"
            : state!.Progress;
        return $"afgebroken via beheer{elapsed} — laatste voortgang: {progress}";
    }
}
