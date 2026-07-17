using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Vraag-traces in het beheer (#143): de lijst blijft slank (route-
/// metadata, géén antwoord/historie — dat dwingt het lijst-record al op
/// type-niveau af) en het detail levert het volledige gesprek: antwoord plus
/// de geparste beurten uit het JSON-snapshot.</summary>
public class AdminOverviewTraceTests
{
    [Fact]
    public async Task AskTracesAsync_LijstBlijftSlank_NieuwsteEerst()
    {
        using var db = NewDb();
        var forced = Trace("nieuwe vraag", createdAt: DateTimeOffset.UtcNow);
        // #153: door de gebruiker geforceerde escalatie — de lijst draagt de
        // attributie voor de badge "agentic (gebruiker)".
        forced.Agentic = true;
        forced.EscalatedBy = "user";
        db.AskTraces.AddRange(
            Trace("oude vraag", createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
            forced);
        await db.SaveChangesAsync();

        var items = await new AdminOverviewService(db, new ChangeFeedService(db)).AskTracesAsync();

        Assert.Equal(["nieuwe vraag", "oude vraag"], items.Select(t => t.Question));
        // Route-metadata blijft aanwezig in de lijst.
        Assert.All(items, t => Assert.Equal("Ruling", t.QuestionType));
        Assert.Equal("user", items.Single(t => t.Question == "nieuwe vraag").EscalatedBy);
        Assert.Null(items.Single(t => t.Question == "oude vraag").EscalatedBy);
    }

    [Fact]
    public async Task AskTraceAsync_DetailLevertAntwoordEnBeurten()
    {
        using var db = NewDb();
        var trace = Trace("vervolgvraag");
        trace.Answer = "**Oordeel:** Ja. [1]";
        trace.History =
            """[{"question":"eerste vraag","answer":"eerste antwoord"}]""";
        db.AskTraces.Add(trace);
        await db.SaveChangesAsync();

        var detail = await new AdminOverviewService(db, new ChangeFeedService(db)).AskTraceAsync(trace.Id);

        Assert.NotNull(detail);
        Assert.Equal("**Oordeel:** Ja. [1]", detail!.Answer);
        var turn = Assert.Single(detail.History);
        Assert.Equal("eerste vraag", turn.Question);
        Assert.Equal("eerste antwoord", turn.Answer);
    }

    [Fact]
    public async Task AskTraceAsync_OnbekendId_GeeftNull()
    {
        using var db = NewDb();
        Assert.Null(await new AdminOverviewService(db, new ChangeFeedService(db)).AskTraceAsync(999));
    }

    [Fact]
    public async Task AskTraceAsync_KapotSnapshot_DegradeertNaarLeegGesprek()
    {
        using var db = NewDb();
        var trace = Trace("vraag");
        trace.Answer = "antwoord";
        trace.History = "dit is geen JSON";
        db.AskTraces.Add(trace);
        await db.SaveChangesAsync();

        var detail = await new AdminOverviewService(db, new ChangeFeedService(db)).AskTraceAsync(trace.Id);

        // Het antwoord blijft zichtbaar; alleen het gesprek valt weg.
        Assert.Equal("antwoord", detail!.Answer);
        Assert.Empty(detail.History);
    }

    // --- testinfra -------------------------------------------------------

    private static AskTrace Trace(string question, DateTimeOffset? createdAt = null) => new()
    {
        Question = question,
        QuestionType = "Ruling",
        DurationMs = 1_000,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de AskService-tests).</summary>
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
