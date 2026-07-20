using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;
using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Domain.Reasoning;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Regressie bij #274: schema en graph-projectie mogen niet meer stil uit
/// elkaar groeien. <see cref="OntologySchema"/> is volgens #227 de ÉNE schema-bron,
/// maar noemde de kaart→mechaniek-relatie HAS_KEYWORD terwijl
/// <see cref="GraphSyncService"/> HAS_MECHANIC projecteerde (en de kaart→domein-
/// relatie IN_DOMAIN terwijl de projectie HAS_DOMAIN schreef). Gevolgen: het schema
/// valideerde niet wat er écht stond, een weiger-reden noemde een relatietype dat
/// niet bestaat, en de Neo4j-native reasoner genereerde Cypher over edges én
/// knooplabels die niemand ooit schrijft.
///
/// Deze tests pinnen vier lagen op elkaar vast: de canonieke naam in het schema, de
/// Cypher die <see cref="GraphSyncService.SyncAsync"/> ECHT naar de driver stuurt
/// (via een opnemende <c>IDriver</c> — géén parallel opgebouwde string, zie
/// <see cref="CapturedCypherAsync"/>), de whitelist waarmee de brein-API de graph
/// bevraagt, en de gegenereerde property-chain-Cypher. Ze falen op de eerste die
/// wegloopt; geverifieerd met een mutatie die de aanroepplek in SyncAsync terugzet
/// op de oude literals (7 rode tests).
///
/// LET OP wat dit NIET bewijst: dat de reasoner nu iets afleidt. Hop 2 van de keten
/// (<c>(:Mechanic)-[:GOVERNED_BY]->(:RuleSection)</c>) wordt nergens geprojecteerd —
/// GOVERNED_BY komt uitsluitend van een <c>:Interaction</c> — dus de property-chain
/// materialiseert nog steeds nul edges. Deze tests bewaken de NAAM-uitlijning, niet
/// de levendheid van de inferentie (ARCHITECTURE §6.4).</summary>
public class OntologyProjectionAlignmentTests
{
    /// <summary>De kaart-facetten die de projectie deterministisch schrijft, met hun
    /// vastgepinde canonieke edge-naam en knooplabel. De literals hier zijn het anker:
    /// de projectie-clausules worden uit het schema gegenereerd, dus alleen een
    /// letterlijke verwachting betrapt een hernoeming die beide kanten meeneemt.</summary>
    public static TheoryData<RelationType, string, string, string> Facets() => new()
    {
        { RelationType.HasMechanic, "HAS_MECHANIC", nameof(EntityType.Mechanic), "m" },
        { RelationType.HasDomain, "HAS_DOMAIN", nameof(EntityType.Domain), "d" },
    };

