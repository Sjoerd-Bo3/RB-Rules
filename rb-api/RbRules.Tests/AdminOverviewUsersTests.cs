using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Kostenoverzicht in het beheer (#42/#121): UsersAsync telt naast
/// vragen/foto's nu ook de echte tokens — per gebruiker én per antwoordpad
/// (cheap/hard/agentic). Metrics zonder usage (oude rb-ai) tellen als 0
/// tokens maar wél als vraag: de som is een ondergrens, geen verzinsel.</summary>
public class AdminOverviewUsersTests
{
    [Fact]
    public async Task UsersAsync_TeltTokensPerGebruikerEnPerPad()
    {
        using var db = NewDb();
        db.Users.Add(new AppUser { Email = "a@example.com" }); // Id 1
        db.Users.Add(new AppUser { Email = "b@example.com" }); // Id 2
        db.AskMetrics.AddRange(
            // Gebruiker 1: cheap mét usage + hard (foto) mét usage.
            Metric(userId: 1, model: "cheap", input: 1_000, output: 100),
            Metric(userId: 1, model: "hard", input: 5_000, output: 400, hadImage: true),
            // Gebruiker 2: oude rij zonder model én zonder usage (vóór #42/#121):
            // telt als cheap-vraag met 0 tokens.
            Metric(userId: 2, model: null, input: null, output: null),
            // Anoniem: agentic mét usage, van vóór #153 (EscalatedBy null) —
            // dat waren per definitie gate-escalaties.
            Metric(userId: null, model: "agentic", input: 52_000, output: 700),
            // Gebruiker 1: zelf geforceerde Grondig-vraag (#153) — eigen pad
            // in het kostenoverzicht.
            Metric(userId: 1, model: "agentic", input: 40_000, output: 500,
                escalatedBy: "user"));
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db).UsersAsync("7d", page: 1);

        var a = overview.Items.Single(u => u.Email == "a@example.com");
        Assert.Equal(46_000, a.InputTokens);
        Assert.Equal(1_000, a.OutputTokens);
        var b = overview.Items.Single(u => u.Email == "b@example.com");
        Assert.Equal(0, b.InputTokens);
        Assert.Equal(0, b.OutputTokens);

        // Pad-totalen over álle vragen (incl. anoniem), vaste volgorde
        // cheap → hard → agentic (gate) → agentic (gebruiker) (#153).
        Assert.Equal(
            ["cheap", "hard", "agentic (gate)", "agentic (gebruiker)"],
            overview.Paths.Select(p => p.Path));
        var cheap = overview.Paths.Single(p => p.Path == "cheap");
        Assert.Equal(2, cheap.Questions);
        Assert.Equal(1_000, cheap.InputTokens);
        Assert.Equal(100, cheap.OutputTokens);
        var gate = overview.Paths.Single(p => p.Path == "agentic (gate)");
        Assert.Equal(1, gate.Questions);
        Assert.Equal(52_000, gate.InputTokens);
        Assert.Equal(700, gate.OutputTokens);
        var forced = overview.Paths.Single(p => p.Path == "agentic (gebruiker)");
        Assert.Equal(1, forced.Questions);
        Assert.Equal(40_000, forced.InputTokens);
        Assert.Equal(500, forced.OutputTokens);
    }

    [Fact]
    public async Task UsersAsync_OudeRijZonderModelMetFoto_ValtOnderHard()
    {
        using var db = NewDb();
        // Rij van vóór #42: geen model, wél foto — dat was toen per definitie
        // het dure pad; de pad-totalen delen die afleiding met de Hard-teller.
        db.AskMetrics.Add(Metric(userId: null, model: null, input: null, output: null, hadImage: true));
        await db.SaveChangesAsync();

        var overview = await new AdminOverviewService(db).UsersAsync("7d", page: 1);

        var path = Assert.Single(overview.Paths);
        Assert.Equal("hard", path.Path);
        Assert.Equal(1, path.Questions);
        Assert.Equal(0, path.InputTokens);
    }

    // --- testinfra -------------------------------------------------------

    private static AskMetric Metric(
        long? userId, string? model, long? input, long? output, bool hadImage = false,
        string? escalatedBy = null) => new()
    {
        DurationMs = 1_000,
        QuestionType = "Ruling",
        HadImage = hadImage,
        UserId = userId,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        EscalatedBy = escalatedBy,
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
