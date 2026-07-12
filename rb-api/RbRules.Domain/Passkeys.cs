namespace RbRules.Domain;

/// <summary>Pure passkey-logica (#109): relying-party-afleiding, de sign-count-
/// replay-beslissing en challenge-hygiëne. Geen I/O — PasskeyService levert de
/// opgeslagen waarden aan; de WebAuthn-cryptografie zelf doet fido2-net-lib.</summary>
public static class Passkeys
{
    /// <summary>Een WebAuthn-ceremonie is kort: opties ophalen en direct de
    /// authenticator gebruiken. Ruim genoeg voor een trage security key,
    /// krap genoeg dat rondslingerende challenges snel waardeloos zijn.</summary>
    public static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);

    public const string RegisterKind = "register";
    public const string LoginKind = "login";

    /// <summary>Leidt RP-id + toegestane origins af uit PUBLIC_BASE_URL
    /// (prod: riftbound-v2.bo3.dev — compose zet die env altijd). Zonder of
    /// met onbruikbare env: localhost-fallback voor dev (#109) — bewust anders
    /// dan de magic-link-fallback (die moet juist nooit localhost mailen),
    /// want een verkeerde RP-id maakt élke passkey onbruikbaar. De origins
    /// dekken de gangbare lokale poorten (vite dev/preview, adapter-node).</summary>
    public static (string RpId, IReadOnlySet<string> Origins) DeriveRelyingParty(string? publicBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && Uri.TryCreate(publicBaseUrl.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(uri.Host))
            return (uri.Host, new HashSet<string> { uri.GetLeftPart(UriPartial.Authority) });

        return ("localhost", new HashSet<string>
        {
            "http://localhost:5173", "http://localhost:4173", "http://localhost:3000",
        });
    }

    /// <summary>Replay-check op de handtekeningteller (WebAuthn §6.1.1).
    /// Beide nul = authenticator zonder teller (gangbaar bij platform-
    /// passkeys), dat is prima. In alle andere gevallen moet de teller strikt
    /// stijgen — ook een terugval naar 0 is een kloon-signaal (fido2-net-lib
    /// keurt alleen dalende niet-nul-tellers af, dit dekt de rest).</summary>
    public static bool IsSignCountValid(long stored, long reported) =>
        (stored == 0 && reported == 0) || reported > stored;

    /// <summary>Eén beslispunt voor challenge-geldigheid: juiste ceremonie-
    /// soort én niet verlopen. De service verwijdert de challenge sowieso
    /// (single-use), dit bepaalt alleen of hij nog verzilverd mag worden.</summary>
    public static bool IsChallengeUsable(
        string expectedKind, string kind, DateTimeOffset expiresAt, DateTimeOffset now) =>
        kind == expectedKind && expiresAt > now;

    /// <summary>Standaardnaam voor een nieuwe passkey — de gebruiker ziet in
    /// het overzicht toch al de aanmaakdatum, dus dit hoeft alleen twee keys
    /// van elkaar te onderscheiden.</summary>
    public static string DefaultName(DateTimeOffset createdAt) =>
        $"Passkey {createdAt:dd-MM-yyyy}";
}