    /// <summary>Draait <see cref="GraphSyncService.SyncAsync"/> met één kaart die een
    /// domein én een mechaniek draagt, tegen een <see cref="RecordingDriver"/>, en geeft
    /// élke Cypher-query terug die de service naar Neo4j stuurde. Zo toetsen de tests
    /// het UITVOERENDE pad in plaats van een hulp-property die de service zelf niet
    /// hoeft te gebruiken.</summary>
    private static async Task<IReadOnlyList<string>> CapturedCypherAsync()
    {
        await using var db = NewDb();
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001", Name = "Shieldbearer", Type = "Unit",
            Domains = ["Fury"], Mechanics = ["Tank"],
        });
        await db.SaveChangesAsync();

        var driver = new RecordingDriver();
        await new GraphSyncService(db, driver).SyncAsync();
        return driver.Queries;
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
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
                        .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                            .ValueConverter<Pgvector.Vector, string>(
                            v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }

    // ── Opnemende Neo4j-driver ────────────────────────────────────────────────
    // Zelfde lijn als de ThrowingDriver in BreinProjectionServiceTests: met de hand
    // geschreven, alleen de leden die dit pad raakt. GraphSyncService consumeert geen
    // enkele result-cursor (het is een pure write-projectie), dus een lege stub volstaat.

    private sealed class RecordingDriver : IDriver
    {
        private readonly List<string> _queries = [];
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

    private sealed class RecordingSession(List<string> queries) : IAsyncSession
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

    private sealed class RecordingTransaction(List<string> queries) : IAsyncTransaction
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

    /// <summary>Lege result-cursor: GraphSyncService leest nooit een resultaat terug.</summary>
    private sealed class EmptyCursor : IResultCursor
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

    [Theory]
    [MemberData(nameof(Facets))]
    public void Schema_DraagtDeCanoniekeEdgeNaamEnRange(
        RelationType type, string edgeName, string nodeLabel, string _)
    {
        var relation = OntologySchema.Relations[type];

        Assert.Equal(edgeName, relation.EdgeName);
        Assert.Equal(nodeLabel, Assert.Single(relation.Range).ToString());
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public async Task Projectie_SchrijftPreciesDeSchemaEdgeEnHetSchemaLabel(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // CRUCIAAL: dit toetst de Cypher die SyncAsync ECHT naar de driver stuurt,
        // niet een parallel opgebouwde string. Een eerdere versie van deze test
        // asserteerde op GraphSyncService.MechanicMergeClause; die property wordt
        // dode code zodra iemand op de aanroepplek weer een literal zet, en dan
        // bewees de test alleen nog dat het schema met zichzelf klopt. Door de
        // service met een opnemende driver te draaien valt de test wél om als de
        // aanroepplek de ontologie omzeilt.
        var executed = await CapturedCypherAsync();

        var merge = Assert.Single(executed, q => q.Contains($":{nodeLabel} {{name: p.value}}"));
        Assert.Contains($"MERGE ({alias}:{nodeLabel} {{name: p.value}})", merge);
        Assert.Contains($"MERGE (c)-[:{edgeName}]->({alias})", merge);

        // En de naam die er echt uitgaat is de naam uit het register.
        Assert.Contains($"-[:{OntologySchema.Relations[type].EdgeName}]->", merge);
    }

    [Theory]
    [InlineData("HAS_KEYWORD")]
    [InlineData("IN_DOMAIN")]
    [InlineData(":Keyword {name:")]
    public async Task Projectie_SchrijftDeOudeNamenNergensMeer(string retired)
    {
        var executed = await CapturedCypherAsync();
        Assert.DoesNotContain(executed, q => q.Contains(retired, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public async Task BreinApi_KentDeEdgeDieDeProjectieSchrijft(
        RelationType type, string edgeName, string nodeLabel, string alias)
    {
        // BrainQuery.EdgeTypes is de whitelist waarmee /brain de graph filtert:
        // een edge die de projectie schrijft maar die daar niet in staat, is voor de
        // brein-API onzichtbaar. Ook hier het uitvoerende pad als bron.
        Assert.Contains(OntologySchema.Relations[type].EdgeName, BrainQuery.EdgeTypes);
        Assert.Contains(edgeName, BrainQuery.EdgeTypes);

        var executed = await CapturedCypherAsync();
        Assert.Contains(executed, q => q.Contains($"({alias}:{nodeLabel} ")
                                       && q.Contains($"-[:{edgeName}]->"));
    }

    [Theory]
    [MemberData(nameof(Facets))]
    public void Reasoner_KetentOverDeGeprojecteerdeEdgeEnHetGeprojecteerdeLabel(
        RelationType type, string edgeName, string nodeLabel, string _)
    {
        // De property-chain-regel die bij dit facet begint moet exact de geprojecteerde
        // hop matchen. Vóór #274 stond hier HAS_KEYWORD->(:Keyword) resp.
        // IN_DOMAIN->(:Domain): een MATCH die op de live graaf nul rijen oplevert.
        var rules = InferenceRuleRegistry.PropertyChainRules()
            .Where(r => r.Cypher.Contains($"[:{edgeName}]"))
            .ToList();

        Assert.NotEmpty(rules);
        foreach (var rule in rules)
            Assert.Contains($"MATCH (c:Card)-[:{edgeName}]->(:{nodeLabel})", rule.Cypher);

        Assert.Equal(OntologySchema.Relations[type].EdgeName, edgeName);
    }

    [Fact]
    public void Reasoner_GebruiktGeenEdgeNaamDieBuitenDeOntologieValt()
    {
        // Elke edge-naam in een gegenereerde regel moet een geregistreerde relatie
        // zijn. Vangt de omgekeerde drift: een handgeschreven Cypher-hop die nergens
        // in het schema staat en dus door niets gevalideerd wordt.
        foreach (var rule in InferenceRuleRegistry.All)
            foreach (Match m in Regex.Matches(rule.Cypher, @"\[[a-z]*:([A-Z_]+)"))
            {
                var edge = m.Groups[1].Value;
                Assert.True(OntologySchema.RelationByEdgeName(edge) is not null,
                    $"regel '{rule.Id}' gebruikt edge '{edge}' die niet in de ontologie staat");
            }
    }

    [Theory]
    [InlineData("HAS_KEYWORD")]
    [InlineData("IN_DOMAIN")]
    public void OudeNaam_BestaatNietMeerInHetSchema(string retiredEdgeName)
    {
        // De hernoemde namen mogen niet als tweede ingang terugsluipen: één relatie,
        // één naam. RelationByEdgeName is hoofdletterongevoelig, dus dit dekt ook
        // een 'has_keyword'-variant.
        Assert.Null(OntologySchema.RelationByEdgeName(retiredEdgeName));
    }
}
