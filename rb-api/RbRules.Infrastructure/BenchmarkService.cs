using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BenchmarkRunResult(long RunId, int Questions, int Keyed, int Correct, string Message);

/// <summary>Benchmarkrun (#158, JobCatalog "benchmark"): draait de vaste
/// vragenset (BenchmarkSeed) door de bestaande /ask-pipeline met
/// AskOptions.Benchmark = true — retrieval + prompt zijn identiek aan een
/// normale vraag, maar AskService onderdrukt daarmee élk leer-/
/// meetneveneffect (geen ask_trace/ask_metric, geen agentic-relatie-
/// terugkoppeling #120). De meerkeuze-opties gaan via BenchmarkPrompt alleen
/// in de vraagtekst van déze run mee, nooit in het normale /ask-pad. Scoren
/// volgt de "gecommitteerde keuze"-aanpak uit issue #158: de deterministische
/// letter-parser (BenchmarkPrompt.ParseChoice) haalt de keuze uit het
/// antwoord; geen match ⇒ ChosenIndex null, geen crash van de run.</summary>
public class BenchmarkService(RbRulesDbContext db, AskService ask)
{
    public async Task<BenchmarkRunResult> RunAsync(
        string? label, Action<string> progress, CancellationToken ct = default)
    {
        var questions = await db.BenchmarkQuestions
            .OrderBy(q => q.Id)
            .ToListAsync(ct);
        if (questions.Count == 0)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "benchmark", Status = "error",
                Detail = "geen benchmarkvragen — seed ontbreekt (verwacht bij de volgende opstart)",
            });
            await db.SaveChangesAsync(ct);
            return new(0, 0, 0, 0, "geen benchmarkvragen — seed ontbreekt");
        }

        var run = new BenchmarkRun { Label = label, QuestionCount = questions.Count };
        db.BenchmarkRuns.Add(run);
        await db.SaveChangesAsync(ct); // Run-id nu bekend voor de resultaatrijen.

        var keyed = 0;
        var correct = 0;
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            progress($"{i + 1}/{questions.Count} · {q.Category} #{q.Id}");

            var composed = BenchmarkPrompt.BuildQuestion(q.Question, q.Options);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AskResult result;
            try
            {
                // BENCHMARK-VLAG (#158): het enige punt waarop deze run zich
                // onderscheidt van een normale vraag — zie AskService.AskOptions.
                result = await ask.AskAsync(
                    composed, images: null, history: null,
                    options: new AskOptions { Benchmark = true }, ct: ct);
            }
            catch (Exception ex)
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "benchmark", Ref = q.ExternalKey, Status = "error",
                    Detail = $"vraag {q.ExternalKey} mislukt: {ex.Message}",
                });
                continue;
            }
            sw.Stop();

            var chosen = BenchmarkPrompt.ParseChoice(result.Answer, q.Options.Length);
            // Correct is uitsluitend null zonder officiële sleutel — een
            // parse-mislukking op een gekeyde vraag is gewoon fout (null-index
            // matcht geen CorrectIndex), nooit een aparte "fout"-status.
            bool? isCorrect = q.CorrectIndex is null ? null : chosen == q.CorrectIndex;
            if (q.CorrectIndex is not null)
            {
                keyed++;
                if (isCorrect == true) correct++;
            }

            db.BenchmarkResults.Add(new BenchmarkResult
            {
                RunId = run.Id,
                QuestionId = q.Id,
                Answer = result.Answer,
                ChosenIndex = chosen,
                Correct = isCorrect,
                DurationMs = (int)sw.ElapsedMilliseconds,
                InputTokens = result.Usage?.InputTokens,
                OutputTokens = result.Usage?.OutputTokens,
            });
        }

        run.CompletedAt = DateTimeOffset.UtcNow;
        run.KeyedCount = keyed;
        run.CorrectCount = correct;
        run.ScorePercent = keyed == 0 ? null : Math.Round(100.0 * correct / keyed, 1);

        var scoreLabel = run.ScorePercent is { } sp ? $"{sp}%" : "nog geen sleutel";
        db.RunLogs.Add(new RunLog
        {
            Kind = "benchmark", Ref = run.Id.ToString(), Status = "ok",
            Detail = $"{questions.Count} vragen, {keyed} gekeyed, {correct} correct ({scoreLabel})",
        });
        await db.SaveChangesAsync(ct);

        return new(run.Id, questions.Count, keyed, correct,
            $"{questions.Count} vragen · {keyed} gekeyed · score {scoreLabel}");
    }
}
