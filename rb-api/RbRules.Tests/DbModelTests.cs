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
    // Passkeys (#109).
    [InlineData(typeof(PasskeyCredential), "passkey_credential")]
    [InlineData(typeof(PasskeyChallenge), "passkey_challenge")]
    // Provenance-ruggengraat (fase 0a, #233).
    [InlineData(typeof(MiningRun), "mining_run")]
    [InlineData(typeof(Assertion), "assertion")]
    // Getypeerde mechanic-predicaten (fase 5, #229).
    [InlineData(typeof(MechanicPredicateAssertion), "mechanic_predicate")]
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

    /// <summary>Vertaalbaarheids-net voor de passkey-login (#109): de
    /// credential-lookup vergelijkt op een bytea-kolom — bewezen vertaalbaar,
    /// niet aannemen (conventie).</summary>
    [Fact]
    public void PasskeyCredentialLookup_TranslatesToSql()
    {
        using var db = CreateContext();
        var rawId = new byte[] { 1, 2, 3 };

        var byCredentialId = db.PasskeyCredentials
            .Include(c => c.User)
            .Where(c => c.CredentialId == rawId);
        Assert.Contains("JOIN", byCredentialId.ToQueryString(), StringComparison.OrdinalIgnoreCase);

        var challenge = db.PasskeyChallenges
            .Where(c => c.TokenHash == "x");
        Assert.NotEmpty(challenge.ToQueryString());
    }

    /// <summary>Vertaalbaarheids-net voor de reviewqueue-weergaven van #124
    /// (conventie: LINQ dat naar SQL moet, is bewezen vertaalbaar): de
    /// default-filter (te reviewen + niet gearchiveerd) en de
    /// unreviewed-bovenaan-sortering met conditional (CASE WHEN).</summary>
    [Fact]
    public void ReviewQueueFilters_TranslateToSql()
    {
        using var db = CreateContext();

        var claimsDefault = db.Claims.AsNoTracking()
            .Where(c => c.Status == "unreviewed" && c.ArchivedAt == null)
            .OrderBy(c => c.Status == "unreviewed" && c.ArchivedAt == null ? 0 : 1)
            .ThenByDescending(c => c.LastSeen);
        Assert.Contains("CASE", claimsDefault.ToQueryString(), StringComparison.OrdinalIgnoreCase);

        var relationsArchived = db.Relations.AsNoTracking()
            .Where(r => r.ArchivedAt != null)
            .OrderBy(r => r.Status == "unreviewed" && r.ArchivedAt == null ? 0 : 1)
            .ThenByDescending(r => r.DetectedAt);
        Assert.NotEmpty(relationsArchived.ToQueryString());

        // Chip-tellingen over het niet-gearchiveerde deel.
        var counts = db.Claims.AsNoTracking()
            .Where(c => c.ArchivedAt == null)
            .GroupBy(c => c.Status)
            .Select(g => new { g.Key, Count = g.Count() });
        Assert.Contains("GROUP BY", counts.ToQueryString(), StringComparison.OrdinalIgnoreCase);
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
