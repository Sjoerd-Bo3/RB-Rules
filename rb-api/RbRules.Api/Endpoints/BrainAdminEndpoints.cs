using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>De Brein-verkenner & inspectie (#236, inzicht-thread) onder
/// /api/admin/brein/*. READ-ONLY en ADDITIEF: puur leesbare projecties over de
/// bestaande brein-tabellen, admin-gated met dezelfde <see cref="AdminAuthFilter"/>
/// (X-Admin-Key) als de rest van /api/admin. Géén schrijf-pad, géén mining/reasoner/
/// retrieval-executie (aparte increments), geen live-Neo4j-afhankelijkheid — alles
/// komt uit Postgres (de SoT voor de ABox). Endpoints dun → logica in
/// <see cref="BrainExplorerService"/> (docs/CONVENTIONS.md).</summary>
public static class BrainAdminEndpoints
{
    public static void MapBrainAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var brein = app.MapGroup("/api/admin/brein").AddEndpointFilter<AdminAuthFilter>();

        // ── Overzicht: tegel-tellingen per tabel ───────────────────────
        brein.MapGet("/overzicht", async (BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.OverviewAsync(ct)));

        // ── Cockpit: operationele pipeline-status (per-stap-tellingen,
        //    laatste-run per brein-job, /ask-retrieval-flag). READ-ONLY; de
        //    flag komt sinds #254 uit de beheerde instellingen (DB-override op
        //    de env-default) en is dus een échte knop — zie /api/admin/settings.
        brein.MapGet("/cockpit", async (
                BrainExplorerService svc, ManagedSettingsService settings, CancellationToken ct) =>
            Results.Ok(await svc.CockpitAsync(
                (await settings.BreinRetrievalAsync(ct)).Enabled, ct)));

        // ── Entiteiten: canoniek + alt-labels + merge-status ───────────
        brein.MapGet("/entities", async (
                string? kind, string? status, int? page, BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.EntitiesAsync(kind, status, page ?? 1, ct)));

        // ── Interacties: condities + tier + provenance-anker ───────────
        brein.MapGet("/interactions", async (
                string? status, int? page, BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.InteractionsAsync(status, page ?? 1, ct)));

        // ── Assertions: de provenance-keten van één feit-ref ───────────
        // Catch-all ({**refText}): section-/card-refs bevatten een slash en
        // rb-web stuurt refs URL-ge-encodeerd — beide vormen op dezelfde keten.
        brein.MapGet("/assertions/{**refText}", async (
                string refText, BrainExplorerService svc, CancellationToken ct) =>
        {
            var chain = await svc.ProvenanceChainAsync(refText, ct);
            return chain is null
                ? Results.Problem(statusCode: 400, title: "ongeldige ref",
                    detail: $"'{refText}' is geen geldige BrainRef (verwacht kind:key, " +
                            "bv. interaction:42 of assertion:<ulid>).")
                : Results.Ok(chain);
        });

        // ── Conflicts: reasoning-tegenspraken + routering ──────────────
        brein.MapGet("/conflicts", async (
                string? status, int? page, BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.ConflictsAsync(status, page ?? 1, ct)));

        // ── AnswerTrace: lijst (viewer-picker) + herspeelbaar detail ───
        brein.MapGet("/answertraces", async (BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.AnswerTracesAsync(ct)));

        brein.MapGet("/answertrace/{id}", async (
                string id, BrainExplorerService svc, CancellationToken ct) =>
        {
            var trace = await svc.AnswerTraceAsync(id, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        // ── Observability: fase-7 rollups ──────────────────────────────
        brein.MapGet("/observability", async (BrainExplorerService svc, CancellationToken ct) =>
            Results.Ok(await svc.ObservabilityAsync(ct)));
    }
}
