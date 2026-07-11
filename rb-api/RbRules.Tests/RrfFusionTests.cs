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
    public void Fuse_BonusBreaksTie_LikeSourceBias()
    {
        // Twee items op dezelfde rang in evenveel lijsten; de bron-bias
        // (zoals AskService die per vraagtype geeft) beslist.
        var lists = new[]
        {
            new[] { (Id: 1L, Source: "core-rules") },
            new[] { (Id: 2L, Source: "community") },
        };
        var fused = RrfFusion.Fuse(
            lists, h => h.Id, take: 2,
            bonus: h => h.Source.Contains("core") ? 0.008 : 0);
        Assert.Equal(1L, fused[0]);
    }

    [Fact]
    public void Fuse_DuplicateAcrossLists_AppearsOnce()
    {
        var fused = RrfFusion.Fuse([[5L, 6L], [6L, 5L]], take: 10);
        Assert.Equal(2, fused.Count);
    }
}
