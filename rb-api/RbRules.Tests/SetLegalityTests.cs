using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Set-legaliteit (#22): statusafleiding uit PublishedOn t.o.v. een
/// meegegeven 'vandaag', inclusief de randgevallen releasedag en onbekende
/// datum.</summary>
public class SetLegalityTests
{
    private static readonly DateOnly Today = new(2026, 7, 11);

    [Fact]
    public void StatusFor_UnknownDate_IsAnnounced() =>
        Assert.Equal(SetLegalityStatus.Announced, SetLegality.StatusFor(null, Today));

    [Fact]
    public void StatusFor_ReleaseDayItself_IsLegal() =>
        Assert.Equal(SetLegalityStatus.Legal, SetLegality.StatusFor(Today, Today));

    [Fact]
    public void StatusFor_PastDate_IsLegal() =>
        Assert.Equal(SetLegalityStatus.Legal,
            SetLegality.StatusFor(new DateOnly(2025, 10, 31), Today));

    [Fact]
    public void StatusFor_Tomorrow_IsUpcoming() =>
        Assert.Equal(SetLegalityStatus.Upcoming,
            SetLegality.StatusFor(Today.AddDays(1), Today));

    [Theory]
    [InlineData(SetLegalityStatus.Legal, "legal")]
    [InlineData(SetLegalityStatus.Upcoming, "upcoming")]
    [InlineData(SetLegalityStatus.Announced, "announced")]
    public void Key_IsStableApiValue(SetLegalityStatus status, string expected) =>
        Assert.Equal(expected, SetLegality.Key(status));

    [Fact]
    public void PromptFact_FutureSet_NamesSetAndDate() =>
        Assert.Equal("NOG NIET LEGAAL — komt in set Vendetta op 2026-07-31.",
            SetLegality.PromptFact(new DateOnly(2026, 7, 31), Today, "Vendetta"));

    [Fact]
    public void PromptFact_FutureSetWithoutName_StillWarns() =>
        Assert.Equal("NOG NIET LEGAAL — komt in set ? op 2026-07-31.",
            SetLegality.PromptFact(new DateOnly(2026, 7, 31), Today, null));

    [Theory]
    [InlineData(null)]         // datum onbekend: geen legaliteitsclaim
    [InlineData("2026-07-11")] // releasedag zelf: legaal
    [InlineData("2025-10-31")] // verleden: legaal
    public void PromptFact_ReleasedOrUnknown_ReturnsNull(string? published) =>
        Assert.Null(SetLegality.PromptFact(
            published is null ? null : DateOnly.Parse(published), Today, "Origins"));
}
