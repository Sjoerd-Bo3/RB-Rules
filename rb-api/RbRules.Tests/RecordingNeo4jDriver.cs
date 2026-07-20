using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Een <see cref="IDriver"/> die élke Cypher-query opneemt in plaats van
/// hem uit te voeren. Zo toetsen tests het UITVOERENDE pad — wat de service ECHT
/// naar Neo4j stuurt — in plaats van een parallel opgebouwde string of een
/// hulp-property die de service zelf niet hoeft te gebruiken.
///
/// Met de hand geschreven, alleen de leden die dit pad raakt (zelfde lijn als de
/// ThrowingDriver in BreinProjectionServiceTests). De projecties consumeren geen
/// enkele result-cursor (het zijn pure write-projecties), dus een lege stub volstaat.
///
/// Sinds #289 gedeeld tussen <see cref="OntologyProjectionAlignmentTests"/> (de
/// naam-uitlijning per facet) en <see cref="ProjectionOntologyGuardTests"/> (de
/// projectie↔ontologie-guard over het hele corpus).</summary>
internal sealed class RecordingDriver : IDriver
{
    private readonly List<string> _queries = [];

    /// <summary>Elke opgenomen query-tekst, in uitvoeringsvolgorde. De guards
    /// vergelijken bewust als VERZAMELING — volgorde is geen contract.</summary>
    public IReadOnlyList<string> Queries => _queries;

    public IAsyncSession AsyncSession() => new RecordingSession(_queries);
    public IAsyncSession AsyncSession(Action<SessionConfigBuilder> action) => AsyncSession();

    public Config Config => throw new NotSupportedException();
    public bool Encrypted => throw new NotSupportedException();
    public Task<IServerInfo> GetServerInfoAsync() => throw new NotSupportedException();
    public Task<bool> TryVerifyConnectivityAsync() => throw new NotSupportedException();
    public Task VerifyConnectivityAsync() => throw new NotSupportedException();
    public Task<bool> SupportsMultiDbAsync() => throw new NotSupportedException();
    public Task<bool> SupportsSessionAuthAsync() => throw new NotSupportedException();
    public IExecutableQuery<IRecord, IRecord> ExecutableQuery(string cypher) =>
        throw new NotSupportedException();
    public Task<bool> VerifyAuthenticationAsync(IAuthToken authToken) =>
        throw new NotSupportedException();
    public IBookmarkManager GetExecutableQueryBookmarkManager() =>
        throw new NotSupportedException();

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingSession(List<string> queries) : IAsyncSession
{
    public Task<IAsyncTransaction> BeginTransactionAsync() =>
        Task.FromResult<IAsyncTransaction>(new RecordingTransaction(queries));
    public Task<IAsyncTransaction> BeginTransactionAsync(Action<TransactionConfigBuilder> action) =>
        BeginTransactionAsync();
    public Task<IAsyncTransaction> BeginTransactionAsync(AccessMode mode) =>
        BeginTransactionAsync();
    public Task<IAsyncTransaction> BeginTransactionAsync(AccessMode mode, Action<TransactionConfigBuilder> action) =>
        BeginTransactionAsync();

    public Task<IResultCursor> RunAsync(string query) => Record(query);
    public Task<IResultCursor> RunAsync(string query, object parameters) => Record(query);
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        Record(query);
    public Task<IResultCursor> RunAsync(Query query) => Record(query.Text);
    public Task<IResultCursor> RunAsync(string query, Action<TransactionConfigBuilder> action) =>
        Record(query);
    public Task<IResultCursor> RunAsync(
        string query, IDictionary<string, object> parameters, Action<TransactionConfigBuilder> action) =>
        Record(query);
    public Task<IResultCursor> RunAsync(Query query, Action<TransactionConfigBuilder> action) =>
        Record(query.Text);

    private Task<IResultCursor> Record(string query)
    {
        queries.Add(query);
        return Task.FromResult<IResultCursor>(new EmptyCursor());
    }

    public Bookmarks LastBookmarks => throw new NotSupportedException();
    public SessionConfig SessionConfig => throw new NotSupportedException();

    public Task<T> ReadTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work) => work(new RecordingTransaction(queries));
    public Task<T> ReadTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task ReadTransactionAsync(Func<IAsyncTransaction, Task> work) => work(new RecordingTransaction(queries));
    public Task ReadTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task<T> WriteTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work) => work(new RecordingTransaction(queries));
    public Task<T> WriteTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task WriteTransactionAsync(Func<IAsyncTransaction, Task> work) => work(new RecordingTransaction(queries));
    public Task WriteTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task<T> ExecuteReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work) => work(new RecordingTransaction(queries));
    public Task<T> ExecuteReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task ExecuteReadAsync(Func<IAsyncQueryRunner, Task> work) => work(new RecordingTransaction(queries));
    public Task ExecuteReadAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task<T> ExecuteWriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work) => work(new RecordingTransaction(queries));
    public Task<T> ExecuteWriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));
    public Task ExecuteWriteAsync(Func<IAsyncQueryRunner, Task> work) => work(new RecordingTransaction(queries));
    public Task ExecuteWriteAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries));

    public Task CloseAsync() => Task.CompletedTask;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingTransaction(List<string> queries) : IAsyncTransaction
{
    public Task<IResultCursor> RunAsync(string query) => Record(query);
    public Task<IResultCursor> RunAsync(string query, object parameters) => Record(query);
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        Record(query);
    public Task<IResultCursor> RunAsync(Query query) => Record(query.Text);

    private Task<IResultCursor> Record(string query)
    {
        queries.Add(query);
        return Task.FromResult<IResultCursor>(new EmptyCursor());
    }

    public TransactionConfig TransactionConfig => throw new NotSupportedException();
    public Task CommitAsync() => Task.CompletedTask;
    public Task RollbackAsync() => Task.CompletedTask;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Lege result-cursor: de graph-projecties lezen nooit een resultaat terug.</summary>
internal sealed class EmptyCursor : IResultCursor
{
    public Task<string[]> KeysAsync() => Task.FromResult(Array.Empty<string>());
    public Task<IResultSummary> ConsumeAsync() => throw new NotSupportedException();
    public Task<IRecord> PeekAsync() => Task.FromResult<IRecord>(null!);
    public Task<bool> FetchAsync() => Task.FromResult(false);
    public IRecord Current => throw new InvalidOperationException("geen rijen");
    public bool IsOpen => false;

    public async IAsyncEnumerator<IRecord> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>Een schone in-memory <see cref="RbRulesDbContext"/> voor de
/// graph-projectietests. InMemory kent het pgvector-type niet, dus vectors gaan als
/// tekst (zelfde patroon als de overige service-tests).</summary>
internal static class TestGraphDb
{
    public static RbRulesDbContext New() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Pgvector.Vector, string>(
                            v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }
}
