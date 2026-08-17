using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Seed-invarianten voor #158 (patroon SourceSeedTests): de
/// judge-vragen moeten idempotent te importeren zijn (unieke ExternalKey) en
/// intern consistent (elke optie-index — inclusief een eventuele
/// CorrectIndex — moet binnen Options vallen). CorrectIndex is bewust overal
/// null: de officiële antwoordsleutel is nog niet bevestigd (issue #158) en
/// een vraag zonder sleutel mag nooit als waarheid behandeld worden.</summary>
public class BenchmarkSeedTests
{
    [Fact]
    public void ExternalKeys_AreUnique()
    {
        var keys = BenchmarkSeed.Defaults.Select(q => q.ExternalKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Part1_Bevat12JudgeVragen()
    {
        Assert.Equal(12, BenchmarkSeed.Defaults.Count);
        Assert.All(BenchmarkSeed.Defaults, q => Assert.Equal("judge", q.Category));
    }

    [Fact]
    public void ElkeVraag_HeeftMinstensTweeOpties()
    {
        Assert.All(BenchmarkSeed.Defaults, q => Assert.True(q.Options.Length >= 2, q.ExternalKey));
    }

    [Fact]
    public void CorrectIndex_IsNogOveralNull()
    {
        // De officiële antwoordsleutel is nog niet bevestigd — zie issue #158
        // ("0 bevestigde antwoorden"). Zodra Sjoerd een sleutel aanlevert komt
        // die als CorrectIndex-update binnen, niet via deze seed opnieuw.
        Assert.All(BenchmarkSeed.Defaults, q => Assert.Null(q.CorrectIndex));
    }

    [Fact]
    public void CorrectIndex_ValtBinnenOptiesBereik_AlsHijOoitGezetWordt()
    {
        // Toekomstbestendig: als een latere seed-update wél een CorrectIndex
        // zet, moet die geldig zijn — voorkomt een stille off-by-one.
        foreach (var q in BenchmarkSeed.Defaults.Where(q => q.CorrectIndex is not null))
            Assert.InRange(q.CorrectIndex!.Value, 0, q.Options.Length - 1);
    }

    [Fact]
    public void Vragen_EnOpties_ZijnNietLeeg()
    {
        foreach (var q in BenchmarkSeed.Defaults)
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Question), q.ExternalKey);
            Assert.All(q.Options, o => Assert.False(string.IsNullOrWhiteSpace(o), q.ExternalKey));
        }
    }
}
