using System.Security.Cryptography;
using System.Text;

namespace RbRules.Domain;

/// <summary>Privacy-nette IP-koppeling voor de ask-geschiedenis (#157):
/// HMAC-SHA256 van het client-IP met een server-secret — nooit het rauwe IP
/// bewaren. Puur en deterministisch: zelfde IP + secret geeft altijd dezelfde
/// hash, zodat "zelfde apparaat/IP" herkenbaar is zonder het IP zelf op te
/// slaan. Zonder secret (ASK_IP_HASH_SECRET ontbreekt) of zonder IP geeft
/// <see cref="Hash"/> null — de aanroeper slaat dan bewust geen ip_hash op
/// (stille degradatie, nooit een crash).</summary>
public static class IpHashing
{
    public static string? Hash(string? ip, string? secret)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(secret)) return null;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexStringLower(bytes);
    }
}
