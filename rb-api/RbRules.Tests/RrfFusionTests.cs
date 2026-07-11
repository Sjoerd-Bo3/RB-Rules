using RbRules.Domain;
using Xunit;

namespace RbRules.Tests;

public class RrfFusionTests
{
    [Fact]
    public void Fuse_ItemInBothLists_RanksAboveSingleListItems()
    {
        // 42 staat in beide lijsten (lager gerangschikt), 1 en 9 elk in één
        // lijst bovenaan — de dubbelganger wint door de som van bijdragen.
        var fused = RrfFusion.Fuse([[1L, 42L], [9L, 42L]], take: 3);
        Assert.Equal(42L, fused[0]);
        Assert.Equal(3, fused.Count);
    }

    [Fact]
    public void Fuse_HigherRankScoresHigher()
    {
        var fused = RrfFusion.Fuse([[7L, 8L, 9L]], take: 3);
        Assert.Equal([7L, 8L, 9L], fused);
    }

    [Fact]
    public void Fuse_TakeCapsTheResult()
    {
        var fused = RrfFusion.Fuse([[1L, 2L, 3L, 4L]], take: 2);
        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void Fuse_EmptyLists_YieldEmptyResult()
    {
        Assert.Empty(RrfFusion.Fuse<long>([[], []], take: 5));
        Assert.Empty(RrfFusion.Fuse(Array.Empty<IEnumerable<long>>(), take: 5));
    }

    [Fact]
    public void Fuse_BonusOverridesRankAdvantage()
    {
        // Review-fix: de oude tie-test slaagde ook zonder bonus (stabiele
        // sortering). Hier wint item 2 alleen als de bonus echt meetelt:
        // zonder bonus wint 1 op rang (1/61 > 1/62).
        var lists = new[] { new[] { (Id: 1L, Source: "community"), (Id: 2L, Source: "core-rules") } };
        var fused = RrfFusion.Fuse(
            lists, h => h.Id, take: 2,
            bonus: h => h.Source.Contains("core") ? 0.01 : 0);
        Assert.Equal(2L, fused[0]);
    }

    [Fact]
    public void Fuse_DefaultKIsSixty_AndRankOffsetIsOne()
    {
        Assert.Equal(60, RrfFusion.DefaultK);
        // Pin de +1-offset: met k=0 is de score 1/(rang+1). Item A op rang 0
        // in één lijst (score 1) is dan exact gelijk aan item B op rang 1 in
        // twee lijsten (1/2 + 1/2); zonder de offset zou rang 0 door nul
        // delen. Gelijkspel valt terug op insertievolgorde (A eerst).
        var fused = RrfFusion.Fuse([[1L, 2L], [3L, 2L]], take: 3, k: 0);
        Assert.Equal(1L, fused[0]);
    }

    [Fact]
    public void Fuse_BonusCountsPerOccurrence()
    {
        // Item 9 staat in beide lijsten op rang 1 (achterstand op item 8 van
        // 2/61 - 2/62 ~ 0.00053). Eén keer bonus (0.0004) is te weinig; twee
        // keer (per voorkomen, 0.0008) wint — dit pint het per-voorkomen-
        // gedrag vast waar AskService's bron-bias op rekent.
        var lists = new[]
        {
            new[] { (Id: 8L, B: 0.0), (Id: 9L, B: 0.0004) },
            new[] { (Id: 8L, B: 0.0), (Id: 9L, B: 0.0004) },
        };
        var fused = RrfFusion.Fuse(lists, h => h.Id, take: 2, bonus: h => h.B);
        Assert.Equal(9L, fused[0]);
    }

    [Fact]
    public void Fuse_EqualScores_KeepInsertionOrder()
    {
        // 1 en 2 hebben exact dezelfde somscore (rang 0+1 gespiegeld);
        // de stabiele sortering houdt de eerst-geziene sleutel voorop.
        var fused = RrfFusion.Fuse([[1L, 2L], [2L, 1L]], take: 2);
        Assert.Equal([1L, 2L], fused);
    }

    [Fact]
    public void Fuse_DuplicateAcrossLists_AppearsOnce()
    {
        var fused = RrfFusion.Fuse([[5L, 6L], [6L, 5L]], take: 10);
        Assert.Equal(2, fused.Count);
    }
}
