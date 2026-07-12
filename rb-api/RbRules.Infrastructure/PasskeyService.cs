using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record PasskeyCeremony(string Token, string OptionsJson);
public record PasskeyBeginResult(PasskeyCeremony? Ceremony, string? Error);
/// <summary>Session is null bij de extra-passkey-flow (gebruiker was al
/// ingelogd — de bestaande sessie blijft gewoon geldig).</summary>
public record PasskeyRegisterResult(LoginVerifyResult? Session, string? Error)
{
    public bool Ok => Error is null;
}
public record PasskeyInfo(long Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>WebAuthn-ceremonies (#109): registratie en login met passkeys via
/// fido2-net-lib. Challenges zijn kort geldig en single-use (login_token-
/// hygiëne); na een geslaagde ceremonie geeft UserAccountService.
/// StartSessionAsync exact dezelfde sessie uit als de magic-link-verify —
/// quota en kosteninzicht (#42) merken geen verschil.</summary>
public class PasskeyService(
    RbRulesDbContext db, UserAccountService accounts, ILogger<PasskeyService> logger)
{
    /// <summary>Fido2 per aanroep opgebouwd: goedkoop, en zo volgt de RP-id
    /// altijd de actuele PUBLIC_BASE_URL (afleiding: Passkeys.DeriveRelyingParty).</summary>
    private static Fido2 CreateFido2()
    {
        var (rpId, origins) = Passkeys.DeriveRelyingParty(
            Environment.GetEnvironmentVariable("PUBLIC_BASE_URL"));
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = rpId,
            ServerName = "Riftbound Rules",
            Origins = origins,
        });
    }

    /// <summary>Start een registratie-ceremonie. Zonder userId: nieuw account
    /// op de gekozen identifier — een bestaand adres wordt geweigerd, want een
    /// passkey ongeauthenticeerd aan een bestaand account koppelen zou dat
    /// account overneembaar maken (er is geen mail-bevestiging). Met userId:
    /// extra passkey bij het eigen, al ingelogde account.</summary>
    public async Task<PasskeyBeginResult> BeginRegistrationAsync(
        string rawEmail, long? userId = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Natuurlijk opruimmoment voor verlopen ceremonies (zie RequestLoginAsync).
        await db.PasskeyChallenges.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct);

        string email;
        byte[] userHandle;
        List<PublicKeyCredentialDescriptor> exclude = [];
        if (userId is { } uid)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
            if (user is null) return new(null, "onbekend account");
            // Stabiele handle per account: zo groepeert de authenticator alle
            // passkeys van dit account en overschrijft een tweede registratie
            // op hetzelfde apparaat de eerste in plaats van te stapelen.
            user.PasskeyHandle ??= RandomNumberGenerator.GetBytes(32);
            email = user.Email;
            userHandle = user.PasskeyHandle;
            exclude = (await db.PasskeyCredentials.AsNoTracking()
                    .Where(c => c.UserId == uid)
                    .Select(c => c.CredentialId)
                    .ToListAsync(ct))
                .Select(id => new PublicKeyCredentialDescriptor(id))
                .ToList();
        }
        else
        {
            var normalized = Accounts.NormalizeEmail(rawEmail);
            if (normalized is null) return new(null, "geen geldig e-mailadres");
            if (await db.Users.AnyAsync(u => u.Email == normalized, ct))
                return new(null,
                    "dit e-mailadres heeft al een account — log in en voeg daar een passkey toe");
            email = normalized;
            userHandle = RandomNumberGenerator.GetBytes(32);
        }

        var options = CreateFido2().RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userHandle, Name = email, DisplayName = email },
            ExcludeCredentials = exclude,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Discoverable credential vereist: dan werkt inloggen met één
                // knop, zonder eerst een identifier te hoeven typen.
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return new(await StoreChallengeAsync(
            Passkeys.RegisterKind, options.ToJson(), email, userId, now, ct), null);
    }

    /// <summary>Verzilvert een registratie-ceremonie: verifieert het
    /// authenticator-antwoord, slaat de credential op en start een sessie
    /// (behalve bij de extra-passkey-flow — daar loopt al een sessie).</summary>
    public async Task<PasskeyRegisterResult> FinishRegistrationAsync(
        string token, AuthenticatorAttestationRawResponse response, CancellationToken ct = default)
    {
        var challenge = await ConsumeChallengeAsync(Passkeys.RegisterKind, token, ct);
        if (challenge is null)
            return new(null, "de registratie is verlopen of al gebruikt — probeer het opnieuw");

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);
        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await CreateFido2().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (p, innerCt) =>
                    !await db.PasskeyCredentials.AnyAsync(c => c.CredentialId == p.CredentialId, innerCt),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            // Verwacht pad (verkeerde origin, kapot antwoord): detail in de
            // log voor de beheerder, nette melding voor de bezoeker.
            logger.LogWarning(ex, "Passkey-registratie geweigerd");
            return new(null, "de passkey kon niet geverifieerd worden — probeer het opnieuw");
        }

        AppUser? user;
        if (challenge.UserId is { } uid)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
            if (user is null) return new(null, "onbekend account");
        }
        else
        {
            // Herhaalcheck van BeginRegistrationAsync: het adres kan tussen de
            // twee stappen in geclaimd zijn (de unieke index vangt de race af,
            // dit geeft er een nette melding voor).
            if (await db.Users.AnyAsync(u => u.Email == challenge.Email, ct))
                return new(null,
                    "dit e-mailadres heeft al een account — log in en voeg daar een passkey toe");
            user = new AppUser { Email = challenge.Email!, PasskeyHandle = options.User.Id };
            db.Users.Add(user);
        }

        db.PasskeyCredentials.Add(new PasskeyCredential
        {
            User = user,
            CredentialId = registered.Id,
            PublicKey = registered.PublicKey,
            SignCount = registered.SignCount,
            Aaguid = registered.AaGuid,
            Name = Passkeys.DefaultName(DateTimeOffset.UtcNow),
        });

        if (challenge.UserId is not null)
        {
            await db.SaveChangesAsync(ct);
            return new(null, null);
        }
        // Nieuw ingelogd: exact dezelfde sessie-uitgifte als de magic-link.
        return new(await accounts.StartSessionAsync(user, ct), null);
    }

    /// <summary>Start een login-ceremonie. Bewust zonder allowCredentials:
    /// discoverable credentials laten de authenticator zelf het account
    /// kiezen (één knop), en er lekt geen credential-lijst per e-mailadres.</summary>
    public async Task<PasskeyCeremony> BeginLoginAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.PasskeyChallenges.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var options = CreateFido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });
        return await StoreChallengeAsync(
            Passkeys.LoginKind, options.ToJson(), email: null, userId: null, now, ct);
    }

    /// <summary>Verzilvert een login-ceremonie. Null = ongeldig (onbekende
    /// credential, verlopen challenge, kapotte handtekening of replay) — de
    /// endpoint maakt daar één generieke foutmelding van.</summary>
    public async Task<LoginVerifyResult?> FinishLoginAsync(
        string token, AuthenticatorAssertionRawResponse response, CancellationToken ct = default)
    {
        var challenge = await ConsumeChallengeAsync(Passkeys.LoginKind, token, ct);
        if (challenge is null) return null;

        var options = AssertionOptions.FromJson(challenge.OptionsJson);
        var credential = await db.PasskeyCredentials.Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == response.RawId, ct);
        if (credential?.User is null) return null;

        VerifyAssertionResult result;
        try
        {
            result = await CreateFido2().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(
                    credential.User.PasskeyHandle is { } handle
                    && handle.AsSpan().SequenceEqual(p.UserHandle)),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            logger.LogWarning(ex, "Passkey-login geweigerd");
            return null;
        }

        // Replay-check bovenop de bibliotheek: die keurt alleen dalende
        // niet-nul-tellers af; een terugval naar 0 is óók een kloon-signaal.
        if (!Passkeys.IsSignCountValid(credential.SignCount, result.SignCount))
        {
            logger.LogWarning(
                "Passkey-login geweigerd: sign-count ging niet vooruit ({Stored} -> {Reported}) — mogelijk gekloonde credential",
                credential.SignCount, result.SignCount);
            return null;
        }

        credential.SignCount = result.SignCount;
        credential.LastUsedAt = DateTimeOffset.UtcNow;
        return await accounts.StartSessionAsync(credential.User, ct);
    }

    public async Task<List<PasskeyInfo>> ListAsync(long userId, CancellationToken ct = default)
    {
        return await db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyInfo(c.Id, c.Name, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(ct);
    }

    /// <summary>Verwijdert een eigen passkey. Ook de laatste mag weg — de UI
    /// waarschuwt dat het account dan onbereikbaar is zolang er geen
    /// mail-herstel bestaat (#109), maar het blijft het account van de
    /// gebruiker zelf.</summary>
    public async Task<bool> DeleteAsync(long userId, long passkeyId, CancellationToken ct = default)
    {
        return await db.PasskeyCredentials
            .Where(c => c.Id == passkeyId && c.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private async Task<PasskeyCeremony> StoreChallengeAsync(
        string kind, string optionsJson, string? email, long? userId,
        DateTimeOffset now, CancellationToken ct)
    {
        var token = Accounts.NewToken();
        db.PasskeyChallenges.Add(new PasskeyChallenge
        {
            TokenHash = Accounts.HashToken(token),
            Kind = kind,
            Email = email,
            UserId = userId,
            OptionsJson = optionsJson,
            ExpiresAt = now.Add(Passkeys.ChallengeTtl),
        });
        await db.SaveChangesAsync(ct);
        return new PasskeyCeremony(token, optionsJson);
    }

    /// <summary>Haalt een challenge op en verwijdert hem meteen (single-use,
    /// ook bij een mislukte ceremonie). Null = onbekend, verkeerde soort of
    /// verlopen (Passkeys.IsChallengeUsable).</summary>
    private async Task<PasskeyChallenge?> ConsumeChallengeAsync(
        string expectedKind, string token, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = Accounts.HashToken(token);
        var challenge = await db.PasskeyChallenges.FirstOrDefaultAsync(c => c.TokenHash == hash, ct);
        if (challenge is null) return null;
        db.PasskeyChallenges.Remove(challenge);
        await db.SaveChangesAsync(ct);
        return Passkeys.IsChallengeUsable(expectedKind, challenge.Kind, challenge.ExpiresAt, now)
            ? challenge
            : null;
    }
}
