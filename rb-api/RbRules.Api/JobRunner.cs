using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api;

/// <summary>Achtergrond-jobs voor admin-acties: POST start direct (202), de
/// status-endpoint toont live wat er draait — inclusief een voortgangsregel
/// die de job zelf bijwerkt. Eén job tegelijk — de acties delen dezelfde
/// data en LLM/Ollama-capaciteit.</summary>
public class JobRunner(IServiceScopeFactory scopeFactory, ILogger<JobRunner> logger)
{
    public record JobState(
        string Name, string Status, DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt, string? Detail, string? Progress = null);

    private readonly Lock _lock = new();
    private JobState? _current;
    private JobState? _last;

    public (JobState? Running, JobState? Last) Snapshot()
    {
        lock (_lock) return (_current, _last);
    }

    public bool TryStart(
        string name,
        Func<IServiceProvider, Action<string>, CancellationToken, Task<string>> work)
    {
        lock (_lock)
        {
            if (_current is not null) return false;
            _current = new(name, "running", DateTimeOffset.UtcNow, null, null);
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
                detail = await work(scope.ServiceProvider, Report, CancellationToken.None);
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
            }
        });
        return true;
    }
}
