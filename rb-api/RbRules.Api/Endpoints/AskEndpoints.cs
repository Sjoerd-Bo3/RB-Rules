using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class AskEndpoints
{
    public static void MapAskEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Rulings-Q&A (S2): hybrid retrieval + §-citaten ─────────────
        app.MapPost("/api/ask", async (AskRequest req, AskService ask, RbRulesDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question))
                return Results.BadRequest(new { error = "question is verplicht" });
            // Optionele board-state-foto('s): max 2, alleen gangbare beeldformaten.
            var images = (req.Images ?? [])
                .Where(i => !string.IsNullOrWhiteSpace(i.Data))
                .Take(2)
                .Select(i => new RbAiClient.AiImage(i.MediaType, i.Data))
                .ToList();
            if (images.Any(i => i.MediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")))
                return Results.BadRequest(new { error = "afbeeldingstype niet ondersteund" });
            if (images.Any(i => i.Data.Length > 8_000_000))
                return Results.BadRequest(new { error = "afbeelding te groot (max ~6 MB)" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ask.AskAsync(req.Question.Trim(), images.Count > 0 ? images : null);
            sw.Stop();
            try
            {
                // Duurmeting voedt de echte "gemiddeld ±Xs"-indicatie op de vraagpagina.
                db.AskMetrics.Add(new AskMetric
                {
                    DurationMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue),
                    QuestionType = result.QuestionType,
                    HadImage = images.Count > 0,
                    Ok = result.Ok,
                });
                await db.SaveChangesAsync();
            }
            catch
            {
                // meting mag een antwoord nooit blokkeren
            }
            return Results.Ok(result);
        });

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
            return Results.Ok(new
            {
                Count = recent.Count,
                AvgMs = (int)recent.Average(),
                MedianMs = sorted[sorted.Count / 2],
                P90Ms = sorted[(int)(sorted.Count * 0.9)],
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
        });

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
        });
    }
}
