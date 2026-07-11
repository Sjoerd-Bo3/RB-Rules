using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Model-regressietests: tabelnamen moeten het PoP-schema spiegelen
/// (1-op-1 datamigratie) en vector-kolommen moeten GETYPT zijn.</summary>
public class DbModelTests
{
    private static RbRulesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseNpgsql("Host=localhost;Database=x;Username=x", o => o.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    [Theory]
    [InlineData(typeof(Source), "source")]
    [InlineData(typeof(Document), "document")]
    [InlineData(typeof(Change), "change")]
    [InlineData(typeof(Conflict), "conflict")]
    [InlineData(typeof(Correction), "correction")]
    [InlineData(typeof(CardSet), "card_set")]
    [InlineData(typeof(Card), "card")]
    [InlineData(typeof(RuleChunk), "rule_chunk")]
    [InlineData(typeof(RunLog), "run_log")]
    [InlineData(typeof(PushSubscription), "push_subscription")]
    [InlineData(typeof(Claim), "claim")]
    [InlineData(typeof(ClaimSource), "claim_source")]
    [InlineData(typeof(SourceProposal), "source_proposal")]
    // Accounts (#42): app_user en niet "user" — gereserveerd woord in Postgres.
    [InlineData(typeof(AppUser), "app_user")]
    [InlineData(typeof(UserSession), "user_session")]
    [InlineData(typeof(LoginToken), "login_token")]
    public void TableNames_MatchPopSchema(Type entity, string expectedTable)
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(entity);
        Assert.NotNull(entityType);
        Assert.Equal(expectedTable, entityType!.GetTableName());
    }

    /// <summary>Vertaalbaarheids-net voor de nieuwe aggregaties van #42
    /// (conventie: LINQ dat naar SQL moet, is bewezen vertaalbaar). Dit is de
    /// query-vorm van AdminOverviewService.UsersAsync en
    /// UserAccountService.UsageTodayAsync; ToQueryString dwingt de vertaling
    /// af zonder database.</summary>
    [Fact]
    public void UserUsageAggregates_TranslateToSql()
    {
        using var db = CreateContext();
        var since = DateTimeOffset.UtcNow.AddDays(-7);

        var perUser = db.AskMetrics.AsNoTracking()
            .Where(m => m.CreatedAt >= since)
            .GroupBy(m => m.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Questions = g.Count(),
                Photos = g.Count(m => m.HadImage),
                Hard = g.Count(m => m.Model == "hard" || (m.Model == null && m.HadImage)),
                Failed = g.Count(m => !m.Ok),
                AvgMs = (int)g.Average(m => m.DurationMs),
            });
        Assert.Contains("GROUP BY", perUser.ToQueryString(), StringComparison.OrdinalIgnoreCase);

        var usageToday = db.AskMetrics.AsNoTracking()
            .Where(m => m.UserId == 1 && m.CreatedAt >= since)
            .GroupBy(m => 1)
            .Select(g => new { Questions = g.Count(), Photos = g.Count(m => m.HadImage) });
        Assert.NotEmpty(usageToday.ToQueryString());

        // Sessie → gebruiker via de navigatie (UserQuotaFilter-pad).
        var resolve = db.UserSessions.AsNoTracking()
            .Where(s => s.TokenHash == "x" && s.ExpiresAt > since)
            .Select(s => s.User);
        Assert.Contains("JOIN", resolve.ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VectorColumns_AreTypedWithFixedDimension()
    {
        using var db = CreateContext();
        var expected = $"vector({EmbeddingConfig.Dimensions})";
        foreach (var (entity, prop) in new[]
                 {
                     (typeof(Card), nameof(Card.Embedding)),
                     (typeof(RuleChunk), nameof(RuleChunk.Embedding)),
                     (typeof(Correction), nameof(Correction.Embedding)),
                     (typeof(Claim), nameof(Claim.Embedding)),
                 })
        {
            var p = db.Model.FindEntityType(entity)!.FindProperty(prop);
            Assert.NotNull(p);
            Assert.Equal(expected, p!.GetColumnType());
        }
    }

    [Fact]
    public void CardAndRuleChunk_HaveHnswIndexOnEmbedding()
    {
        using var db = CreateContext();
        foreach (var entity in new[] { typeof(Card), typeof(RuleChunk), typeof(Claim) })
        {
            var idx = db.Model.FindEntityType(entity)!
                .GetIndexes()
                .Where(i => i.Properties.Any(p => p.Name == "Embedding"));
            Assert.Contains(idx, i => i.GetMethod() == "hnsw");
        }
    }
}
