using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Relatie-triage op het reviewqueue-overzicht (#199 v1):
/// AdminOverviewService.RelationsAsync draagt de aanbeveling door en sorteert
/// erop (accept/reject eerst — de twee bulk-actionabele groepen — dan unsure,
/// dan nog niet getriaged), plus de aanbevelingsgroep-tellingen voor de
/// bulk-actie-knoppen.</summary>
public class AdminOverviewRelationsTests
{
    [Fact]
    public async Task RelationsAsync_SorteertOpAanbeveling_AcceptEnRejectEerst()
    {
        using var db = NewDb();
        var untriaged = Voorstel(db, "concept:a", "concept:b", recommendation: null);
        var unsure = Voorstel(db, "concept:c", "concept:d", recommendation: "unsure");
        var reject = Voorstel(db, "concept:e", "concept:f", recommendation: "reject");
        var accept = Voorstel(db, "concept:g", "concept:h", recommendation: "accept");
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db).RelationsAsync(status: null, page: 1);

        Assert.Equal(
            new[] { accept.Id, reject.Id, unsure.Id, untriaged.Id },
            overview.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task RelationsAsync_DraagtRecommendationEnReasonDoor()
    {
        using var db = NewDb();
        var r = Voorstel(db, "concept:a", "concept:b", recommendation: "accept");
        r.RecommendationReason = "The context confirms it (refs: 402.3)";
        await db.SaveChangesAsync();

        var item = Assert.Single((await new AdminOverviewService(db).RelationsAsync(null, 1)).Items);

        Assert.Equal("accept", item.Recommendation);
        Assert.Equal("The context confirms it (refs: 402.3)", item.RecommendationReason);
    }

    [Fact]
    public async Task RelationsAsync_TeltAanbevelingsgroepenInDeHuidigeWeergave()
    {
        using var db = NewDb();
        Voorstel(db, "concept:a", "concept:b", recommendation: "accept");
        Voorstel(db, "concept:c", "concept:d", recommendation: "accept");
        Voorstel(db, "concept:e", "concept:f", recommendation: "reject");
        Voorstel(db, "concept:g", "concept:h", recommendation: null);
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db).RelationsAsync(null, 1);

        Assert.Equal(2, overview.RecommendationCounts.Single(c => c.Recommendation == "accept").Count);
        Assert.Equal(1, overview.RecommendationCounts.Single(c => c.Recommendation == "reject").Count);
        Assert.DoesNotContain(overview.RecommendationCounts, c => c.Recommendation == "unsure");
    }

    private static Relation Voorstel(
        RbRulesDbContext db, string fromRef, string toRef, string? recommendation)
    {
        var relation = new Relation
        {
            FromRef = fromRef, ToRef = toRef, Kind = "clarifies",
            Explanation = "uitleg", Provenance = "test", Trust = 0.5,
            Recommendation = recommendation,
        };
        db.Relations.Add(relation);
        return relation;
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AdminOverview-tests).</summary>
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
