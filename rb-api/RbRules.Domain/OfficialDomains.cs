namespace RbRules.Domain;

/// <summary>Officiële Riot-domeinen voor Riftbound (#167). Een bron-feed met
/// AutoApprove mag nieuwe artikelen alléén dírect als enabled trust-1 official
/// <see cref="Source"/> aanmaken wanneer de feed (én het artikel) op zo'n
/// domein staat; elke andere host routeert naar de reviewqueue
/// (<see cref="SourceProposal"/>) — "nooit automatisch aan" voor
/// niet-geverifieerde domeinen, consistent met <c>SourceScoutService.AcceptAsync</c>
/// (Enabled=false) en <c>HubDiscovery</c> (logt alleen kandidaten).
///
/// playriftbound.com is HÉT Riot-domein (docs/CLAUDE.md); riftbound.
/// leagueoflegends.com is de legacy-variant die pad-behoudend naar
/// playriftbound.com 301't (<see cref="HubDiscovery"/>, <see cref="UrlGuard"/>).
/// Bewust géén partner-/community-domeinen (uvsgames.com, riftbound.gg): die
/// zijn trust 2/3 en horen dus nooit langs de auto-approve-route, ongeacht een
/// AutoApprove-vinkje.</summary>
public static class OfficialDomains
{
    /// <summary>Basisdomeinen; een host matcht exact óf als subdomein
    /// (news.playriftbound.com), nooit via een substring-truc
    /// (playriftbound.com.evil.example) — dat laatste eindigt niet op
    /// ".playriftbound.com".</summary>
    private static readonly string[] Official =
    [
        "playriftbound.com",
        "riftbound.leagueoflegends.com",
    ];

    /// <summary>Is deze host een officieel Riot-domein of subdomein daarvan?
    /// Case- en trailing-dot-ongevoelig; leeg/onbruikbaar ⇒ false (nooit "bij
    /// twijfel officieel").</summary>
    public static bool IsOfficialHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return Official.Any(d =>
            h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>Is deze URL een https-URL op een officieel Riot-domein? Alleen
    /// https telt (zelfde eis als <see cref="UrlGuard"/>): een http-URL is
    /// nooit "officieel genoeg" voor de auto-approve-route.</summary>
    public static bool IsOfficialUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && IsOfficialHost(uri.Host);
}
