using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Negeer-kandidaat-signaal (#180 deel 2): pure logica, geen I/O —
/// SourceListService levert de vier ingrediënten aan uit gebatchte
/// run_log/Change/ClaimSource/Correction-tellingen.</summary>
public class SourceIgnoreCandidacyTests
{
    [Fact]
    public void Evaluate_MinderDanTweeScans_NooitKandidaat()
    {
        Assert.False(SourceIgnoreCandidacy.Evaluate(
            completedScans: 1, changes: 0, claims: 0, rulings: 0));
    }

    [Fact]
    public void Evaluate_NulScans_NietKandidaat()
    {
        Assert.False(SourceIgnoreCandidacy.Evaluate(
            completedScans: 0, changes: 0, claims: 0, rulings: 0));
    }

    [Fact]
    public void Evaluate_TweeScansNietsOpgeleverd_Kandidaat()
    {
        Assert.True(SourceIgnoreCandidacy.Evaluate(
            completedScans: 2, changes: 0, claims: 0, rulings: 0));
    }

    [Fact]
    public void Evaluate_MetChanges_GeenKandidaat()
    {
        Assert.False(SourceIgnoreCandidacy.Evaluate(
            completedScans: 5, changes: 1, claims: 0, rulings: 0));
    }

    [Fact]
    public void Evaluate_MetClaims_GeenKandidaat()
    {
        Assert.False(SourceIgnoreCandidacy.Evaluate(
            completedScans: 5, changes: 0, claims: 2, rulings: 0));
    }

    [Fact]
    public void Evaluate_MetRulings_GeenKandidaat()
    {
        Assert.False(SourceIgnoreCandidacy.Evaluate(
            completedScans: 5, changes: 0, claims: 0, rulings: 1));
    }
}
