using System.Security.Cryptography;
using System.Text;

namespace RbRules.Domain;

public enum QuotaVerdict { Allowed, Blocked, QuestionsExhausted, PhotosExhausted }

public record QuotaCheck(QuotaVerdict Verdict, string? Message)
{
    public bool Allowed => Verdict == QuotaVerdict.Allowed;
}

/// <summary>Pure account-logica (#42): e-mail-normalisatie, token-hygiëne en
/// de quota-beslissing. Geen I/O — de services leveren de tellingen aan.</summary>
public static class Accounts
{
    /// <summary>Normaliseert een e-mailadres (trim + lowercase) en valideert
    /// de vorm minimaal: precies één @, niet-lege local part, domein met punt.
    /// Null = ongeldig. Bewust geen volledige RFC-validatie (KISS) — de echte
    /// controle is dat de magic-link alleen op een werkend adres aankomt.</summary>
    public static string? NormalizeEmail(string? raw)
    {
        var email = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 254) return null;
        if (email.Any(char.IsWhiteSpace)) return null;
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return null;
        var domain = email[(at + 1)..];
        if (domain.Length < 3 || !domain.Contains('.') ||
            domain.StartsWith('.') || domain.EndsWith('.'))
            return null;
        return email;
    }

    /// <summary>Nieuw onvoorspelbaar token: 256 bits crypto-random, URL-veilig
    /// base64 (magic-link-tokens reizen in een query-parameter).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>SHA-256-hex van een token. De database bewaart nooit het token
    /// zelf: een gelekte tabel levert geen bruikbare links of sessies op.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>De quota-beslissing voor een ingelogde gebruiker (#42).
    /// countsQuestion = false voor LLM-routes die geen "vraag" zijn
    /// (explain/resolve/feedback): daar geldt alleen de blokkade-check —
    /// de rate-limiter blijft de rem op die routes.</summary>
    public static QuotaCheck CheckQuota(
        bool blocked, int dailyQuota, int dailyPhotoQuota,
        int questionsToday, int photosToday, bool countsQuestion, bool hasImage)
    {
        if (blocked)
            return new(QuotaVerdict.Blocked, "dit account is geblokkeerd door de beheerder");
        if (!countsQuestion)
            return new(QuotaVerdict.Allowed, null);
        // Foto-check vóór de algemene check: het foto-quotum is de krappere
        // (duurdere) limiet en verdient de specifiekere melding.
        if (hasImage && photosToday >= dailyPhotoQuota)
            return new(QuotaVerdict.PhotosExhausted,
                $"daglimiet voor foto-vragen bereikt ({dailyPhotoQuota} per dag) — stel de vraag eventueel zonder foto");
        if (questionsToday >= dailyQuota)
            return new(QuotaVerdict.QuestionsExhausted,
                $"daglimiet bereikt ({dailyQuota} vragen per dag) — morgen kan het weer");
        return new(QuotaVerdict.Allowed, null);
    }
}
