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
internal sealed record RecordedStatement(string Cypher, IReadOnlyDictionary<string, object>? Parameters)
{
    /// <summary>De lijst-parameter (<c>$rows</c>/<c>$pairs</c>/<c>$ids</c>/<c>$refs</c>)
    /// waarmee dit statement gevoed werd, of <c>null</c> als het er geen heeft. De
    /// guard gebruikt dit om te toetsen dat de "gevulde" fixture élk
    /// edge-schrijvend statement écht rijen geeft (#289-review, F6) — zonder die
    /// controle erodeert de fixture stil en verliest de rij-onafhankelijkheidstest
    /// zijn kracht zonder dat iets rood wordt.</summary>
    public System.Collections.ICollection? RowList => Parameters?.Values
        .OfType<System.Collections.ICollection>()
        .FirstOrDefault();
}

internal sealed class RecordingDriver(Func<string, long?>? resultCount = null) : IDriver
{
    private readonly List<RecordedStatement> _statements = [];

    /// <summary>Elk opgenomen statement, in uitvoeringsvolgorde. De guards
    /// vergelijken bewust als VERZAMELING — volgorde is geen contract.</summary>
    public IReadOnlyList<RecordedStatement> Statements => _statements;

    /// <summary>Alleen de query-teksten.</summary>
    public IReadOnlyList<string> Queries => [.. _statements.Select(s => s.Cypher)];

