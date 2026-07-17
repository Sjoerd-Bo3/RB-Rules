using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Sectie-dossier en changeconsolidatie (#206, review-fix
/// finding 8): de changes-historie van een sectie toont alleen primaire
/// changes — de graph projecteert bewust álle changes (volledige brontrail,
/// ARCHITECTURE §6.3), dus zonder filter zou een geconsolideerd paar hier
/// alsnog dubbel verschijnen. De AFFECTS-buren komen via een gestubde
/// <see cref="BrainGraphService"/> (NeighborsAsync is virtual als
/// test-seam); de database is EF InMemory. Geen claims geseed — de
/// ILike-voorselectie in DossierAsync is niet InMemory-vertaalbaar maar
/// wordt bij nul rijen nooit geëvalueerd (zelfde beperking als
/// RelationTriageServiceTests).</summary>
public class RuleBrowserDossierTests
{
    [Fact]
    public async Task DossierAsync_GeconsolideerdeSecundaire_VerschijntNietInDeChangesHistorie()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-pdf", Name = "Core Rules", Url = "https://example.test/rules",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "daily",
        });
        db.Sources.Add(new Source
        {
            Id = "mobalytics", Name = "Mobalytics", Url = "https://example.test/moba",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "daily",
        });
        db.Documents.Add(new Document
        {
            Id = 1, SourceId = "core-rules-pdf", Content = "regels", ContentHash = "h1",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = "core-rules-pdf", SectionCode = "7.4",
            ChunkIndex = 0, Text = "Deflect reduces combat damage.",
        });
        var primary = new Change
        {
            SourceId = "core-rules-pdf", ChangeType = "core-rule", Summary = "§7.4 changed.",
        };
        db.Changes.Add(primary);
        await db.SaveChangesAsync();
        var secondary = new Change
        {
            SourceId = "mobalytics", ChangeType = "core-rule", Summary = "Community: §7.4 changed.",
            ConsolidatedWithId = primary.Id,
        };
        db.Changes.Add(secondary);
        await db.SaveChangesAsync();

        // De graph wijst BEIDE changes aan (volledige brontrail) — precies
        // het scenario waarin het dossier zonder filter dubbel zou tonen.
        var svc = new RuleBrowserService(
            db, new StubGraph(primary.Id, secondary.Id),
            NullLogger<RuleBrowserService>.Instance);

        var dossier = await svc.DossierAsync("7.4", source: null);

        Assert.NotNull(dossier);
        Assert.False(dossier.GraphDegraded);
        var change = Assert.Single(dossier.Changes);
        Assert.Equal(primary.Id, change.Id);
    }

    /// <summary>Stubt de AFFECTS-buren van de sectie: beide change-refs,
    /// zoals de echte graph ze na een rebuild zou teruggeven.</summary>
    private sealed class StubGraph(params long[] changeIds) : BrainGraphService(null!)
    {
        public override Task<IReadOnlyList<BrainNeighbor>?> NeighborsAsync(
            string label, string refValue, string[] edgeFilter, string kind,
            BrainDirection direction, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrainNeighbor>?>([.. changeIds.Select(id =>
                new BrainNeighbor(
                    BrainRef.Change(id).Format(), null, "AFFECTS", "in", null))]);
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }
}
