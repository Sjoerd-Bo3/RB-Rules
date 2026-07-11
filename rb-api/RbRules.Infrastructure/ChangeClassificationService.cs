using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ClassifyBackfillResult(int Attempted, int Classified, int Failed, int Remaining);

/// <summary>Herclassificatie van changes zonder samenvatting/duiding (#58).
/// Als rb-ai tijdens de scan niet beschikbaar was, is de change opgeslagen met
/// ChangeType "unknown" en Summary null — de diff staat wél opgeslagen, dus
/// classificeren kan altijd alsnog. Best-effort en idempotent: wat mislukt
/// blijft gewoon staan voor een volgende run.</summary>
public class ChangeClassificationService(RbRulesDbContext db, RbAiClient ai)
{
    public async Task<ClassifyBackfillResult> ClassifyPendingAsync(
        DateTimeOffset? since = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        var query = Pending().Include(c => c.Source).AsQueryable();
        if (since is not null) query = query.Where(c => c.DetectedAt >= since);
        var todo = await query.OrderBy(c => c.DetectedAt).ToListAsync(ct);

        var classified = 0;
        var failed = 0;
        var n = 0;
        foreach (var change in todo)
        {
            n++;
            progress?.Invoke(
                $"change {n}/{todo.Count} classificeren ({change.SourceId}, {change.DetectedAt:yyyy-MM-dd})");
            var raw = await ai.AskAsync(
                Classifier.BuildPrompt(change.Source?.Name ?? change.SourceId, change.Diff!),
                Classifier.SystemPrompt, ct: ct);
            var cls = raw is null ? null : Classifier.Parse(raw);
            if (Classifier.Apply(change, cls))
            {
                classified++;
            }
            else
            {
                failed++;
                // Geen stille degradatie meer: de reden waarom duiding uitblijft
                // is expliciet terug te vinden in run_log (#58).
                db.RunLogs.Add(new RunLog
                {
                    Kind = "classify", Ref = $"change:{change.Id}", Status = "error",
                    Detail = raw is null
                        ? "rb-ai niet beschikbaar — blijft staan voor een volgende run"
                        : "LLM-antwoord onbruikbaar (geen volledige classificatie)",
                });
            }
            await db.SaveChangesAsync(ct);
        }

        var remaining = await Pending().CountAsync(ct);
        return new(todo.Count, classified, failed, remaining);
    }

    // Zelfde predicaat voor todo én Remaining. Bewust inline (niet via
    // Classifier.NeedsClassification): eigen methodes vertalen niet in
    // EF-expression-trees.
    private IQueryable<Change> Pending() =>
        db.Changes.Where(c =>
            c.Diff != null && c.Diff != "" &&
            (c.ChangeType == "unknown"
             || c.Summary == null || c.Summary == ""
             || c.Meaning == null || c.Meaning == ""));
}