    public IAsyncSession AsyncSession() => new RecordingSession(_statements, resultCount);
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

internal sealed class RecordingSession(
    List<RecordedStatement> queries, Func<string, long?>? resultCount = null) : IAsyncSession
{
    public Task<IAsyncTransaction> BeginTransactionAsync() =>
        Task.FromResult<IAsyncTransaction>(new RecordingTransaction(queries, resultCount));
    public Task<IAsyncTransaction> BeginTransactionAsync(Action<TransactionConfigBuilder> action) =>
        BeginTransactionAsync();
    public Task<IAsyncTransaction> BeginTransactionAsync(AccessMode mode) =>
        BeginTransactionAsync();
    public Task<IAsyncTransaction> BeginTransactionAsync(AccessMode mode, Action<TransactionConfigBuilder> action) =>
        BeginTransactionAsync();

    public Task<IResultCursor> RunAsync(string query) => Record(query, null);
    public Task<IResultCursor> RunAsync(string query, object parameters) => Record(query, null);
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        Record(query, parameters);
    public Task<IResultCursor> RunAsync(Query query) => Record(query.Text, null);
    public Task<IResultCursor> RunAsync(string query, Action<TransactionConfigBuilder> action) =>
        Record(query, null);
    public Task<IResultCursor> RunAsync(
        string query, IDictionary<string, object> parameters, Action<TransactionConfigBuilder> action) =>
        Record(query, parameters);
    public Task<IResultCursor> RunAsync(Query query, Action<TransactionConfigBuilder> action) =>
        Record(query.Text, null);

    private Task<IResultCursor> Record(string query, IDictionary<string, object>? parameters)
    {
        queries.Add(new RecordedStatement(
            query, parameters is null ? null : new Dictionary<string, object>(parameters)));
        return Task.FromResult<IResultCursor>(new EmptyCursor());
    }

    public Bookmarks LastBookmarks => throw new NotSupportedException();
    public SessionConfig SessionConfig => throw new NotSupportedException();

    public Task<T> ReadTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> ReadTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task ReadTransactionAsync(Func<IAsyncTransaction, Task> work) => work(new RecordingTransaction(queries, resultCount));
    public Task ReadTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> WriteTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> WriteTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task WriteTransactionAsync(Func<IAsyncTransaction, Task> work) => work(new RecordingTransaction(queries, resultCount));
    public Task WriteTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> ExecuteReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> ExecuteReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task ExecuteReadAsync(Func<IAsyncQueryRunner, Task> work) => work(new RecordingTransaction(queries, resultCount));
    public Task ExecuteReadAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> ExecuteWriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work) => work(new RecordingTransaction(queries, resultCount));
    public Task<T> ExecuteWriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));
    public Task ExecuteWriteAsync(Func<IAsyncQueryRunner, Task> work) => work(new RecordingTransaction(queries, resultCount));
    public Task ExecuteWriteAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder> action) => work(new RecordingTransaction(queries, resultCount));

    public Task CloseAsync() => Task.CompletedTask;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingTransaction(
    List<RecordedStatement> queries, Func<string, long?>? resultCount = null) : IAsyncTransaction
{
    public Task<IResultCursor> RunAsync(string query) => Record(query, null);
    public Task<IResultCursor> RunAsync(string query, object parameters) => Record(query, null);
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        Record(query, parameters);
    public Task<IResultCursor> RunAsync(Query query) => Record(query.Text, null);

    private Task<IResultCursor> Record(string query, IDictionary<string, object>? parameters)
    {
        queries.Add(new RecordedStatement(
            query, parameters is null ? null : new Dictionary<string, object>(parameters)));
        // Sinds #321 lezen de RELATES_TO-statements een count(r)-rij terug; een
        // test die dat leespad wil voeden geeft de driver een resultCount-functie
        // mee (query-tekst → telling). Zonder functie (of bij null) blijft het
        // gedrag van vóór #321: een lege cursor, alsof er niets te lezen valt.
        return Task.FromResult<IResultCursor>(
            resultCount?.Invoke(query) is { } written
                ? new SingleCountCursor(written)
                : new EmptyCursor());
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

/// <summary>Cursor met precies één <c>{written: n}</c>-rij — de vorm die een
/// echt <c>RETURN count(r) AS written</c> oplevert (#321).</summary>
internal sealed class SingleCountCursor(long written) : IResultCursor
{
    private bool _fetched;

    public Task<string[]> KeysAsync() => Task.FromResult(new[] { "written" });
    public Task<IResultSummary> ConsumeAsync() => throw new NotSupportedException();
    public Task<IRecord> PeekAsync() =>
        Task.FromResult<IRecord>(_fetched ? null! : new CountRecord(written));

    public Task<bool> FetchAsync()
    {
        if (_fetched) return Task.FromResult(false);
        _fetched = true;
        return Task.FromResult(true);
    }

    public IRecord Current => _fetched
        ? new CountRecord(written)
        : throw new InvalidOperationException("eerst FetchAsync");

    public bool IsOpen => !_fetched;

    public async IAsyncEnumerator<IRecord> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (_fetched) yield break;
        _fetched = true;
        yield return new CountRecord(written);
    }
}

/// <summary>Eén record met alleen <c>written</c> (long, zoals Neo4j een count
/// teruggeeft).</summary>
internal sealed class CountRecord(long written) : IRecord
{
    private readonly Dictionary<string, object> _values = new() { ["written"] = written };

    public object this[int index] => index == 0
        ? written
        : throw new ArgumentOutOfRangeException(nameof(index));

    public object this[string key] => _values[key];
    IReadOnlyDictionary<string, object> IRecord.Values => _values;
    IReadOnlyList<string> IRecord.Keys => ["written"];
    public T Get<T>(string key) => (T)Convert.ChangeType(_values[key], typeof(T));

    public bool TryGet<T>(string key, out T value)
    {
        if (_values.ContainsKey(key)) { value = Get<T>(key); return true; }
        value = default!;
        return false;
    }

    public T GetCaseInsensitive<T>(string key) => Get<T>(key.ToLowerInvariant());
    public bool TryGetCaseInsensitive<T>(string key, out T value) =>
        TryGet(key.ToLowerInvariant(), out value);

    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value!);
    IEnumerable<string> IReadOnlyDictionary<string, object>.Keys => _values.Keys;
    IEnumerable<object> IReadOnlyDictionary<string, object>.Values => _values.Values;
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
