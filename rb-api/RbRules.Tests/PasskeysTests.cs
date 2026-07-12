using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure passkey-logica (#109): RP-id-afleiding uit PUBLIC_BASE_URL,
/// de sign-count-replay-beslissing en challenge-geldigheid (TTL + soort).</summary>
public class PasskeysTests
{
    [Theory]
    [InlineData("https://riftbound-v2.bo3.dev", "riftbound-v2.bo3.dev", "https://riftbound-v2.bo3.dev")]
    [InlineData("https://riftbound-v2.bo3.dev/", "riftbound-v2.bo3.dev", "https://riftbound-v2.bo3.dev")]
    [InlineData("  https://riftbound-v2.bo3.dev  ", "riftbound-v2.bo3.dev", "https://riftbound-v2.bo3.dev")]
    // Expliciete poort: RP-id blijft de kale host, de origin houdt de poort —
    // zo ziet de browser het ook (WebAuthn vergelijkt op volledige origin).
    [InlineData("http://localhost:5173", "localhost", "http://localhost:5173")]
    [InlineData("https://voorbeeld.nl:8443/pad", "voorbeeld.nl", "https://voorbeeld.nl:8443")]
    public void DeriveRelyingParty_UsesHostFromPublicBaseUrl(
        string baseUrl, string expectedRpId, string expectedOrigin)
    {
        var (rpId, origins) = Passkeys.DeriveRelyingParty(baseUrl);
        Assert.Equal(expectedRpId, rpId);
        Assert.Equal([expectedOrigin], origins.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geen-url")]
    [InlineData("ftp://voorbeeld.nl")] // WebAuthn bestaat alleen op http(s)
    public void DeriveRelyingParty_FallsBackToLocalhostForDev(string? baseUrl)
    {
        var (rpId, origins) = Passkeys.DeriveRelyingParty(baseUrl);
        Assert.Equal("localhost", rpId);
        // De gangbare lokale poorten: vite dev, vite preview, adapter-node.
        Assert.Contains("http://localhost:5173", origins);
        Assert.All(origins, o => Assert.StartsWith("http://localhost:", o));
    }

    [Theory]
    [InlineData(0, 0, true)]    // authenticator zonder teller (platform-passkeys)
    [InlineData(0, 1, true)]    // teller start te lopen
    [InlineData(5, 6, true)]    // strikt stijgend
    [InlineData(5, 100, true)]  // sprongen zijn prima (teller is per credential)
    [InlineData(5, 5, false)]   // replay: exact dezelfde teller
    [InlineData(5, 4, false)]   // replay: teruggedraaid
    [InlineData(5, 0, false)]   // terugval naar 0 = kloon-signaal (de
                                // bibliotheek laat dit juist door — regressie)
    public void IsSignCountValid_RequiresStrictlyIncreasingCounter(
        long stored, long reported, bool valid)
    {
        Assert.Equal(valid, Passkeys.IsSignCountValid(stored, reported));
    }

    [Fact]
    public void IsChallengeUsable_ChecksKindAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var live = now.AddMinutes(1);
        var expired = now.AddSeconds(-1);

        Assert.True(Passkeys.IsChallengeUsable(Passkeys.LoginKind, Passkeys.LoginKind, live, now));
        // Een register-challenge mag nooit als login verzilverd worden (en
        // andersom): de opties horen bij precies één ceremonie-soort.
        Assert.False(Passkeys.IsChallengeUsable(Passkeys.LoginKind, Passkeys.RegisterKind, live, now));
        Assert.False(Passkeys.IsChallengeUsable(Passkeys.RegisterKind, Passkeys.LoginKind, live, now));
        Assert.False(Passkeys.IsChallengeUsable(Passkeys.LoginKind, Passkeys.LoginKind, expired, now));
        // Precies op de grens is verlopen (ExpiresAt > now, consistent met
        // de token-queries in UserAccountService).
        Assert.False(Passkeys.IsChallengeUsable(Passkeys.LoginKind, Passkeys.LoginKind, now, now));
    }

    [Fact]
    public void ChallengeTtl_IsShort()
    {
        // Een WebAuthn-ceremonie duurt seconden; de challenge hoort binnen
        // minuten waardeloos te zijn (geen dagenlang geldige opties in de db).
        Assert.InRange(Passkeys.ChallengeTtl, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void DefaultName_ContainsDate()
    {
        var name = Passkeys.DefaultName(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal("Passkey 12-07-2026", name);
    }
}
