using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Temporele precedentie (#168): TrustTier blijft primair, recency
/// is de tie-breaker bij gelijk gezag — inclusief het null-datum-randgeval
/// ("nooit raden, wél voorspelbaar oudste").</summary>
public class PrecedenceTests
{
    // ── Compare ──────────────────────────────────────────────────────────

    [Fact]
    public void Compare_LowerTierWins_RegardlessOfDate()
    {
        // Tier 1 (officieel) met een oude datum wint van tier 3 met een
        // gloednieuwe datum — gezag blijft primair.
        var cmp = Precedence.Compare<DateOnly>(1, new DateOnly(2020, 1, 1), 3, new DateOnly(2026, 1, 1));
        Assert.True(cmp > 0);
    }

    [Fact]
    public void Compare_EqualTier_NewestDateWins()
    {
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(Precedence.Compare<DateTimeOffset>(1, newer, 1, older) > 0);
        Assert.True(Precedence.Compare<DateTimeOffset>(1, older, 1, newer) < 0);
    }

    [Fact]
    public void Compare_EqualTierAndDate_IsTie() =>
        Assert.Equal(0, Precedence.Compare<DateOnly>(1, new DateOnly(2026, 1, 1), 1, new DateOnly(2026, 1, 1)));

    [Fact]
    public void Compare_NullDate_SortsAsOldest_WhenOtherHasDate()
    {
        // Ontbrekende datum verliest van een bekende datum bij gelijke tier —
        // maar wordt nooit als "nu" of "gelijk" behandeld.
        Assert.True(Precedence.Compare<DateOnly>(1, null, 1, new DateOnly(2020, 1, 1)) < 0);
        Assert.True(Precedence.Compare<DateOnly>(1, new DateOnly(2020, 1, 1), 1, null) > 0);
    }

    [Fact]
    public void Compare_BothNullDates_IsTie() =>
        Assert.Equal(0, Precedence.Compare<DateOnly>(1, null, 1, null));

    // ── Winner ───────────────────────────────────────────────────────────

    // ── CompareStable (deterministische totale orde, review-fix #168) ────

    [Fact]
    public void CompareStable_TierBeatsEverything()
    {
        // Tier 1 met oude datum, lage rank en lage id wint van tier 3 met
        // verse datum, hoge rank en hoge id — gezag blijft primair.
        Assert.True(Precedence.CompareStable<DateOnly>(
            1, new DateOnly(2020, 1, 1), 0, 1,
            3, new DateOnly(2026, 1, 1), 999, 999) > 0);
    }

    [Fact]
    public void CompareStable_EqualTier_NewestDateWins()
    {
        Assert.True(Precedence.CompareStable<DateOnly>(
            1, new DateOnly(2026, 6, 1), 0, 1,
            1, new DateOnly(2025, 1, 1), 999, 999) > 0);
    }

    [Fact]
    public void CompareStable_EqualTierAndDate_HigherRankWins()
    {
        // Zelfde tier, beide null datum: Rank beslist (bron-voorkeur).
        Assert.True(Precedence.CompareStable<DateOnly>(
            1, null, 100, 1,
            1, null, 80, 999) > 0);
    }

    [Fact]
    public void CompareStable_EqualTierDateRank_HigherIdWins_Deterministic()
    {
        // Volledige gelijkstand op tier/datum/rank ⇒ de finale sleutel (Id)
        // beslist eenduidig. En omgekeerd exact gespiegeld (totale orde).
        Assert.True(Precedence.CompareStable<DateOnly>(1, null, 100, 5, 1, null, 100, 2) > 0);
        Assert.True(Precedence.CompareStable<DateOnly>(1, null, 100, 2, 1, null, 100, 5) < 0);
    }

    [Fact]
    public void CompareStable_IdenticalKeys_IsZero() =>
        Assert.Equal(0, Precedence.CompareStable<DateOnly>(1, null, 100, 7, 1, null, 100, 7));

    // ── ReorderTiedByTier ────────────────────────────────────────────────

    private record RankedItem(string Key, short Tier, DateTimeOffset? Date);

    private static DateTimeOffset D(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReorderTiedByTier_KeepsOrderAcrossDifferentTiers()
    {
        // Fusie-rangorde over verschillende tiers blijft ongemoeid, ook al
        // heeft de lagere tier een oudere datum — dit is geen nieuwe rangorde.
        var ranked = new List<RankedItem>
        {
            new("a", 1, D(1)),
            new("b", 2, D(30)),
        };
        var result = Precedence.ReorderTiedByTier(ranked, r => r.Tier, r => r.Date);
        Assert.Equal(["a", "b"], result.Select(r => r.Key));
    }

    [Fact]
    public void ReorderTiedByTier_ReordersWithinEqualTierByRecency()
    {
        var ranked = new List<RankedItem>
        {
            new("oud", 1, D(1)),
            new("nieuw", 1, D(20)),
            new("ander-tier", 2, D(15)),
        };
        var result = Precedence.ReorderTiedByTier(ranked, r => r.Tier, r => r.Date);
        Assert.Equal(["nieuw", "oud", "ander-tier"], result.Select(r => r.Key));
    }

    [Fact]
    public void ReorderTiedByTier_NullDatesKeepOriginalRelativeOrder()
    {
        // Binnen een gelijke-tier-reeks verliest een null-datum-item altijd
        // van een item mét datum, maar twee null-items behouden hun
        // onderlinge fusievolgorde (stabiele sort op index).
        var ranked = new List<RankedItem>
        {
            new("nul-1", 1, null),
            new("nul-2", 1, null),
            new("met-datum", 1, D(5)),
        };
        var result = Precedence.ReorderTiedByTier(ranked, r => r.Tier, r => r.Date);
        Assert.Equal(["met-datum", "nul-1", "nul-2"], result.Select(r => r.Key));
    }

    [Fact]
    public void ReorderTiedByTier_SingleItemRun_IsUnchanged()
    {
        var ranked = new List<RankedItem> { new("solo", 1, D(1)) };
        var result = Precedence.ReorderTiedByTier(ranked, r => r.Tier, r => r.Date);
        Assert.Equal(["solo"], result.Select(r => r.Key));
    }
}
