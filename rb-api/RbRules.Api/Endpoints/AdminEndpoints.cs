using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Beheer (X-Admin-Key) ───────────────────────────────────────
        var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthFilter>();

        admin.MapGet("/ping", () => Results.Ok(new { ok = true }));

        admin.MapPost("/scan", async (string? sourceId, IngestService ingest) =>
            Results.Ok(await ingest.ScanAsync(onlyDue: false, sourceId)));

        admin.MapPost("/cards/sync", async (CardSyncService cards, RbRulesDbContext db) =>
        {
            try
            {
                var r = await cards.SyncAsync();
                db.RunLogs.Add(new RunLog
                {
                    Kind = "cards", Ref = r.Source, Status = "ok",
                    Detail = $"{r.Sets} sets, {r.Cards} kaarten",
                });
                await db.SaveChangesAsync();
                return Results.Ok(r);
            }
            catch (Exception ex)
            {
                // Nooit een kale 500 — de fout hoort zichtbaar te zijn in admin/logs.
                db.ChangeTracker.Clear();
                db.RunLogs.Add(new RunLog { Kind = "cards", Ref = "sync", Status = "error", Detail = ex.Message });
                await db.SaveChangesAsync();
                return Results.Problem(title: "Kaarten-sync mislukt", detail: ex.Message, statusCode: 502);
            }
        });

        admin.MapPost("/cards/embed", async (
            bool? force, CardEmbeddingPipeline pipeline, RbRulesDbContext db) =>
        {
            try
            {
                var r = await pipeline.RunAsync(force ?? false);
                db.RunLogs.Add(new RunLog
                {
                    Kind = "embed", Ref = "cards", Status = "ok",
                    Detail = $"{r.Embedded} geembed, {r.Skipped} al actueel",
                });
                await db.SaveChangesAsync();
                return Results.Ok(r);
            }
            catch (Exception ex)
            {
                db.RunLogs.Add(new RunLog { Kind = "embed", Ref = "cards", Status = "error", Detail = ex.Message });
                await db.SaveChangesAsync();
                return Results.Problem(ex.Message);
            }
        });

        admin.MapPost("/cards/mine", async (
            int? maxBatches, MechanicMiningService mining, RbRulesDbContext db) =>
        {
            var r = await mining.RunAsync(Math.Clamp(maxBatches ?? 25, 1, 200));
            db.RunLogs.Add(new RunLog
            {
                Kind = "mine", Ref = "mechanics", Status = r.Failed > 0 ? "info" : "ok",
                Detail = $"{r.Mined} gemined, {r.Failed} mislukt, {r.Remaining} resterend",
            });
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });

        admin.MapPost("/graph/sync", async (GraphSyncService graph, RbRulesDbContext db) =>
        {
            try
            {
                var r = await graph.SyncAsync();
                db.RunLogs.Add(new RunLog
                {
                    Kind = "graph", Ref = null, Status = "ok",
                    Detail = $"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics",
                });
                await db.SaveChangesAsync();
                return Results.Ok(r);
            }
            catch (Exception ex)
            {
                db.RunLogs.Add(new RunLog { Kind = "graph", Ref = null, Status = "error", Detail = ex.Message });
                await db.SaveChangesAsync();
                return Results.Problem(ex.Message);
            }
        });

        admin.MapPost("/rules/index", async (RuleChunkPipeline pipeline, RbRulesDbContext db) =>
        {
            try
            {
                var results = await pipeline.RunAsync();
                var total = results.Sum(r => r.Chunks);
                db.RunLogs.Add(new RunLog
                {
                    Kind = "embed", Ref = "rules", Status = "ok",
                    Detail = $"{results.Count} bronnen, {total} sectie-chunks",
                });
                await db.SaveChangesAsync();
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                db.RunLogs.Add(new RunLog { Kind = "embed", Ref = "rules", Status = "error", Detail = ex.Message });
                await db.SaveChangesAsync();
                return Results.Problem(ex.Message);
            }
        });

        admin.MapPost("/bans/sync", async (BanErrataSyncService sync, RbRulesDbContext db) =>
        {
            var r = await sync.SyncAsync();
            db.RunLogs.Add(new RunLog
            {
                Kind = "bans", Ref = null, Status = r.Bans + r.Errata > 0 ? "ok" : "info",
                Detail = $"{r.Bans} bans, {r.Errata} errata gestructureerd",
            });
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });

        admin.MapPost("/interactions/mine", async (
            int? max, InteractionService interactions, RbRulesDbContext db) =>
        {
            var r = await interactions.MineAsync(Math.Clamp(max ?? 60, 1, 300));
            db.RunLogs.Add(new RunLog
            {
                Kind = "mine", Ref = "interactions", Status = "ok",
                Detail = $"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd",
            });
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });

        // ── Levendige admin: async jobs + live status ──────────────────
        admin.MapPost("/jobs/{name}", (string name, JobRunner jobs) =>
        {
            Func<IServiceProvider, Action<string>, CancellationToken, Task<string>>? work = name switch
            {
                // Eén knop voor alles: elke stap best-effort in de juiste volgorde —
                // een haperende stap (Ollama/LLM even weg) stopt de rest niet.
                "all" => async (sp, report, ct) =>
                {
                    var results = new List<string>();
                    async Task Step(string label, Func<Task<string>> run)
                    {
                        report($"{results.Count + 1}/8 · {label}");
                        try { results.Add($"{label}: {await run()}"); }
                        catch (Exception ex) { results.Add($"{label}: FOUT — {ex.Message}"); }
                    }

                    await Step("kaarten", async () =>
                    {
                        var r = await sp.GetRequiredService<CardSyncService>().SyncAsync(
                            p => report($"1/8 · kaarten — {p}"), ct);
                        return $"{r.Cards} kaarten via {r.Source}";
                    });
                    await Step("bronnen scannen", async () =>
                    {
                        var r = await sp.GetRequiredService<IngestService>().ScanAsync(
                            onlyDue: false, progress: p => report($"2/8 · scan — {p}"), ct: ct);
                        return string.Join(", ", r.Select(x => $"{x.SourceId}={x.Status}"));
                    });
                    await Step("regels indexeren", async () =>
                    {
                        var r = await sp.GetRequiredService<RuleChunkPipeline>().RunAsync(
                            force: false, p => report($"3/8 · regels — {p}"), ct);
                        return $"{r.Sum(x => x.Chunks)} chunks";
                    });
                    await Step("bans/errata", async () =>
                    {
                        var r = await sp.GetRequiredService<BanErrataSyncService>().SyncAsync(ct);
                        return $"{r.Bans} bans, {r.Errata} errata";
                    });
                    await Step("embeddings", async () =>
                    {
                        var r = await sp.GetRequiredService<CardEmbeddingPipeline>().RunAsync(
                            progress: p => report($"5/8 · embeddings — {p}"), ct: ct);
                        return $"{r.Embedded} geembed";
                    });
                    await Step("mechanieken", async () =>
                    {
                        var r = await sp.GetRequiredService<MechanicMiningService>().RunAsync(
                            progress: p => report($"6/8 · mechanieken — {p}"), ct: ct);
                        return $"{r.Mined} gemined, {r.Remaining} resterend";
                    });
                    await Step("graph", async () =>
                    {
                        var r = await sp.GetRequiredService<GraphSyncService>().SyncAsync(ct);
                        return $"{r.Cards} cards";
                    });
                    await Step("interacties", async () =>
                    {
                        var r = await sp.GetRequiredService<InteractionService>().MineAsync(
                            progress: p => report($"8/8 · interacties — {p}"), ct: ct);
                        return $"{r.Verified} geverifieerd";
                    });
                    return string.Join(" · ", results);
                },
                "scan" => async (sp, report, ct) =>
                {
                    var scanStart = DateTimeOffset.UtcNow;
                    var r = await sp.GetRequiredService<IngestService>()
                        .ScanAsync(onlyDue: false, progress: report, ct: ct);
                    // Ook handmatige scans sturen pushmeldingen bij high-severity.
                    try
                    {
                        await sp.GetRequiredService<PushService>().NotifyHighSeverityAsync(
                            sp.GetRequiredService<RbRulesDbContext>(), scanStart, ct);
                    }
                    catch
                    {
                        // push is best-effort
                    }
                    return string.Join(", ", r.Select(x => $"{x.SourceId}={x.Status}"));
                },
                "cards" => async (sp, report, ct) =>
                {
                    var r = await sp.GetRequiredService<CardSyncService>().SyncAsync(report, ct);
                    return $"{r.Sets} sets, {r.Cards} kaarten via {r.Source}";
                },
                "embed" => async (sp, report, ct) =>
                {
                    var r = await sp.GetRequiredService<CardEmbeddingPipeline>()
                        .RunAsync(progress: report, ct: ct);
                    return $"{r.Embedded} kaarten geembed, {r.Skipped} al actueel";
                },
                "mine" => async (sp, report, ct) =>
                {
                    var r = await sp.GetRequiredService<MechanicMiningService>()
                        .RunAsync(progress: report, ct: ct);
                    return $"{r.Mined} kaarten gemined, {r.Remaining} resterend";
                },
                "rules" => async (sp, report, ct) =>
                {
                    // Handmatige run = volledige herbouw, zodat parser-verbeteringen
                    // ook op bestaande documenten landen.
                    var r = await sp.GetRequiredService<RuleChunkPipeline>()
                        .RunAsync(force: true, report, ct);
                    return $"{r.Sum(x => x.Chunks)} sectie-chunks over {r.Count} bronnen (herbouwd)";
                },
                "bans" => async (sp, report, ct) =>
                {
                    report("officiële documenten structureren via LLM");
                    var r = await sp.GetRequiredService<BanErrataSyncService>().SyncAsync(ct);
                    return $"{r.Bans} bans, {r.Errata} errata gestructureerd";
                },
                "graph" => async (sp, report, ct) =>
                {
                    report("kaarten, domeinen, tags en mechanieken naar Neo4j schrijven");
                    var r = await sp.GetRequiredService<GraphSyncService>().SyncAsync(ct);
                    return $"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics";
                },
                "primer" => async (sp, report, ct) =>
                {
                    var r = await sp.GetRequiredService<PrimerService>()
                        .GenerateAsync(progress: report, ct: ct);
                    return $"{r.Written} primer-docs geschreven (drafts), {r.Skipped} goedgekeurd gelaten, {r.Failed} mislukt";
                },
                "interactions" => async (sp, report, ct) =>
                {
                    var r = await sp.GetRequiredService<InteractionService>()
                        .MineAsync(progress: report, ct: ct);
                    return $"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd";
                },
                _ => null,
            };
            if (work is null) return Results.NotFound(new { error = $"onbekende job '{name}'" });
            return jobs.TryStart(name, work)
                ? Results.Accepted("/api/admin/status", new { started = name })
                : Results.Conflict(new { error = "er draait al een job — wacht tot die klaar is" });
        });

        admin.MapGet("/status", async (JobRunner jobs, RbRulesDbContext db) =>
        {
            var (running, last) = jobs.Snapshot();
            return Results.Ok(new
            {
                Running = running,
                LastJob = last,
                Counts = new
                {
                    Sources = await db.Sources.CountAsync(s => s.Enabled),
                    Changes = await db.Changes.CountAsync(),
                    Cards = await db.Cards.CountAsync(),
                    CardsEmbedded = await db.Cards.CountAsync(c => c.Embedding != null),
                    CardsMined = await db.Cards.CountAsync(c => c.Mechanics != null),
                    RuleChunks = await db.RuleChunks.CountAsync(),
                    Bans = await db.BanEntries.CountAsync(),
                    Errata = await db.Errata.CountAsync(),
                    Interactions = await db.CardInteractions.CountAsync(),
                    OpenCorrections = await db.Corrections.CountAsync(c => c.Status == "unverified"),
                    Knowledge = await db.KnowledgeDocs.CountAsync(),
                },
                Logs = await db.RunLogs.OrderByDescending(l => l.CreatedAt).Take(15).ToListAsync(),
            });
        });

        admin.MapGet("/logs", async (string? kind, RbRulesDbContext db) =>
        {
            var query = db.RunLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(l => l.Kind == kind);
            return await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync();
        });

        admin.MapPost("/sources", async (Source src, RbRulesDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(src.Id) || string.IsNullOrWhiteSpace(src.Url))
                return Results.BadRequest(new { error = "id en url zijn verplicht" });
            db.Sources.Add(src);
            await db.SaveChangesAsync();
            return Results.Created($"/api/sources/{src.Id}", src);
        });

        admin.MapPatch("/sources/{id}", async (string id, SourcePatch patch, RbRulesDbContext db) =>
        {
            var src = await db.Sources.FindAsync(id);
            if (src is null) return Results.NotFound();
            if (patch.Name is not null) src.Name = patch.Name;
            if (patch.Url is not null) src.Url = patch.Url;
            if (patch.TrustTier is not null) src.TrustTier = patch.TrustTier.Value;
            if (patch.Rank is not null) src.Rank = patch.Rank.Value;
            if (patch.Cadence is not null) src.Cadence = patch.Cadence;
            if (patch.Enabled is not null) src.Enabled = patch.Enabled.Value;
            await db.SaveChangesAsync();
            return Results.Ok(src);
        });

        admin.MapDelete("/sources/{id}", async (string id, RbRulesDbContext db) =>
        {
            var src = await db.Sources.FindAsync(id);
            if (src is null) return Results.NotFound();
            // FK's zijn cascade/set-null geconfigureerd (audit-fix) — geen wees-rijen.
            await db.Documents.Where(d => d.SourceId == id).ExecuteDeleteAsync();
            await db.Changes.Where(c => c.SourceId == id).ExecuteDeleteAsync();
            db.Sources.Remove(src);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Feed-curatie: ruis (bijv. oude flip-flop-spam) handmatig kunnen opruimen.
        admin.MapDelete("/changes/{id:long}", async (long id, RbRulesDbContext db) =>
        {
            var c = await db.Changes.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Changes.Remove(c);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Primer-kennisdocs (kennislaag 1, #49): reviewen en goedkeuren.
        admin.MapGet("/knowledge", async (RbRulesDbContext db) =>
            await db.KnowledgeDocs.AsNoTracking()
                .OrderBy(k => k.Topic)
                .Select(k => new
                {
                    k.Id, k.Kind, k.Topic, k.Title, k.Body,
                    k.SectionRefs, k.Status, k.UpdatedAt,
                })
                .ToListAsync());

        admin.MapPost("/knowledge/{id:long}/approve", async (long id, RbRulesDbContext db) =>
        {
            var doc = await db.KnowledgeDocs.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Status = "approved";
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Intrekken (#70): terug naar draft — het doc doet dan niet meer mee
        // in de /ask-context (AskService filtert op Status == "approved")
        // tot her-goedkeuring.
        admin.MapPost("/knowledge/{id:long}/unapprove", async (long id, RbRulesDbContext db) =>
        {
            var doc = await db.KnowledgeDocs.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Status = "draft";
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Beheerder-bewerking (#70): titel/tekst corrigeren zonder
        // her-generatie. Status blijft wat hij was: een bewerkte approved
        // blijft approved, een bewerkte draft blijft draft.
        admin.MapPatch("/knowledge/{id:long}", async (
            long id, KnowledgePatch patch, RbRulesDbContext db, EmbeddingService embeddings) =>
        {
            var doc = await db.KnowledgeDocs.FindAsync(id);
            if (doc is null) return Results.NotFound();
            var title = patch.Title?.Trim();
            var body = patch.Body?.Trim();
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
                return Results.BadRequest(new { error = "titel of tekst is verplicht" });

            var changed = (!string.IsNullOrEmpty(title) && title != doc.Title)
                       || (!string.IsNullOrEmpty(body) && body != doc.Body);
            if (!string.IsNullOrEmpty(title)) doc.Title = title;
            if (!string.IsNullOrEmpty(body)) doc.Body = body;
            if (changed)
            {
                try
                {
                    // Zelfde embed-input als PrimerService, zodat /ask de
                    // bewerkte versie direct semantisch vindt.
                    doc.Embedding = await embeddings.EmbedOneAsync($"{doc.Title}\n{doc.Body}");
                    doc.EmbeddingModel = EmbeddingConfig.Model;
                }
                catch
                {
                    // Ollama tijdelijk weg — opslaan telt (zelfde patroon als
                    // corrections/verify). De oude embedding hoort bij de oude
                    // tekst: liever géén embedding (het doc doet even niet mee
                    // in /ask) dan een stille mismatch; embedding volgt bij de
                    // volgende bewerking.
                    doc.Embedding = null;
                    doc.EmbeddingModel = null;
                }
            }
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                doc.Id, doc.Kind, doc.Topic, doc.Title, doc.Body,
                doc.SectionRefs, doc.Status, doc.UpdatedAt,
                Embedded = doc.Embedding != null,
            });
        });

        admin.MapDelete("/knowledge/{id:long}", async (long id, RbRulesDbContext db) =>
        {
            var doc = await db.KnowledgeDocs.FindAsync(id);
            if (doc is null) return Results.NotFound();
            db.KnowledgeDocs.Remove(doc);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // ── Tegel-overzichten (#61): elke dashboard-tegel klikt door ──────
        admin.MapGet("/overview/cards", async (
                string? filter, string? q, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.CardsAsync(filter, q, page ?? 1)));

        admin.MapGet("/overview/rulechunks", async (
                string? sourceId, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.RuleChunksAsync(sourceId, page ?? 1)));

        admin.MapGet("/overview/bans", async (AdminOverviewService overview) =>
            Results.Ok(await overview.BansAsync()));

        admin.MapGet("/overview/errata", async (AdminOverviewService overview) =>
            Results.Ok(await overview.ErrataAsync()));

        admin.MapGet("/overview/interactions", async (
                int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.InteractionsAsync(page ?? 1)));

        admin.MapGet("/overview/changes", async (
                int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.ChangesAsync(page ?? 1)));

        // Denkstappen-traces van de vraag-pipeline (#40).
        admin.MapGet("/asktraces", async (RbRulesDbContext db) =>
            await db.AskTraces.AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .Take(30)
                .ToListAsync());

        // Projectie zonder Embedding — 1024 floats per rij horen niet in JSON.
        admin.MapGet("/corrections", async (RbRulesDbContext db) =>
            await db.Corrections.AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .Take(200)
                .Select(c => new
                {
                    c.Id, c.Scope, c.Ref, c.Text, c.Question,
                    c.Provenance, c.Status, c.CreatedAt, c.VerifiedAt,
                })
                .ToListAsync());

        admin.MapPost("/corrections/{id:long}/verify", async (
            long id, RbRulesDbContext db, EmbeddingService embeddings) =>
        {
            var c = await db.Corrections.FindAsync(id);
            if (c is null) return Results.NotFound();
            c.Status = "verified";
            c.VerifiedAt = DateTimeOffset.UtcNow;
            try
            {
                // Embedding op vraag+correctie zodat /ask de ruling semantisch vindt.
                c.Embedding = await embeddings.EmbedOneAsync($"{c.Question}\n{c.Text}");
            }
            catch
            {
                // Ollama tijdelijk weg — verificatie telt, embedding volgt bij her-verify.
            }
            await db.SaveChangesAsync();
            return Results.Ok(c);
        });

        admin.MapDelete("/corrections/{id:long}", async (long id, RbRulesDbContext db) =>
        {
            var c = await db.Corrections.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Corrections.Remove(c);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });
    }
}
