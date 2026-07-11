using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebPush;

namespace RbRules.Infrastructure;

/// <summary>Web-push (#28): meldingen bij high-severity wijzigingen (bans,
/// errata, regelwijzigingen). VAPID-keys komen uit de omgeving; zonder keys
/// is push uitgeschakeld en doen subscribe-endpoints niets.</summary>
public class PushService(ILogger<PushService> logger)
{
    public string? PublicKey { get; } = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
    private readonly string? _privateKey = Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
    private readonly string _subject =
        Environment.GetEnvironmentVariable("VAPID_SUBJECT") ?? "mailto:beheer@bo3.dev";

    public bool Enabled => !string.IsNullOrEmpty(PublicKey) && !string.IsNullOrEmpty(_privateKey);

    public async Task<int> SendToAllAsync(
        RbRulesDbContext db, string title, string body, string url, CancellationToken ct = default)
    {
        if (!Enabled) return 0;
        var subs = await db.PushSubscriptions.ToListAsync(ct);
        if (subs.Count == 0) return 0;

        var vapid = new VapidDetails(_subject, PublicKey, _privateKey);
        using var client = new WebPushClient();
        var payload = JsonSerializer.Serialize(new { title, body, url });

        var sent = 0;
        foreach (var s in subs)
        {
            try
            {
                await client.SendNotificationAsync(
                    new PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, vapid, ct);
                sent++;
            }
            catch (WebPushException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                db.PushSubscriptions.Remove(s); // abonnement bestaat niet meer
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push naar {Endpoint} mislukt", s.Endpoint[..Math.Min(40, s.Endpoint.Length)]);
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Abonnement is ondertussen al (elders) verwijderd — prima.
        }
        return sent;
    }
}
