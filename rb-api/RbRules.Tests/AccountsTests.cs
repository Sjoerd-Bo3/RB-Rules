using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure account-logica (#42): e-mail-normalisatie, token-hygiëne en
/// de quota-beslissing die UserQuotaFilter handhaaft.</summary>
public class AccountsTests
{
    [Theory]
    [InlineData("speler@example.com", "speler@example.com")]
    [InlineData("  Speler@Example.COM  ", "speler@example.com")]
    [InlineData("a.b+tag@sub.domein.nl", "a.b+tag@sub.domein.nl")]
    public void NormalizeEmail_AcceptsAndNormalizes(string raw, string expected)
    {
        Assert.Equal(expected, Accounts.NormalizeEmail(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geen-apenstaart")]
    [InlineData("@example.com")]           // lege local part
    [InlineData("speler@")]                // leeg domein
    [InlineData("speler@localhost")]       // domein zonder punt
    [InlineData("a@b@example.com")]        // twee apenstaarten
    [InlineData("speler@.nl")]             // domein begint met punt
    [InlineData("speler@example.")]        // domein eindigt op punt
    [InlineData("spe ler@example.com")]    // witruimte binnenin
    public void NormalizeEmail_RejectsInvalid(string? raw)
    {
        Assert.Null(Accounts.NormalizeEmail(raw));
    }

    [Fact]
    public void NormalizeEmail_RejectsOverlongAddresses()
    {
        var raw = new string('a', 250) + "@example.com";
        Assert.Null(Accounts.NormalizeEmail(raw));
    }

    [Fact]
    public void NewToken_IsUrlSafeAndUnpredictablyUnique()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => Accounts.NewToken()).ToList();
        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            // Reist in een query-parameter: geen tekens die encoding vergen.
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
            Assert.True(t.Length >= 42, "256 bits base64url is minstens 43 tekens");
        });
    }

    [Fact]
    public void HashToken_IsDeterministicAndTokenSpecific()
    {
        var token = Accounts.NewToken();
        Assert.Equal(Accounts.HashToken(token), Accounts.HashToken(token));
        Assert.NotEqual(Accounts.HashToken(token), Accounts.HashToken(token + "x"));
        Assert.Matches("^[0-9a-f]{64}$", Accounts.HashToken(token));
    }

    [Fact]
    public void CheckQuota_BlockedWinsAltijd()
    {
        var check = Accounts.CheckQuota(
            blocked: true, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday: 0, photosToday: 0, countsQuestion: false, hasImage: false);
        Assert.Equal(QuotaVerdict.Blocked, check.Verdict);
        Assert.False(check.Allowed);
    }

    [Fact]
    public void CheckQuota_NonQuestionRoutes_OnlyBlockedCheck()
    {
        // resolve/explain/feedback: ook boven het dagquotum toegestaan —
        // daar is de rate-limiter de rem.
        var check = Accounts.CheckQuota(
            blocked: false, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday: 30, photosToday: 5, countsQuestion: false, hasImage: false);
        Assert.True(check.Allowed);
    }

    [Theory]
    [InlineData(29, true)]   // net onder de limiet: mag
    [InlineData(30, false)]  // op de limiet: dicht
    [InlineData(31, false)]
    public void CheckQuota_DailyQuestionLimit(int questionsToday, bool allowed)
    {
        var check = Accounts.CheckQuota(
            blocked: false, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday, photosToday: 0, countsQuestion: true, hasImage: false);
        Assert.Equal(allowed, check.Allowed);
        if (!allowed) Assert.Equal(QuotaVerdict.QuestionsExhausted, check.Verdict);
    }

    [Fact]
    public void CheckQuota_PhotoLimit_OnlyAppliesToPhotoQuestions()
    {
        // Foto-quotum vol, maar zonder foto mag de vraag gewoon door.
        var textOnly = Accounts.CheckQuota(
            blocked: false, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday: 10, photosToday: 5, countsQuestion: true, hasImage: false);
        Assert.True(textOnly.Allowed);

        var withPhoto = Accounts.CheckQuota(
            blocked: false, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday: 10, photosToday: 5, countsQuestion: true, hasImage: true);
        Assert.Equal(QuotaVerdict.PhotosExhausted, withPhoto.Verdict);
        Assert.Contains("foto", withPhoto.Message);
    }

    [Fact]
    public void CheckQuota_PhotoMessage_WinsOverGenericWhenBothExhausted()
    {
        // Beide limieten vol met een foto-vraag: de specifiekere melding.
        var check = Accounts.CheckQuota(
            blocked: false, dailyQuota: 30, dailyPhotoQuota: 5,
            questionsToday: 30, photosToday: 5, countsQuestion: true, hasImage: true);
        Assert.Equal(QuotaVerdict.PhotosExhausted, check.Verdict);
    }
}
