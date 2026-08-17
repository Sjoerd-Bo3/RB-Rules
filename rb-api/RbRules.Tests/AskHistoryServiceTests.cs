using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Eigen ask-geschiedenis (#157): scope strikt op user_id resp.
/// ip_hash van het huidige request — nooit kruislekkage tussen accounts/IP's,
/// en nooit een crash zonder scope (anoniem zonder secret/eerdere vraag).</summary>
public class AskHistoryServiceTests
{
    [Fact]
    public async Task RecentAsync_IngelogdeGebruiker_AlleenEigenVragenOpUserId()
    {
        using var db = NewDb();
        db.AskTraces.AddRange(
            Trace("mijn vraag 1", userId: 1),
            Trace("mijn vraag 2", userId: 1),
            Trace("andermans vraag", userId: 2),
            // Zelfde ip_hash als gebruiker 1 zou hebben — moet genegeerd
            // worden zodra userId bekend is (ingelogd wint van ip_hash).
            Trace("anonieme vraag zelfde ip", ipHash: "hash-van-user-1-ip"));
        await db.SaveChangesAsync();

        var items = await new AskHistoryService(db).RecentAsync(userId: 1, ipHash: "hash-van-user-1-ip");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.StartsWith("mijn vraag", i.Question));
    }

    [Fact]
    public async Task RecentAsync_Anoniem_AlleenEigenIpHash_GeenKruislekkage()
    {
        using var db = NewDb();
        db.AskTraces.AddRange(
            Trace("van mijn ip", ipHash: "hash-a"),
            Trace("van ander ip", ipHash: "hash-b"),
            Trace("ingelogde vraag", userId: 5));
        await db.SaveChangesAsync();

        var items = await new AskHistoryService(db).RecentAsync(userId: null, ipHash: "hash-a");

        var item = Assert.Single(items);
        Assert.Equal("van mijn ip", item.Question);
    }

    [Fact]
    public async Task RecentAsync_ZonderUserIdEnZonderIpHash_GeeftLegeLijst()
    {
        using var db = NewDb();
        db.AskTraces.Add(Trace("iemand anders", userId: 1));
        await db.SaveChangesAsync();

        // Anoniem zonder ASK_IP_HASH_SECRET (of nog geen eerdere vraag): geen
        // enkele scope om op te filteren — lege lijst, geen crash.
        var items = await new AskHistoryService(db).RecentAsync(userId: null, ipHash: null);

        Assert.Empty(items);
    }

    [Fact]
    public async Task RecentAsync_NieuwsteEerst_EnGecaptOpTwintig()
    {
        using var db = NewDb();
        for (var i = 0; i < 25; i++)
            db.AskTraces.Add(Trace($"vraag {i}", userId: 1,
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var items = await new AskHistoryService(db).RecentAsync(userId: 1, ipHash: null);

        Assert.Equal(20, items.Count);
        Assert.Equal("vraag 0", items[0].Question);
        Assert.Equal("vraag 19", items[19].Question);
    }

    [Fact]
    public async Task RecentAsync_ItemDraagtAntwoordVraagtypeEnAgentic()
    {
        using var db = NewDb();
        var trace = Trace("Werkt Deflect tijdens een showdown?", userId: 1);
        trace.Answer = "**Oordeel:** Ja. [1]";
        trace.QuestionType = "Ruling";
        trace.Agentic = true;
        db.AskTraces.Add(trace);
        await db.SaveChangesAsync();

        var item = Assert.Single(await new AskHistoryService(db).RecentAsync(userId: 1, ipHash: null));

        Assert.Equal("Werkt Deflect tijdens een showdown?", item.Question);
        Assert.Equal("**Oordeel:** Ja. [1]", item.Answer);
        Assert.Equal("Ruling", item.QuestionType);
        Assert.True(item.Agentic);
    }

    // --- testinfra -------------------------------------------------------

    private static AskTrace Trace(
        string question, long? userId = null, string? ipHash = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Question = question,
        QuestionType = "Ruling",
        DurationMs = 1_000,
        UserId = userId,
        IpHash = ipHash,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AskService-tests).</summary>
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
