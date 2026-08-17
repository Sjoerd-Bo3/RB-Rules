using System.Net;
using System.Net.Sockets;

namespace RbRules.Domain;

public record UrlGuardResult(bool Allowed, string? Reason)
{
    public static readonly UrlGuardResult Ok = new(true, null);
    public static UrlGuardResult Blocked(string reason) => new(false, reason);
}

/// <summary>SSRF-guard voor externe fetches (#45). Register-URL's komen deels
/// van webvondsten (scout #63, hub-ontdekking #94) en beheerder-invoer; PDF-
/// links worden per run uit hub-HTML geplukt. Zonder guard kan zo'n URL naar
/// compose-interne diensten wijzen (rb-v2-postgres, neo4j, ollama — geen van
/// alle met auth op het interne netwerk) of naar cloud-metadata.
///
/// Twee pure bouwstenen, elk op een eigen rand aangeroepen:
/// - <see cref="Check"/>: URL-regels vóór de fetch (alleen https, geen
///   letterlijke IP's, geen localhost/interne namen) — IngestService en het
///   accepteren van bronvoorstellen.
/// - <see cref="IsBlockedIp"/>: IP-regels ná DNS-resolutie — de fetch-laag
///   (SafeExternalHttp) checkt hiermee élk verbindingsdoel, ook redirect-hops,
///   op het moment van verbinden (geen check-dan-fetch-gat / DNS-rebinding).
/// </summary>
public static class UrlGuard
{
    /// <summary>Redirect-limiet voor externe fetches: ruim genoeg voor
    /// legacy-domein-301's (riftbound.leagueoflegends.com → playriftbound.com)
    /// plus een tracking-hop of twee, krap genoeg tegen redirect-kettingen.</summary>
    public const int MaxRedirects = 5;

    /// <summary>Pure URL-validatie (geen I/O — DNS doet de fetch-laag).
    /// Toegestaan: https naar een publieke DNS-naam met domein.</summary>
    public static UrlGuardResult Check(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return UrlGuardResult.Blocked("geen geldige absolute URL");

        if (uri.Scheme != Uri.UriSchemeHttps)
            return UrlGuardResult.Blocked("alleen https is toegestaan");

        // user@host is legitiem maar in de praktijk alleen een verwarrings-
        // truc ("https://playriftbound.com@evil.example/") — weiger het.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return UrlGuardResult.Blocked("userinfo (gebruikersnaam@) in de URL is niet toegestaan");

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || IPAddress.TryParse(host.Trim('[', ']'), out _))
            return UrlGuardResult.Blocked("letterlijke IP-adressen zijn niet toegestaan");

        if (host.Length == 0 || uri.HostNameType != UriHostNameType.Dns)
            return UrlGuardResult.Blocked("onbruikbare hostnaam");

        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
            return UrlGuardResult.Blocked("localhost is niet toegestaan");

        // Zonder domein-punt is het een interne netwerknaam — precies de vorm
        // van de compose-servicenamen (rb-v2-postgres, neo4j, ollama).
        if (!host.Contains('.'))
            return UrlGuardResult.Blocked("hostnaam zonder domein (interne netwerknaam?)");

        if (host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".home.arpa", StringComparison.Ordinal))
            return UrlGuardResult.Blocked("interne hostnaam (.local/.internal/.home.arpa) is niet toegestaan");

        return UrlGuardResult.Ok;
    }

    /// <summary>Puur IP-predicaat voor ná DNS-resolutie: loopback, privé,
    /// link-local (cloud-metadata!), CGNAT, multicast/reserved en de IPv6-
    /// tegenhangers. IPv4-mapped IPv6 wordt eerst teruggevouwen naar IPv4.
    /// Onbekende adresfamilies zijn geblokkeerd (nooit "bij twijfel door").</summary>
    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                 // 0.0.0.0/8
                || b[0] == 10                                // 10.0.0.0/8 privé
                || (b[0] == 100 && (b[1] & 0xC0) == 64)      // 100.64.0.0/10 CGNAT
                || (b[0] == 169 && b[1] == 254)              // 169.254.0.0/16 link-local/metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)      // 172.16.0.0/12 privé (docker-netwerken)
                || (b[0] == 192 && b[1] == 168)              // 192.168.0.0/16 privé
                || (b[0] == 198 && (b[1] & 0xFE) == 18)      // 198.18.0.0/15 benchmark
                || b[0] >= 224;                              // multicast/reserved/broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if (b.All(x => x == 0)) return true;             // :: (unspecified)
            return (b[0] & 0xFE) == 0xFC                     // fc00::/7 ULA
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)   // fe80::/10 link-local
                || (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // 64:ff9b::/96 NAT64
                || b[0] == 0xFF;                             // ff00::/8 multicast
        }

        return true;
    }
}
