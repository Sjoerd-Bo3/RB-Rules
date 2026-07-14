using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class AskEndpoints
{
    /// <summary>Zelfde casing als Results.Ok (camelCase) voor de NDJSON-frames.</summary>
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    public static void MapAskEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Rulings-Q&A (S2): hybrid retrieval + §-citaten ─────────────
        app.MapPost("/api/ask", async (AskRequest req, AskService ask) =>
        {
            if (ValidateAsk(req, out var images, out var history) is { } bad) return bad;
            // De duurmeting voor de "gemiddeld ±Xs"-indicatie zit in AskService.
            // Approach (#153): parse valt veilig op Auto terug; of de keuze
            // gehonoreerd wordt beslist AskService (alleen ingelogd).
            var result = await ask.AskAsync(
                req.Question.Trim(), images.Count > 0 ? images : null,
                history.Count > 0 ? history : null,
                AgenticGate.ParseApproach(req.Approach));
            return Results.Ok(result);
        }).RequireRateLimiting("llm").AddEndpointFilter<UserQuotaFilter>();

        // ── Streamende variant (#31): NDJSON-frames ────────────────────
        // meta (citaties/claims vóór het antwoord) → delta* → final|error.
        // Zelfde retrieval en afronding als /api/ask (AskService is de bron);
        // AI-uitval eindigt in een final-frame met de gedegradeerde tekst,
        // precies zoals de niet-streamende route. Zelfde quota-poort (#42):
        // de filter vuurt vóór de eerste frame-byte, dus 401/403/429 zijn
        // hier nog gewone JSON-responses.
        app.MapPost("/api/ask/stream", (AskRequest req, AskService ask, HttpContext http) =>
        {
            if (ValidateAsk(req, out var images, out var history) is { } bad) return bad;
            return Results.Stream(
                body => StreamAskAsync(
                    ask, req.Question.Trim(), images, history,
                    AgenticGate.ParseApproach(req.Approach), http, body),
                "application/x-ndjson");
        }).RequireRateLimiting("llm").AddEndpointFilter<UserQuotaFilter>();

        // Echte duurstatistiek (laatste 100 geslaagde vragen) voor de wachtindicatie.
        app.MapGet("/api/ask/stats", async (RbRulesDbContext db) =>
        {
            var recent = await db.AskMetrics
                .Where(m => m.Ok)
                .OrderByDescending(m => m.CreatedAt)
                .Take(100)
                .Select(m => m.DurationMs)
                .ToListAsync();
            if (recent.Count == 0) return Results.Ok(new { Count = 0 });
            var sorted = recent.OrderBy(x => x).ToList();
            // Fase-verdeling (#152): gemiddelde rewrite/embed/retrieval/AI
            // over de recentste geslaagde traces mét timings — de traces zijn
            // de enige plek waar de verdeling staat (metric kent alleen het
            // totaal). Parse is tolerant (AskPhases.Parse); geen timings =
            // gewoon geen phases-blok, de basisstatistiek blijft werken.
            var phaseRows = await db.AskTraces.AsNoTracking()
                .Where(t => t.Ok && t.PhaseTimings != null)
                .OrderByDescending(t => t.CreatedAt)
                .Take(50)
                .Select(t => t.PhaseTimings!)
                .ToListAsync();
            var parsed = phaseRows
                .Select(AskPhases.Parse)
                .OfType<AskPhases>()
                .ToList();
            return Results.Ok(new
            {
                Count = recent.Count,
                AvgMs = (int)recent.Average(),
                MedianMs = sorted[sorted.Count / 2],
                P90Ms = sorted[(int)(sorted.Count * 0.9)],
                Phases = parsed.Count == 0 ? null : new
                {
                    Count = parsed.Count,
                    RewriteMs = (int)parsed.Average(p => p.RewriteMs),
                    EmbedMs = (int)parsed.Average(p => p.EmbedMs),
                    RetrievalMs = (int)parsed.Average(p => p.RetrievalMs),
                    AiMs = (int)parsed.Average(p => p.AiMs),
                },
            });
        });

        // ── Interacties (S3) ───────────────────────────────────────────
        app.MapPost("/api/resolve", async (ResolveRequest req, InteractionService interactions) =>
        {
            if (req.CardIds is not { Length: >= 2 and <= 3 })
                return Results.BadRequest(new { error = "geef 2 of 3 card-ids" });
            var result = await interactions.ResolveAsync(req.CardIds);
            return result is null
                ? Results.BadRequest(new { error = "kaarten niet gevonden" })
                : Results.Ok(result);
        }).RequireRateLimiting("llm").AddEndpointFilter<UserQuotaFilter>();

        // ── Feedback op antwoorden (self-learning, #24) ────────────────
        app.MapPost("/api/corrections", async (CorrectionSubmit body, RbRulesDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(body.Question) || body.Question.Length > 2000)
                return Results.BadRequest(new { error = "question ontbreekt of is te lang" });
            if (body.Verdict is not ("up" or "down"))
                return Results.BadRequest(new { error = "verdict moet 'up' of 'down' zijn" });
            var text = string.IsNullOrWhiteSpace(body.Text)
                ? (body.Verdict == "up" ? "Door gebruiker als juist bevestigd." : "Door gebruiker als onjuist gemarkeerd.")
                : body.Text.Trim();
            if (text.Length > 4000)
                return Results.BadRequest(new { error = "correctie is te lang (max 4000 tekens)" });
            // Spam-rem: de reviewqueue is handwerk — cap de open items.
            if (await db.Corrections.CountAsync(c => c.Status == "unverified") >= 500)
                return Results.StatusCode(429);

            db.Corrections.Add(new Correction
            {
                Scope = "answer",
                Ref = body.Verdict,
                Text = text,
                Question = body.Question.Trim(),
                Provenance = "web-feedback",
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("llm").AddEndpointFilter<UserQuotaFilter>();
    }

    /// <summary>Gedeelde validatie/normalisatie voor /api/ask en
    /// /api/ask/stream — beide routes accepteren exact hetzelfde request.
    /// Geeft een BadRequest terug bij fouten, anders null.</summary>
    private static IResult? ValidateAsk(
        AskRequest req, out List<RbAiClient.AiImage> images, out List<AskTurn> history)
    {
        images = [];
        history = [];
        if (string.IsNullOrWhiteSpace(req.Question))
            return Results.BadRequest(new { error = "question is verplicht" });
        // Optionele board-state-foto('s): max 2, alleen gangbare beeldformaten.
        images = [.. (req.Images ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Data))
            .Take(2)
            .Select(i => new RbAiClient.AiImage(i.MediaType, i.Data))];
        if (images.Any(i => i.MediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")))
            return Results.BadRequest(new { error = "afbeeldingstype niet ondersteund" });
        if (images.Any(i => i.Data.Length > 8_000_000))
            return Results.BadRequest(new { error = "afbeelding te groot (max ~6 MB)" });

        // Doorvraag-historie (#41): gecapt op 3 rondes, tekstlengte begrensd.
        history = [.. (req.History ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Question))
            .TakeLast(3)
            .Select(t => new AskTurn(
                t.Question.Length > 2000 ? t.Question[..2000] : t.Question,
                t.Answer.Length > 6000 ? t.Answer[..6000] : t.Answer))];
        return null;
    }

    /// <summary>Schrijft de NDJSON-frames voor /api/ask/stream. Elk frame wordt
    /// direct geflusht zodat het antwoord woord-voor-woord bij de browser komt.
    /// Een weggelopen client (RequestAborted) is geen fout — dan gewoon stoppen;
    /// de metric-afronding in AskService gebruikt bewust geen request-token.</summary>
    private static async Task StreamAskAsync(
        AskService ask, string question, List<RbAiClient.AiImage> images,
        List<AskTurn> history, AskApproach approach, HttpContext http, Stream body)
    {
        await using var writer = new StreamWriter(body);
        async Task WriteFrameAsync(object frame)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(frame, StreamJson));
            await writer.FlushAsync(http.RequestAborted);
        }
        try
        {
            var result = await ask.AskStreamingAsync(
                question, images.Count > 0 ? images : null,
                history.Count > 0 ? history : null,
                onMeta: m => WriteFrameAsync(new
                {
                    type = "meta", questionType = m.QuestionType,
                    citations = m.Citations, claims = m.Claims,
                    // #153: aanpak-terugmelding vóór het antwoord — een
                    // quota-terugval hoort niet te wachten op het slotframe.
                    approach = m.Approach, approachReason = m.ApproachReason,
                }),
                onDelta: text => WriteFrameAsync(new { type = "delta", text }),
                approach, http.RequestAborted);
            await WriteFrameAsync(new { type = "final", result });
        }
        catch (OperationCanceledException)
        {
            // Client is weg — niets meer te schrijven.
        }
        catch (Exception ex)
        {
            // Onverwachte fout ná de 200: als frame melden (best-effort),
            // zodat de UI kan terugvallen i.p.v. eeuwig wachten.
            try
            {
                await WriteFrameAsync(new { type = "error", error = ex.Message });
            }
            catch
            {
                // response al kapot — dan valt er niets meer te melden
            }
        }
    }
}
