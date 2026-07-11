using Microsoft.EntityFrameworkCore;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class PushEndpoints
{
    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Web-push (#28): meldingen bij belangrijke wijzigingen ──────
        app.MapGet("/api/push/vapid", (PushService push) =>
            push.Enabled
                ? Results.Ok(new { publicKey = push.PublicKey })
                : Results.NotFound(new { error = "push niet geconfigureerd (VAPID-keys ontbreken)" }));

        app.MapPost("/api/push/subscribe", async (PushSubscribe body, RbRulesDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(body.Endpoint) ||
                string.IsNullOrWhiteSpace(body.P256dh) || string.IsNullOrWhiteSpace(body.Auth))
                return Results.BadRequest(new { error = "endpoint, p256dh en auth zijn verplicht" });
            // SSRF-guard: alleen echte https-push-endpoints, geen interne adressen.
            if (!Uri.TryCreate(body.Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.IsLoopback ||
                System.Net.IPAddress.TryParse(uri.Host, out _))
                return Results.BadRequest(new { error = "ongeldig push-endpoint" });

            var existing = await db.PushSubscriptions.FindAsync(body.Endpoint);
            if (existing is null)
            {
                db.PushSubscriptions.Add(new RbRules.Domain.PushSubscription
                {
                    Endpoint = body.Endpoint, P256dh = body.P256dh, Auth = body.Auth,
                });
            }
            else
            {
                existing.P256dh = body.P256dh;
                existing.Auth = body.Auth;
            }
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* dubbele gelijktijdige subscribe — al geregistreerd */ }
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/push/unsubscribe", async (PushUnsubscribe body, RbRulesDbContext db) =>
        {
            await db.PushSubscriptions.Where(s => s.Endpoint == body.Endpoint).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        });
    }
}
