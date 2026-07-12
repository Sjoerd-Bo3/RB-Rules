using System.Text.Json;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Accounts met magic-link (#42): e-mail → link → sessie ─────────
        // Succes is altijd hetzelfde generieke antwoord, of het adres nou al
        // een account had of niet — geen account-enumeratie. devLink komt
        // alleen in Development terug (flow testen zonder mailserver).
        app.MapPost("/api/auth/request", async (
            AuthRequestDto body, UserAccountService accounts, IWebHostEnvironment env) =>
        {
            var r = await accounts.RequestLoginAsync(body.Email ?? "", echoLink: env.IsDevelopment());
            if (r.Unavailable)
                return Results.Problem(title: "Inloggen niet beschikbaar",
                    detail: r.Error, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (r.Error is not null) return Results.BadRequest(new { error = r.Error });
            return Results.Ok(new { ok = true, devLink = r.DevLink });
        }).RequireRateLimiting("auth");

        app.MapPost("/api/auth/verify", async (AuthVerifyDto body, UserAccountService accounts) =>
            string.IsNullOrWhiteSpace(body.Token)
                ? Results.BadRequest(new { error = "token ontbreekt" })
                : await accounts.VerifyLoginAsync(body.Token) is { } r
                    ? Results.Ok(new { token = r.SessionToken, email = r.Email, expiresAt = r.ExpiresAt })
                    : Results.BadRequest(new { error = "de inloglink is ongeldig of verlopen — vraag een nieuwe aan" }))
            .RequireRateLimiting("auth");

        // Wie ben ik + verbruik vandaag — voedt de accountpagina in rb-web.
        app.MapGet("/api/auth/me", async (HttpRequest req, UserAccountService accounts) =>
        {
            var token = req.Headers[UserQuotaFilter.TokenHeader].FirstOrDefault();
            var user = string.IsNullOrEmpty(token) ? null : await accounts.ResolveSessionAsync(token);
            if (user is null) return Results.Unauthorized();
            var usage = await accounts.UsageTodayAsync(user.Id);
            return Results.Ok(new
            {
                user.Email, user.Blocked, user.DailyQuota, user.DailyPhotoQuota,
                QuestionsToday = usage.Questions, PhotosToday = usage.Photos,
                user.CreatedAt,
            });
        });

        app.MapPost("/api/auth/logout", async (HttpRequest req, UserAccountService accounts) =>
        {
            var token = req.Headers[UserQuotaFilter.TokenHeader].FirstOrDefault();
            if (!string.IsNullOrEmpty(token)) await accounts.LogoutAsync(token);
            return Results.Ok(new { ok = true });
        });

        // ── Passkeys (#109): WebAuthn als primaire login, geen mail nodig ──
        // Eigen "webauthn"-rate-limit: publiek en dus begrensd, maar ruimer
        // dan de mail-route — elke ceremonie kost twee requests.
        app.MapPost("/api/auth/passkey/register/options", async (
            PasskeyRegisterOptionsDto body, HttpRequest req,
            UserAccountService accounts, PasskeyService passkeys) =>
        {
            // Mét geldige sessie wordt dit "extra passkey bij eigen account".
            var user = await ResolveUserAsync(req, accounts);
            var r = await passkeys.BeginRegistrationAsync(body.Email ?? "", user?.Id);
            return r.Error is not null
                ? Results.BadRequest(new { error = r.Error })
                : CeremonyResponse(r.Ceremony!);
        }).RequireRateLimiting("webauthn");

        app.MapPost("/api/auth/passkey/register/verify", async (
            PasskeyRegisterVerifyDto body, PasskeyService passkeys) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token) || body.Response is null)
                return Results.BadRequest(new { error = "onvolledig registratie-antwoord" });
            var r = await passkeys.FinishRegistrationAsync(body.Token, body.Response);
            if (!r.Ok) return Results.BadRequest(new { error = r.Error });
            // Sessie-shape identiek aan /api/auth/verify; token is null bij de
            // extra-passkey-flow (daar loopt de bestaande sessie gewoon door).
            return Results.Ok(new
            {
                ok = true,
                token = r.Session?.SessionToken,
                email = r.Session?.Email,
                expiresAt = r.Session?.ExpiresAt,
            });
        }).RequireRateLimiting("webauthn");

        app.MapPost("/api/auth/passkey/login/options", async (PasskeyService passkeys) =>
            CeremonyResponse(await passkeys.BeginLoginAsync()))
            .RequireRateLimiting("webauthn");

        app.MapPost("/api/auth/passkey/login/verify", async (
            PasskeyLoginVerifyDto body, PasskeyService passkeys) =>
            string.IsNullOrWhiteSpace(body.Token) || body.Response is null
                ? Results.BadRequest(new { error = "onvolledig login-antwoord" })
                : await passkeys.FinishLoginAsync(body.Token, body.Response) is { } s
                    ? Results.Ok(new { token = s.SessionToken, email = s.Email, expiresAt = s.ExpiresAt })
                    : Results.BadRequest(new { error = "de passkey kon niet geverifieerd worden — probeer het opnieuw" }))
            .RequireRateLimiting("webauthn");

        // Beheer van eigen passkeys — voedt de accountpagina in rb-web.
        app.MapGet("/api/auth/passkeys", async (
            HttpRequest req, UserAccountService accounts, PasskeyService passkeys) =>
        {
            var user = await ResolveUserAsync(req, accounts);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await passkeys.ListAsync(user.Id));
        });

        app.MapDelete("/api/auth/passkeys/{id:long}", async (
            long id, HttpRequest req, UserAccountService accounts, PasskeyService passkeys) =>
        {
            var user = await ResolveUserAsync(req, accounts);
            if (user is null) return Results.Unauthorized();
            return await passkeys.DeleteAsync(user.Id, id)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { error = "passkey niet gevonden" });
        });
    }

    private static async Task<AppUser?> ResolveUserAsync(HttpRequest req, UserAccountService accounts)
    {
        var token = req.Headers[UserQuotaFilter.TokenHeader].FirstOrDefault();
        return string.IsNullOrEmpty(token) ? null : await accounts.ResolveSessionAsync(token);
    }

    /// <summary>Ceremonie-antwoord: het challenge-token voor de verify-stap
    /// plus de WebAuthn-opties als echte JSON (fido2-net-lib serialiseert die
    /// zelf al in de vorm die navigator.credentials verwacht).</summary>
    private static IResult CeremonyResponse(PasskeyCeremony ceremony) => Results.Ok(new
    {
        token = ceremony.Token,
        options = JsonSerializer.Deserialize<JsonElement>(ceremony.OptionsJson),
    });
}
