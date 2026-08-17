using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Fase live-graph (#227, §3.5) — de IO-schil <see cref="BreinProjectionService"/>
/// rond de pure <see cref="BrainProjection"/>. Deze tests borgen het
/// graceful-degradation-contract: Neo4j-uitval is een VERWACHT pad — de
/// <c>breinprojectie</c>-job crasht dan niet, maar meldt <c>GraphAvailable=false</c>
/// met de "graph niet beschikbaar"-samenvatting (afgeleide state is herberekenbaar
/// uit Postgres). Zonder live Neo4j: een <see cref="IDriver"/> die bij
/// <c>AsyncSession()</c> gooit, exact het transiente-driver-scenario.</summary>
public class BreinProjectionServiceTests
{
    [Fact]
    public async Task ProjectAsync_Neo4jUitval_DegradeertNettoZonderCrash()
    {
        using var db = NewDb();
        db.MiningRuns.Add(new MiningRun { Id = "r1", Kind = "mechanic" });
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = "mechanic", CanonicalLabel = "Deflect",
            Status = CanonicalEntityStatus.Canonical, CreatedByRunId = "r1",
        });
        await db.SaveChangesAsync();

        // Transiente driver-/connectie-fout: de "poort" moet dit als verwacht pad
        // opvangen, niet doorgooien.
        var svc = new BreinProjectionService(db, new ThrowingDriver(
            () => new ServiceUnavailableException("kan Neo4j niet bereiken")));

        var result = await svc.ProjectAsync();

        Assert.False(result.GraphAvailable);
        Assert.Contains("graph niet beschikbaar", result.Summary);
        // De rij-opbouw (spiegel van Postgres) draait vóór de write en blijft
        // gevuld — de degradatie verliest de tel-provenance niet.
        Assert.Equal(1, result.CanonicalEntities);
    }

    [Fact]
    public async Task ProjectAsync_LegeBrein_Neo4jUitval_DegradeertNog()
    {
        // Ook zonder data blijft de poort een nette degradatie geven (geen crash,
        // GraphAvailable=false) i.p.v. de happy-path-samenvatting.
        using var db = NewDb();
        var svc = new BreinProjectionService(db, new ThrowingDriver(
            () => new ServiceUnavailableException("down")));

        var result = await svc.ProjectAsync();

        Assert.False(result.GraphAvailable);
        Assert.Contains("graph niet beschikbaar", result.Summary);
    }

    [Fact]
    public async Task ProjectAsync_Annulering_WordtNietGeslikt()
    {
        // OperationCanceledException is GEEN degradatie-pad: het catch-filter laat
        // annulering bewust doorgooien (borgt dat een latere verbreding van de catch
        // annulering niet stilletjes als "graph niet beschikbaar" maskeert).
        using var db = NewDb();
        var svc = new BreinProjectionService(db, new ThrowingDriver(
            () => new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => svc.ProjectAsync());
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op als tekst
    /// (zelfde patroon als de overige service-tests).</summary>
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

    /// <summary>Minimale <see cref="IDriver"/> die bij het openen van een sessie de
    /// meegegeven fout gooit — genoeg om het Neo4j-uitval-pad te raken zonder een
    /// live database. Alle overige leden zijn ongebruikt.</summary>
    private sealed class ThrowingDriver(Func<Exception> onSession) : IDriver
    {
        public IAsyncSession AsyncSession() => throw onSession();
        public IAsyncSession AsyncSession(Action<SessionConfigBuilder> action) => throw onSession();

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
}
