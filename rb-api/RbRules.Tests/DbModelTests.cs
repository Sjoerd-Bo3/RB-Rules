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
    public void TableNames_MatchPopSchema(Type entity, string expectedTable)
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(entity);
        Assert.NotNull(entityType);
        Assert.Equal(expectedTable, entityType!.GetTableName());
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
