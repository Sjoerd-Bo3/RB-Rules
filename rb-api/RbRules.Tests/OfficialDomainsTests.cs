using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Officiële-domein-allowlist (#167): de gate waarachter feed-
/// AutoApprove een artikel direct als enabled trust-1 official bron mag zetten.
/// Host-match, subdomein-match en géén substring-truc (playriftbound.com.evil).</summary>
public class OfficialDomainsTests
{
    [Theory]
    [InlineData("playriftbound.com")]
    [InlineData("PlayRiftbound.com")]                 // case-ongevoelig
    [InlineData("playriftbound.com.")]                // trailing dot (FQDN)
    [InlineData("news.playriftbound.com")]            // subdomein
    [InlineData("en-us.cdn.playriftbound.com")]       // diep subdomein
    [InlineData("riftbound.leagueoflegends.com")]     // legacy Riot-domein
    public void IsOfficialHost_AcceptsOfficialAndSubdomains(string host) =>
        Assert.True(OfficialDomains.IsOfficialHost(host));

    [Theory]
    [InlineData("playriftbound.com.evil.example")]    // substring-truc
    [InlineData("evilplayriftbound.com")]             // geen puntgrens
    [InlineData("leagueoflegends.com")]               // parent van het legacy-domein telt niet
    [InlineData("riftbound.gg")]                      // community
    [InlineData("uvsgames.com")]                      // partner (trust 2), nooit auto-official
    [InlineData("youtube.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsOfficialHost_RejectsEverythingElse(string? host) =>
        Assert.False(OfficialDomains.IsOfficialHost(host));

    [Theory]
    [InlineData("https://playriftbound.com/en-us/news/rules-and-releases/x")]
    [InlineData("https://news.playriftbound.com/x")]
    [InlineData("https://riftbound.leagueoflegends.com/en-us/news/announcements/y")]
    public void IsOfficialUrl_AcceptsHttpsOnOfficialHost(string url) =>
        Assert.True(OfficialDomains.IsOfficialUrl(url));

    [Theory]
    [InlineData("http://playriftbound.com/x")]        // http telt nooit
    [InlineData("https://playriftbound.com.evil.example/x")]
    [InlineData("https://uvsgames.com/x")]
    [InlineData("ftp://playriftbound.com/x")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOfficialUrl_RejectsNonHttpsOrNonOfficial(string? url) =>
        Assert.False(OfficialDomains.IsOfficialUrl(url));

    [Fact]
    public void IsOfficialUrl_RejectsUserInfoLookAlike()
    {
        // "https://playriftbound.com@evil.example/" — de host is evil.example,
        // niet playriftbound.com; Uri parseert de host correct.
        Assert.False(OfficialDomains.IsOfficialUrl("https://playriftbound.com@evil.example/x"));
    }
}
