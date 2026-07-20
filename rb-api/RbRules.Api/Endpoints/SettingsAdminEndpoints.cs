using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Beheerde instellingen (#254) onder <c>/api/admin/settings</c>: de
/// feature-vlaggen die tot nu toe alleen via de VM-<c>.env</c> + een herstart te
/// zetten waren. Admin-gated met dezelfde <see cref="AdminAuthFilter"/> als de rest
/// van /api/admin. Endpoints dun → alle logica (validatie, audit, cache-invalidatie)
/// zit in <see cref="ManagedSettingsService"/>.</summary>
public static class SettingsAdminEndpoints
{
    public static void MapSettingsAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

        // Lezen: per catalogus-sleutel de effectieve waarde, de env-default en
        // wanneer/door wie er overheen is geschreven.
        admin.MapGet("/settings", async (ManagedSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.ListAsync(ct)));

        // Schrijven: één of meer sleutels ALS GEHEEL (het nachtvenster is een paar —
        // los toegepast zou een geldige eindtoestand op de eerste stap stranden).
        // Value leeg/null = terug naar de env-/codewaarde. Een geweigerde waarde geeft
        // 400 mét uitleg en schrijft niets — nooit een stilzwijgend genegeerde
        // schakelaar.
        admin.MapPost("/settings", async (
            SettingsPatch patch, ManagedSettingsService settings, CancellationToken ct) =>
        {
            if (patch.Changes is not { Count: > 0 })
                return Results.Problem(statusCode: 400, title: "Instelling niet gewijzigd",
                    detail: "Geen instelling opgegeven.");

            var result = await settings.SetManyAsync(
                [.. patch.Changes.Select(c => new SettingAssignment(c.Key ?? "", c.Value))],
                patch.Actor ?? "beheer", ct);
            return result.Ok
                ? Results.Ok(result)
                : Results.Problem(statusCode: 400, title: "Instelling niet gewijzigd",
                    detail: result.Error);
        });
    }
}
