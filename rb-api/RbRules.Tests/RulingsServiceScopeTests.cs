using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#177: sinds ClarificationMiningService bestaan mechanic/concept
/// ook als Correction.Scope (naast card/rule_section/answer) — deze tests
/// dekken dat het topic-filter in /rulings die twee nieuwe scopes vindt, en
/// dat ze niet óók onder "answer" dubbel meetellen. BrowseAsync (geen
/// zoekterm) raakt geen full-text/vector-operaties, dus EF InMemory volstaat.</summary>
public class RulingsServiceScopeTests
{
    [Fact]
    public async Task QueryAsync_TopicMechanic_ReturnsMechanicScopedCorrection()
    {
        using var db = NewDb();
        AddVerifiedCorrection(db, scope: "mechanic", reference: "Legion", text: "Legion = finalize.");
        AddVerifiedCorrection(db, scope: "card", reference: "Viktor", text: "Viktor-ruling.");
        await db.SaveChangesAsync();

        var svc = new RulingsService(db, Embeddings(), NullLogger<RulingsService>.Instance);
        var result = await svc.QueryAsync(q: null, topic: "mechanic", page: 1);

        var item = Assert.Single(result.Items);
        Assert.Equal("mechanic", item.Topic);
        Assert.Equal("Legion", item.TopicRef);
    }

    [Fact]
    public async Task QueryAsync_TopicConcept_ReturnsConceptScopedCorrection()
    {
        using var db = NewDb();
        AddVerifiedCorrection(db, scope: "concept", reference: "Reflection tokens", text: "Tellen niet mee.");
        await db.SaveChangesAsync();

        var svc = new RulingsService(db, Embeddings(), NullLogger<RulingsService>.Instance);
        var result = await svc.QueryAsync(q: null, topic: "concept", page: 1);

        var item = Assert.Single(result.Items);
        Assert.Equal("concept", item.Topic);
    }

    [Fact]
    public async Task QueryAsync_TopicAnswer_ExcludesMechanicAndConceptScopedCorrections()
    {
        // "answer" blijft de vangnet-bucket voor claim/relation-promoties —
        // mechanic/concept-scoped rulings mogen daar niet óók in meetellen.
        using var db = NewDb();
        AddVerifiedCorrection(db, scope: "mechanic", reference: "Legion", text: "Legion = finalize.");
        AddVerifiedCorrection(db, scope: "concept", reference: "Mulligan", text: "Eén keer omruilen.");
        AddVerifiedCorrection(db, scope: "claim", reference: "claim:1", text: "Review-notitie-promotie.");
        await db.SaveChangesAsync();

        var svc = new RulingsService(db, Embeddings(), NullLogger<RulingsService>.Instance);
        var result = await svc.QueryAsync(q: null, topic: "answer", page: 1);

        // "answer" toont nooit een TopicRef (up/down-feedback heeft er geen
        // zinvolle) — het punt hier is dat precies 1 item (de claim/relation-
        // promotie) overblijft, niet de mechanic/concept-scoped rulings.
        var item = Assert.Single(result.Items);
        Assert.Equal("Review-notitie-promotie.", item.Text);
    }

    [Fact]
    public async Task QueryAsync_NoTopicFilter_ReturnsAllScopes()
    {
        using var db = NewDb();
        AddVerifiedCorrection(db, scope: "mechanic", reference: "Legion", text: "Legion = finalize.");
        AddVerifiedCorrection(db, scope: "card", reference: "Viktor", text: "Viktor-ruling.");
        await db.SaveChangesAsync();

        var svc = new RulingsService(db, Embeddings(), NullLogger<RulingsService>.Instance);
        var result = await svc.QueryAsync(q: null, topic: null, page: 1);

        Assert.Equal(2, result.Items.Count);
    }

    // --- testinfra ---------------------------------------------------------

    private static void AddVerifiedCorrection(
        RbRulesDbContext db, string scope, string reference, string text) =>
        db.Corrections.Add(new Correction
        {
            Scope = scope, Ref = reference, Text = text,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });

    private static EmbeddingService Embeddings() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
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
