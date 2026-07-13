using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Set-dekking (#145): het riftbound-id codeert nummer én settotaal
/// ("ogn-074-298" = 74 van 298). Per set: basistotaal (meest voorkomende
/// totaal wint), aanwezige basisnummers, ontbrekende nummers als exacte
/// lijst, en boven-basis-varianten (suffix a/star, overnumbered, specials).</summary>
public class SetCoverageTests
{
    [Fact]
    public void Aggregate_ComputesMissingNumbersExactly()
    {
        var rows = SetCoverage.Aggregate(
            ["ogn-001-005", "ogn-002-005", "ogn-004-005"]);
        var ogn = Assert.Single(rows);
        Assert.Equal("OGN", ogn.SetId);
        Assert.Equal(5, ogn.BaseTotal);
        Assert.Equal(2, ogn.MissingNumbers.Count);
        Assert.Equal([3, 5], ogn.MissingNumbers);
        Assert.Equal(3, ogn.Present);
        Assert.Equal(0, ogn.Variants);
        Assert.Empty(ogn.TotalDeviations);
    }

    [Fact]
    public void Aggregate_SuffixedAndOvernumberedPrintingsCountAsVariantsNotBase()
    {
        // Alt-art (057a), signature (227-star én de riftcodex-ster 233*) en
        // overnumbered (249 > 221) vullen géén basisnummers.
        var rows = SetCoverage.Aggregate(
        [
            "sfd-057-221", "sfd-057a-221", "sfd-227-star-221",
            "sfd-233*-221", "sfd-249-221",
        ]);
        var sfd = Assert.Single(rows);
        Assert.Equal(221, sfd.BaseTotal);
        Assert.Equal(1, sfd.Present);              // alleen 057 is een basisprinting
        Assert.Equal(4, sfd.Variants);
        Assert.DoesNotContain(57, sfd.MissingNumbers);
        // Boven-basis-nummers (227/233-star, 249) horen niet in 1..221.
        Assert.Equal(220, sfd.MissingNumbers.Count);
        Assert.DoesNotContain(227, sfd.MissingNumbers);
    }

    [Fact]
    public void Aggregate_MostCommonTotalWinsAndDeviationsAreReported()
    {
        // Bronruis (echt gezien bij OPP): promo's dragen totaal 298 naast 024.
        var rows = SetCoverage.Aggregate(
            ["opp-010-024", "opp-014-024", "opp-135-298"]);
        var opp = Assert.Single(rows);
        Assert.Equal(24, opp.BaseTotal);
        var deviation = Assert.Single(opp.TotalDeviations);
        Assert.Equal(new SetTotalDeviation(298, 1), deviation);
        // Het afwijkende nummer (135) ligt boven het basistotaal → variant.
        Assert.Equal(1, opp.Variants);
        Assert.Equal(2, opp.Present);
    }

    [Fact]
    public void Aggregate_TieOnTotalsPicksTheHighest()
    {
        // Liever te veel als ontbrekend rapporteren dan gaten verzwijgen.
        var rows = SetCoverage.Aggregate(["aaa-001-010", "aaa-002-020"]);
        Assert.Equal(20, Assert.Single(rows).BaseTotal);
    }

    [Fact]
    public void Aggregate_DeviatingTotalRowDoesNotMaskAMissingBaseNumber()
    {
        // Reviewdefect: "opp-005-298" (afwijkend totaal, bronruis) telde als
        // basisnummer 5 en maskeerde zo een écht gat — badge "compleet" én
        // uitval uit het gaten-signaal. Basisnummers tellen alleen bij het
        // winnende totaal; de afwijkende rij is een variant.
        var rows = SetCoverage.Aggregate(["opp-001-024", "opp-002-024", "opp-005-298"]);
        var opp = Assert.Single(rows);
        Assert.Equal(24, opp.BaseTotal);
        Assert.Equal(2, opp.Present);
        Assert.Contains(5, opp.MissingNumbers);
        Assert.Equal(1, opp.Variants);
        Assert.Equal([new SetTotalDeviation(298, 1)], opp.TotalDeviations);
    }

    [Fact]
    public void Aggregate_SpecialSeriesSubtotalsDoNotVoteForTheBaseTotal()
    {
        // Reviewdefect: sp-subtotalen (006) stemden mee voor het basistotaal
        // en konden het echte settotaal overstemmen — "5 van 6 ontbreekt"
        // i.p.v. "23 van 24". Specials stemmen niet mee.
        var rows = SetCoverage.Aggregate(
            ["ppp-001-024", "ppp-sp1-006", "ppp-sp2-006", "ppp-sp3-006"]);
        var ppp = Assert.Single(rows);
        Assert.Equal(24, ppp.BaseTotal);
        Assert.Equal(1, ppp.Present);
        Assert.Equal(23, ppp.MissingNumbers.Count);
        Assert.Equal(3, ppp.Variants);
        // Het sp-subtotaal blijft wel zichtbaar als afwijking.
        Assert.Equal([new SetTotalDeviation(6, 3)], ppp.TotalDeviations);
    }

    [Fact]
    public void Aggregate_SpMajorityStillLosesFromTheRealSetTotal()
    {
        // Zonder het stem-filter besliste hier alleen de tie-break (166 vs 6,
        // elk één stem) — één extra sp-rij flipte het basistotaal naar 6.
        var rows = SetCoverage.Aggregate(["ven-002-166", "ven-sp1-006", "ven-sp2-006"]);
        Assert.Equal(166, Assert.Single(rows).BaseTotal);
    }

    [Fact]
    public void Aggregate_TokensRunesAndSpecialSeriesAreVariantsWithoutTotals()
    {
        // Tokens/runes hebben geen settotaal; de sp-reeks een eigen subtotaal
        // (006) dat als afwijking zichtbaar blijft naast het echte totaal.
        var rows = SetCoverage.Aggregate(
            ["ven-002-166", "ven-r01", "ven-t04", "ven-sp3-006"]);
        var ven = Assert.Single(rows);
        Assert.Equal(166, ven.BaseTotal);
        Assert.Equal(1, ven.Present);
        Assert.Equal(3, ven.Variants);
        Assert.Equal([new SetTotalDeviation(6, 1)], ven.TotalDeviations);
    }

    [Fact]
    public void Aggregate_SetWithOnlyTokensHasNoBaseTotal()
    {
        var rows = SetCoverage.Aggregate(["unl-t01", "unl-t02"]);
        var unl = Assert.Single(rows);
        Assert.Null(unl.BaseTotal);
        Assert.Equal(0, unl.Present);
        Assert.Empty(unl.MissingNumbers);
        Assert.Equal(2, unl.Variants);
    }

    [Fact]
    public void Aggregate_SkipsUnparseableIdsAndSortsSets()
    {
        // Set-facet-restjes ("VEN") zijn geen kaarten — geen crash, geen rij.
        var rows = SetCoverage.Aggregate(
            ["sfd-001-002", "VEN", "ogn-001-001"]);
        Assert.Equal(["OGN", "SFD"], rows.Select(r => r.SetId).ToArray());
        Assert.All(rows, r => Assert.NotNull(r.BaseTotal));
    }

    [Fact]
    public void Aggregate_CompleteSetHasNoMissingNumbers()
    {
        var rows = SetCoverage.Aggregate(["ogs-001-002", "ogs-002-002", "ogs-001a-002"]);
        var ogs = Assert.Single(rows);
        Assert.Empty(ogs.MissingNumbers);
        Assert.Equal(2, ogs.Present);
        Assert.Equal(1, ogs.Variants);
    }
}
