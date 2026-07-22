using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>Body voor een nieuwe tariefrij (#328): append-only — een
/// prijswijziging is een nieuwe rij met een nieuwe ingangsdatum, nooit een
/// update, zodat geboekte bedragen reproduceerbaar blijven.</summary>
public record TariffSubmit(
    string? Model, decimal? InputUsdPerMTok, decimal? OutputUsdPerMTok,
    DateTimeOffset? EffectiveFrom);

/// <summary>Kosten per gebruiker/job in beheer (#328): het live paneel leest
/// /api/admin/costs (rb-web pollt via zijn eigen proxy); de tarieventabel is
/// zonder deploy bij te werken via POST /api/admin/tariffs. Admin-gated met
/// dezelfde <see cref="AdminAuthFilter"/> als de rest van /api/admin;
/// endpoints dun — de aggregatie zit in <see cref="AiUsageReportService"/>.</summary>
public static class CostAdminEndpoints
{
    public static void MapCostAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

        admin.MapGet("/costs", async (
            string? period, AiUsageReportService report, CancellationToken ct) =>
            Results.Ok(await report.OverviewAsync(period, ct)));

        admin.MapPost("/tariffs", async (
            TariffSubmit body, RbRulesDbContext db, CancellationToken ct) =>
        {
            var model = body.Model?.Trim();
            if (string.IsNullOrEmpty(model) || model.Length > 128)
                return Results.BadRequest(new { error = "model is verplicht (max 128 tekens)" });
            if (body.InputUsdPerMTok is not (>= 0m and <= 100_000m)
                || body.OutputUsdPerMTok is not (>= 0m and <= 100_000m))
                return Results.BadRequest(new { error = "prijzen moeten tussen 0 en 100000 USD/MTok liggen" });

            var tariff = new AiTariff
            {
                Model = model,
                InputUsdPerMTok = body.InputUsdPerMTok.Value,
                OutputUsdPerMTok = body.OutputUsdPerMTok.Value,
                // Zonder datum geldt het tarief per direct; met datum is ook
                // een aangekondigde prijswijziging vooraf in te voeren.
                EffectiveFrom = body.EffectiveFrom ?? DateTimeOffset.UtcNow,
            };
            db.AiTariffs.Add(tariff);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true, tariff.Id });
        });
    }
}
