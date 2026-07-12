using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Scoped drager van de ingelogde gebruiker binnen één request (#42):
/// gezet door de quota-filter in de Api-laag, gelezen waar gebruik wordt
/// vastgelegd (AskService stempelt ask_metric/ask_trace). Null = anoniem.</summary>
public class RequestUserContext
{
    public AppUser? User { get; set; }
}

public record LoginRequestResult(
    bool Sent, string? Error, bool Unavailable = false, string? DevLink = null);
public record LoginVerifyResult(string SessionToken, string Email, DateTimeOffset ExpiresAt);
public record UsageToday(int Questions, int Photos);

/// <summary>Accounts met magic-link-login (#42): e-mail → eenmalige link →
/// sessie. Wachtwoorden bestaan hier bewust niet; de database bewaart alleen
/// token-hashes (Accounts.HashToken).</summary>
public class UserAccountService(RbRulesDbContext db, MailService mail, ILogger<UserAccountService> logger)
{
    private static readonly TimeSpan LoginTokenTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(90);

    /// <summary>Basis voor de link in de mail — bewust uit de omgeving en
    /// nooit uit request-invoer, zodat niemand phishing-links via onze
    /// mails kan sturen.</summary>
    private static string PublicBaseUrl =>
        Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")?.TrimEnd('/')
        ?? "https://riftbound-v2.bo3.dev";

    /// <summary>Maakt een eenmalige inloglink en mailt die. echoLink (alleen
    /// Development) geeft de link terug in het resultaat zodat de flow zonder
    /// mailserver te testen is.</summary>
    public async Task<LoginRequestResult> RequestLoginAsync(
        string rawEmail, bool echoLink = false, CancellationToken ct = default)
    {
        var email = Accounts.NormalizeEmail(rawEmail);
        if (email is null) return new(false, "geen geldig e-mailadres");
        if (!mail.Configured && !echoLink)
            return new(false,
                "inloggen is tijdelijk niet beschikbaar: er is geen mailvoorziening geconfigureerd",
                Unavailable: true);

        var now = DateTimeOffset.UtcNow;
        // Eén actieve link per adres; dit is meteen het natuurlijke
        // opruimmoment voor verlopen links en sessies.
        await db.LoginTokens
            .Where(t => t.Email == email || t.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var token = Accounts.NewToken();
        db.LoginTokens.Add(new LoginToken
        {
            Email = email,
            TokenHash = Accounts.HashToken(token),
            ExpiresAt = now.Add(LoginTokenTtl),
        });
        await db.SaveChangesAsync(ct);

        var link = $"{PublicBaseUrl}/account/verify?token={token}";
        if (mail.Configured)
        {
            try
            {
                await mail.SendAsync(email, "Inloggen bij Riftbound Rules",
                    "Log in bij Riftbound Rules Companion via deze link " +
                    $"({(int)LoginTokenTtl.TotalMinutes} minuten geldig):\n\n{link}\n\n" +
                    "Vroeg je dit niet aan? Dan kun je deze mail negeren.", ct);
            }
            catch (Exception ex)
            {
                // Mailuitval is een verwacht pad: nette fout voor de bezoeker,
                // detail in de log voor de beheerder.
                logger.LogWarning(ex, "Magic-link-mail versturen mislukt");
                return new(false, "de e-mail kon niet verstuurd worden — probeer het later opnieuw",
                    Unavailable: true, DevLink: echoLink ? link : null);
            }
        }
        return new(true, null, DevLink: echoLink ? link : null);
    }

    /// <summary>Verzilvert een magic-link: maakt het account aan als het nog
    /// niet bestond en start een sessie. Null = ongeldig/verlopen/gebruikt.</summary>
    public async Task<LoginVerifyResult?> VerifyLoginAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = Accounts.HashToken(token);
        var loginToken = await db.LoginTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
        if (loginToken is null) return null;
        loginToken.UsedAt = now;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == loginToken.Email, ct);
        if (user is null)
        {
            user = new AppUser { Email = loginToken.Email };
            db.Users.Add(user);
        }
        return await StartSessionAsync(user, ct);
    }

    /// <summary>Sessie-uitgifte, gedeeld door magic-link en passkeys (#109):
    /// elke geslaagde login-ceremonie krijgt exact dezelfde sessie (token-hash
    /// in de database, 90 dagen). SaveChanges hier rondt ook nog openstaande
    /// wijzigingen van de aanroeper af (zelfde scoped DbContext).</summary>
    public async Task<LoginVerifyResult> StartSessionAsync(AppUser user, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        user.LastLoginAt = now;
        var sessionToken = Accounts.NewToken();
        db.UserSessions.Add(new UserSession
        {
            User = user,
            TokenHash = Accounts.HashToken(sessionToken),
            ExpiresAt = now.Add(SessionTtl),
        });
        await db.SaveChangesAsync(ct);
        return new(sessionToken, user.Email, now.Add(SessionTtl));
    }

    /// <summary>Sessietoken → gebruiker; null = onbekend of verlopen.</summary>
    public async Task<AppUser?> ResolveSessionAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = Accounts.HashToken(token);
        return await db.UserSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.ExpiresAt > now)
            .Select(s => s.User)
            .FirstOrDefaultAsync(ct);
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        var hash = Accounts.HashToken(token);
        await db.UserSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    /// <summary>Gebruik van vandaag (UTC-dag) uit ask_metric — de teller
    /// waartegen de quota-filter handhaaft. Mislukte vragen tellen mee:
    /// simpel, en conservatief als abuse-rem.</summary>
    public async Task<UsageToday> UsageTodayAsync(long userId, CancellationToken ct = default)
    {
        var since = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var counts = await db.AskMetrics.AsNoTracking()
            .Where(m => m.UserId == userId && m.CreatedAt >= since)
            .GroupBy(m => 1)
            .Select(g => new { Questions = g.Count(), Photos = g.Count(m => m.HadImage) })
            .FirstOrDefaultAsync(ct);
        return new(counts?.Questions ?? 0, counts?.Photos ?? 0);
    }
}
