using System.Net;
using System.Net.Mail;

namespace RbRules.Infrastructure;

/// <summary>Minimale SMTP-mailer voor de magic-link-login (#42). Het project
/// had geen mailvoorziening; configuratie via env (SMTP_HOST, SMTP_PORT,
/// SMTP_USER, SMTP_PASS, SMTP_FROM). Niet geconfigureerd = inloggen geeft
/// een nette 503; de rest van de site blijft gewoon werken.</summary>
public class MailService
{
    private readonly string? _host = Environment.GetEnvironmentVariable("SMTP_HOST");
    private readonly int _port =
        int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
    private readonly string? _user = Environment.GetEnvironmentVariable("SMTP_USER");
    private readonly string? _pass = Environment.GetEnvironmentVariable("SMTP_PASS");
    private readonly string? _from = Environment.GetEnvironmentVariable("SMTP_FROM");

    public bool Configured => !string.IsNullOrEmpty(_host) && !string.IsNullOrEmpty(_from);

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("SMTP is niet geconfigureerd");
        // SmtpClient is niet thread-safe; login-mail is laag volume, dus een
        // verse client per verzending is de simpelste veilige vorm.
        using var client = new SmtpClient(_host!, _port)
        {
            EnableSsl = true,
            Credentials = string.IsNullOrEmpty(_user) ? null : new NetworkCredential(_user, _pass),
        };
        using var message = new MailMessage(_from!, to, subject, body);
        await client.SendMailAsync(message, ct);
    }
}
