using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure IP-hashing-logica (#157): HMAC-SHA256 van het client-IP met
/// een server-secret — nooit het rauwe IP. Determinisme is het hele punt
/// (zelfde IP + secret moet altijd matchen), en het ontbreken van
/// secret/IP moet degraderen naar null, nooit een crash.</summary>
public class IpHashingTests
{
    private const string Secret = "test-secret-uit-env";

    [Fact]
    public void Hash_IsDeterministic_ZelfdeIpEnSecretGeeftZelfdeHash()
    {
        Assert.Equal(
            IpHashing.Hash("203.0.113.5", Secret),
            IpHashing.Hash("203.0.113.5", Secret));
    }

    [Fact]
    public void Hash_VerschillendIp_GeeftVerschillendeHash()
    {
        Assert.NotEqual(
            IpHashing.Hash("203.0.113.5", Secret),
            IpHashing.Hash("203.0.113.6", Secret));
    }

    [Fact]
    public void Hash_VerschillendSecret_GeeftVerschillendeHash()
    {
        // Het IP zelf staat nooit onversleuteld in het resultaat — een ander
        // secret levert een compleet andere hash op hetzelfde IP.
        Assert.NotEqual(
            IpHashing.Hash("203.0.113.5", Secret),
            IpHashing.Hash("203.0.113.5", "een-ander-secret"));
    }

    [Fact]
    public void Hash_GeeftHexEncodedSha256Lengte()
    {
        var hash = IpHashing.Hash("203.0.113.5", Secret);
        Assert.NotNull(hash);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_ZonderSecret_GeeftNull_StilleDegradatie(string? secret)
    {
        Assert.Null(IpHashing.Hash("203.0.113.5", secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_ZonderIp_GeeftNull(string? ip)
    {
        Assert.Null(IpHashing.Hash(ip, Secret));
    }
}
