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
    }
}
