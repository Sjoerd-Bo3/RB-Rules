using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Admin-only facade over rb-ai's private control plane. This is kept
/// separate from ManagedSettings because account credentials are write-only
/// operational secrets, not application settings.</summary>
public static class AiConfigAdminEndpoints
{
    public static void MapAiConfigAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var ai = app.MapGroup("/api/admin/ai-config")
            .AddEndpointFilter<AdminAuthFilter>();

        ai.MapGet("", async (AiControlClient client, CancellationToken ct) =>
            ToResult(await client.GetSnapshotAsync(ct)));

        ai.MapPost("/pools", async (
            AiPoolCreateRequest request, AiControlClient client, CancellationToken ct) =>
        {
            if (request.Validate() is { } error) return Invalid(error);
            return ToResult(await client.CreatePoolAsync(request, ct), created: true);
        });

        ai.MapPatch("/pools/{id}", async (
            string id, AiPoolPatchRequest request, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige pool-id.");
            if (request.Validate() is { } error) return Invalid(error);
            return ToResult(await client.PatchPoolAsync(id, request, ct));
        });

        ai.MapDelete("/pools/{id}", async (
            string id, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige pool-id.");
            return ToNoContent(await client.DeletePoolAsync(id, ct));
        });

        ai.MapPost("/accounts", async (
            AiAccountCreateRequest request, AiControlClient client, CancellationToken ct) =>
        {
            if (request.Validate() is { } error) return Invalid(error);
            return ToResult(await client.CreateAccountAsync(request, ct), created: true);
        });

        ai.MapPatch("/accounts/{id}", async (
            string id, AiAccountPatchRequest request, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige account-id.");
            if (request.Validate() is { } error) return Invalid(error);
            return ToResult(await client.PatchAccountAsync(id, request, ct));
        });

        ai.MapDelete("/accounts/{id}", async (
            string id, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige account-id.");
            return ToNoContent(await client.DeleteAccountAsync(id, ct));
        });

        // The credential travels once from the admin POST body to rb-ai. Neither
        // this endpoint nor AiControlClient renders, logs, audits, or returns it.
        ai.MapPut("/accounts/{id}/credential", async (
            string id, AiCredentialReplaceRequest request,
            AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige account-id.");
            if (request.Validate() is { } error) return Invalid(error);
            return ToResult(await client.ReplaceCredentialAsync(id, request, ct));
        });

        ai.MapPost("/accounts/{id}/test", async (
            string id, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige account-id.");
            return ToResult(await client.TestAccountAsync(id, ct));
        });

        ai.MapPost("/accounts/{id}/device-login", async (
            string id, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(id)) return Invalid("Ongeldige account-id.");
            return ToResult(await client.StartDeviceLoginAsync(id, ct));
        });

        ai.MapGet("/device-login/{sessionId}", async (
            string sessionId, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(sessionId)) return Invalid("Ongeldige login-sessie.");
            return ToResult(await client.GetDeviceLoginAsync(sessionId, ct));
        });

        ai.MapDelete("/device-login/{sessionId}", async (
            string sessionId, AiControlClient client, CancellationToken ct) =>
        {
            if (!ValidId(sessionId)) return Invalid("Ongeldige login-sessie.");
            return ToNoContent(await client.CancelDeviceLoginAsync(sessionId, ct));
        });
    }

    internal static IResult ToResult<T>(AiControlResult<T> result, bool created = false) =>
        result.Ok
            ? Results.Json(result.Value, statusCode: created
                ? StatusCodes.Status201Created
                : StatusCodes.Status200OK)
            : Failure(result.Failure);

    internal static IResult ToNoContent(AiControlResult<bool> result) =>
        result.Ok ? Results.NoContent() : Failure(result.Failure);

    private static IResult Failure(AiControlFailure failure) => failure switch
    {
        AiControlFailure.InvalidRequest => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "AI-configuratie geweigerd",
            detail: "rb-ai heeft de configuratiewijziging geweigerd."),
        AiControlFailure.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "AI-configuratie niet gevonden"),
        AiControlFailure.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "AI-configuratieconflict",
            detail: "De wijziging botst met de actuele AI-configuratie."),
        _ => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "AI-beheer niet beschikbaar",
            detail: "De interne rb-ai-controlplane is niet bereikbaar of niet geconfigureerd."),
    };

    private static IResult Invalid(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Ongeldige AI-configuratie",
        detail: detail);

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
