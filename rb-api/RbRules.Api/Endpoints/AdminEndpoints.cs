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
                    Kind = "cards", Ref = r.SourceLabel, Status = "ok",
                    Detail = $"{r.Sets} sets, {r.CardsSummary}",
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
                Detail = $"{r.Mined} gemined, {r.Failed} mislukt, {r.Remaining} resterend, " +
                         $"{r.NewCandidates} nieuwe keyword-kandidaten",
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
        // De jobdefinities zelf staan in JobCatalog (Infrastructure, #59).
        admin.MapPost("/jobs/{name}", (string name, JobRunner jobs) =>
        {
            var job = JobCatalog.Find(name);
            if (job is null) return Results.NotFound(new { error = $"onbekende job '{name}'" });
            return jobs.TryStart(name, job.Run)
                ? Results.Accepted("/api/admin/status", new { started = name })
                : Results.Conflict(new { error = "er draait al een job — wacht tot die klaar is" });
        });

        admin.MapGet("/status", async (JobRunner jobs, JobLedger ledger, RbRulesDbContext db) =>
        {
            var (running, last) = jobs.Snapshot();
            return Results.Ok(new
            {
                Running = running,
                LastJob = last,
                // Laatste afronding per job uit het run_log-grootboek (#122):
                // overleeft een herstart en toont ook de automatische runs
                // van de scheduler (relaties nachtelijk, scout wekelijks).
                JobRuns = await ledger.LastRunsAsync(),
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
                    Claims = await db.Claims.CountAsync(),
                    Relations = await db.Relations.CountAsync(),
                    MechanicCandidates = await db.MechanicKeywords.CountAsync(k => k.Status == "candidate"),
                    OpenProposals = await db.SourceProposals.CountAsync(p => p.Status == "proposed"),
                    Users = await db.Users.CountAsync(),
                    Decks = await db.Decks.CountAsync(),
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
            // #119: de hertoets-kanttekening leeft alleen zolang het doc op
            // review wacht — goedkeuren betekent "opnieuw gecontroleerd", dus
            // de reden gaat weg en de tekst is weer exact die van de embedding.
            doc.Body = KnowledgeRecheck.StripMarkers(doc.Body);
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
            // #119: hertoets-kanttekeningen zijn beheer-metadata, geen
            // doc-tekst — bewerken verwijdert ze (de beheerder hééft de reden
            // dan gezien), zodat een nieuwe embedding nooit een kanttekening
            // bevat.
            var body = patch.Body is null
                ? null : KnowledgeRecheck.StripMarkers(patch.Body).Trim();
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

        admin.MapGet("/overview/claims", async (
                string? status, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.ClaimsAsync(status, page ?? 1)));

        admin.MapGet("/overview/proposals", async (
                string? status, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.ProposalsAsync(status, page ?? 1)));

        // Relatievoorstellen (#116): status-chips + kind-vocabulaire + queue.
        admin.MapGet("/overview/relations", async (
                string? status, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.RelationsAsync(status, page ?? 1)));

        // Piltover Archive-decks (#15): attributie + deep-link per deck.
        admin.MapGet("/overview/decks", async (
                int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.DecksAsync(page ?? 1)));

        // Gebruikers + kosteninzicht (#42): LLM-gebruik per account per
        // periode, met de cheap/hard-verdeling als kosten-indicatie.
        admin.MapGet("/overview/users", async (
                string? period, int? page, AdminOverviewService overview) =>
            Results.Ok(await overview.UsersAsync(period, page ?? 1)));

        // Set-dekking (#145): per set de aanwezige én exact ontbrekende
        // basisnummers, afgeleid uit de riftbound-id's zelf ("ogn-074-298" =
        // 74 van 298). Vers berekend bij elke aanvraag.
        admin.MapGet("/overview/setcoverage", async (AdminOverviewService overview) =>
            Results.Ok(await overview.SetCoverageAsync()));

        // Judge-benchmark (#158): run-historie + het detail van de gekozen
        // (of meest recente) run — de job zelf draait via /jobs/benchmark.
        admin.MapGet("/overview/benchmark", async (long? run, AdminOverviewService overview) =>
            Results.Ok(await overview.BenchmarkAsync(run)));

        // Accountbeheer (#42): blokkeren en quota bijstellen.
        admin.MapPatch("/users/{id:long}", async (long id, UserPatch patch, RbRulesDbContext db) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            if (patch.DailyQuota is < 0 or > 10_000 || patch.DailyPhotoQuota is < 0 or > 10_000
                || patch.DailyAgenticQuota is < 0 or > 10_000)
                return Results.BadRequest(new { error = "quotum moet tussen 0 en 10000 liggen" });
            if (patch.Blocked is not null) user.Blocked = patch.Blocked.Value;
            if (patch.DailyQuota is not null) user.DailyQuota = patch.DailyQuota.Value;
            if (patch.DailyPhotoQuota is not null) user.DailyPhotoQuota = patch.DailyPhotoQuota.Value;
            if (patch.DailyAgenticQuota is not null) user.DailyAgenticQuota = patch.DailyAgenticQuota.Value;
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                user.Id, user.Email, user.Blocked, user.DailyQuota, user.DailyPhotoQuota,
                user.DailyAgenticQuota,
            });
        });

        // Bronvoorstellen-review (#63): accepteren zet de bron uitgeschakeld
        // in het register (veilige defaults — de beheerder zet hem daarna
        // bewust aan); verwerpen houdt de URL uit volgende scout-runs.
        // Een door de SSRF-guard geweigerde URL (#45) geeft een 422 met
        // { error } — de vorm die de adminApi-helper in rb-web als
        // foutmelding aan de beheerder toont.
        admin.MapPost("/proposals/{id:long}/accept", async (
                long id, SourceScoutService scout) =>
            await scout.AcceptAsync(id) switch
            {
                null => Results.NotFound(),
                { Status: "refused" } r => Results.UnprocessableEntity(new { error = r.Message }),
                var r => Results.Ok(r),
            });

        admin.MapPost("/proposals/{id:long}/reject", async (
                long id, SourceScoutService scout) =>
            await scout.RejectAsync(id) is { } r ? Results.Ok(r) : Results.NotFound());

        // Claims-review (#50): accepteren maakt een claim retrieval-baar
        // (het /ask-kanaal zelf is #51); verwerpen houdt hem uit beeld.
        // Beide nemen optioneel een beheerder-notitie mee (#124); bij
        // verwerpen is die notitie meteen de zichtbare reden bij het item.
        admin.MapPost("/claims/{id:long}/accept", async (
            long id, ReviewDecision? body, RbRulesDbContext db) =>
        {
            var claim = await db.Claims.FindAsync(id);
            if (claim is null) return Results.NotFound();
            claim.Status = "accepted";
            claim.StatusReason = null;
            if (!string.IsNullOrWhiteSpace(body?.Note)) claim.ReviewNote = body.Note.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/claims/{id:long}/reject", async (
            long id, ReviewDecision? body, RbRulesDbContext db) =>
        {
            var claim = await db.Claims.FindAsync(id);
            if (claim is null) return Results.NotFound();
            claim.Status = "rejected";
            if (!string.IsNullOrWhiteSpace(body?.Note)) claim.ReviewNote = body.Note.Trim();
            // Verwerpen was zwijgend (#124): met notitie is de reden de notitie.
            claim.StatusReason = claim.ReviewNote ?? "door de beheerder afgewezen";
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Archief (#124): gearchiveerd = uit de default-reviewweergave — puur
        // beheer-zicht; status (en dus /ask-deelname en graph-projectie)
        // verandert niet. Herstel kan altijd via de archief-chip.
        admin.MapPost("/claims/{id:long}/archive", async (long id, RbRulesDbContext db) =>
        {
            var claim = await db.Claims.FindAsync(id);
            if (claim is null) return Results.NotFound();
            claim.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/claims/{id:long}/unarchive", async (long id, RbRulesDbContext db) =>
        {
            var claim = await db.Claims.FindAsync(id);
            if (claim is null) return Results.NotFound();
            claim.ArchivedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // "Archiveer alle afgehandelde" (#124): alles waar de beheerder al
        // over besliste (of wat de pipeline zelf afwees) in één keer het
        // archief in; te-reviewen items blijven staan.
        admin.MapPost("/claims/archive-handled", async (RbRulesDbContext db) =>
        {
            var archived = await db.Claims
                .Where(c => c.ArchivedAt == null && c.Status != "unreviewed")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ArchivedAt, DateTimeOffset.UtcNow));
            return Results.Ok(new { archived });
        });

        // Notitie → geverifieerde ruling (#124): de beheerder-notitie wordt
        // een Correction (scope claim, ref = BrainRef) via het verify-pad,
        // zodat de uitleg voortaan antwoorden stuurt.
        admin.MapPost("/claims/{id:long}/promote-note", async (
                long id, ReviewDecision? body, ReviewNoteService notes) =>
            await notes.PromoteClaimNoteAsync(id, body?.Note) switch
            {
                { Status: PromoteNoteStatus.NotFound } => Results.NotFound(),
                { Status: PromoteNoteStatus.NoNote } => Results.BadRequest(
                    new { error = "geen notitie om door te zetten — schrijf er eerst één" }),
                var r => Results.Ok(new { ok = true, r.CorrectionId, r.Embedded, r.Updated }),
            });

        // Relatie-review (#116): accepteren maakt het voorstel definitief
        // (blijft/komt in de graph bij de volgende graph-sync, mits het kind
        // geaccepteerd is); verwerpen haalt hem uit de projectie én voorkomt
        // dat de miner hetzelfde voorstel opnieuw opvoert.
        admin.MapPost("/relations/{id:long}/accept", async (
            long id, ReviewDecision? body, RbRulesDbContext db) =>
        {
            var relation = await db.Relations.FindAsync(id);
            if (relation is null) return Results.NotFound();
            relation.Status = "accepted";
            relation.ReviewedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(body?.Note)) relation.ReviewNote = body.Note.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/relations/{id:long}/reject", async (
            long id, ReviewDecision? body, RbRulesDbContext db) =>
        {
            var relation = await db.Relations.FindAsync(id);
            if (relation is null) return Results.NotFound();
            relation.Status = "rejected";
            relation.ReviewedAt = DateTimeOffset.UtcNow;
            // Verwerp-reden (#124) — zichtbaar bij het item in de queue.
            if (!string.IsNullOrWhiteSpace(body?.Note)) relation.ReviewNote = body.Note.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Archief + notitie-promotie voor relaties (#124, claims-patroon).
        admin.MapPost("/relations/{id:long}/archive", async (long id, RbRulesDbContext db) =>
        {
            var relation = await db.Relations.FindAsync(id);
            if (relation is null) return Results.NotFound();
            relation.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/relations/{id:long}/unarchive", async (long id, RbRulesDbContext db) =>
        {
            var relation = await db.Relations.FindAsync(id);
            if (relation is null) return Results.NotFound();
            relation.ArchivedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/relations/archive-handled", async (RbRulesDbContext db) =>
        {
            var archived = await db.Relations
                .Where(r => r.ArchivedAt == null && r.Status != "unreviewed")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAt, DateTimeOffset.UtcNow));
            return Results.Ok(new { archived });
        });

        admin.MapPost("/relations/{id:long}/promote-note", async (
                long id, ReviewDecision? body, ReviewNoteService notes) =>
            await notes.PromoteRelationNoteAsync(id, body?.Note) switch
            {
                { Status: PromoteNoteStatus.NotFound } => Results.NotFound(),
                { Status: PromoteNoteStatus.NoNote } => Results.BadRequest(
                    new { error = "geen notitie om door te zetten — schrijf er eerst één" }),
                var r => Results.Ok(new { ok = true, r.CorrectionId, r.Embedded, r.Updated }),
            });

        // Kind-vocabulaire-review (#116, patroon mechanics): accepteren laat
        // relaties met dit kind meedoen in de graph-projectie (volgende
        // graph-sync); verwerpen houdt het kind — en nieuwe voorstellen
        // ermee — uit beeld.
        admin.MapPost("/relationkinds/{id:long}/accept", async (long id, RbRulesDbContext db) =>
        {
            var kind = await db.RelationKinds.FindAsync(id);
            if (kind is null) return Results.NotFound();
            kind.Status = "accepted";
            kind.ReviewedAt = DateTimeOffset.UtcNow;
            db.RunLogs.Add(new RunLog
            {
                Kind = "relations", Ref = $"kind:{kind.Kind}", Status = "ok",
                Detail = "kind geaccepteerd — relaties met dit kind gaan mee bij de volgende graph-sync",
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        admin.MapPost("/relationkinds/{id:long}/reject", async (long id, RbRulesDbContext db) =>
        {
            var kind = await db.RelationKinds.FindAsync(id);
            if (kind is null) return Results.NotFound();
            kind.Status = "rejected"; // blijft bewaard: wordt niet opnieuw voorgesteld
            kind.ReviewedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });
        // Kennis-gaten-rapport (#52): waar is de kennisbank dun — dekking,
        // vraag-signalen (lege retrieval/AI-uitval/negatieve feedback) en
        // bron-versheid. Vers berekend bij elke aanvraag.
        admin.MapGet("/overview/gaps", async (KnowledgeGapsService gaps, CancellationToken ct) =>
            Results.Ok(await gaps.BuildAsync(ct)));
        // Groeiend mechaniek-vocabulaire (#52): kandidaten uit de miner
        // reviewen. Accepteren = vocabulaire + re-mine van de betrokken
        // kaarten; verwerpen = term komt niet opnieuw de queue in.
        admin.MapGet("/mechanics", async (MechanicVocabularyService vocab) =>
            Results.Ok(await vocab.ListAsync()));
        admin.MapPost("/mechanics/{id:long}/accept", async (
                long id, MechanicVocabularyService vocab) =>
            await vocab.AcceptAsync(id) is { } r ? Results.Ok(r) : Results.NotFound());
        admin.MapPost("/mechanics/{id:long}/reject", async (
                long id, MechanicVocabularyService vocab) =>
            await vocab.RejectAsync(id) ? Results.Ok(new { ok = true }) : Results.NotFound());
        // Bewijs bij een kandidaat (#123): welke kaarten dragen de term, met
        // snippet — lazy opgevraagd bij het uitklappen in de admin.
        admin.MapGet("/mechanics/{id:long}/cards", async (
                long id, MechanicVocabularyService vocab, CancellationToken ct) =>
            await vocab.CardsForKeywordAsync(id, ct: ct) is { } cards
                ? Results.Ok(cards) : Results.NotFound());

        // Denkstappen-traces van de vraag-pipeline (#40). De lijst is slank;
        // het volledige gesprek (antwoord + eerdere beurten, #143) komt per
        // trace uit het detail — lazy bij het uitklappen in het beheer.
        admin.MapGet("/asktraces", async (AdminOverviewService overview) =>
            Results.Ok(await overview.AskTracesAsync()));
        admin.MapGet("/asktraces/{id:long}", async (
                long id, AdminOverviewService overview) =>
            await overview.AskTraceAsync(id) is { } t
                ? Results.Ok(t) : Results.NotFound());

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
