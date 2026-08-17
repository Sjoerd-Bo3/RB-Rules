using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BenchmarkRunResult(long RunId, int Questions, int Keyed, int Correct, string Message);

/// <summary>Resultaat van één volledige model-sweep (#174): elk model uit de
/// lijst 2×, dezelfde vragenset. RunIds staan in de volgorde waarin ze
/// draaiden (model1-run1, model1-run2, model2-run1, …) zodat de aanroeper
/// (job-log/tests) ze kan terugvinden zonder opnieuw te queryen.</summary>
public record BenchmarkSweepResult(
    long SweepId, IReadOnlyList<long> RunIds, string Message);

/// <summary>Benchmarkrun (#158, JobCatalog "benchmark"): draait de vaste
/// vragenset (BenchmarkSeed) door de bestaande /ask-pipeline met
/// AskOptions.Benchmark = true — retrieval + prompt zijn identiek aan een
/// normale vraag, maar AskService onderdrukt daarmee élk leer-/
/// meetneveneffect (geen ask_trace/ask_metric, geen agentic-relatie-
/// terugkoppeling #120). De meerkeuze-opties gaan via BenchmarkPrompt alleen
/// in de vraagtekst van déze run mee, nooit in het normale /ask-pad. Scoren
/// volgt de "gecommitteerde keuze"-aanpak uit issue #158: de deterministische
/// letter-parser (BenchmarkPrompt.ParseChoice) haalt de keuze uit het
/// antwoord; geen match ⇒ ChosenIndex null, geen crash van de run.
///
/// Model-sweep (#174, JobCatalog "benchmarksweep"): dezelfde pipeline, maar
/// voor elk geconfigureerd model 2 herhalingen — voor een eerlijke
/// vergelijking op score én tijd, en om te zien of een model consistent
/// scoort (2 gelijke runs) of een toevalstreffer was. RunOneAsync hieronder
/// is de gedeelde kern: RunAsync (single, ongewijzigd gedrag — Model/RunIndex/
/// SweepId blijven null) en RunSweepAsync (elk (model, run)-paar krijgt zijn
/// eigen BenchmarkRun-rij) roepen 'm allebei aan. Een onbekend/ongeldig model
/// crasht de sweep niet: AskService/RbAiClient degraderen een rb-ai-fout al
/// naar RbAiClient.UnavailableAnswer (geen exception), dus die vraag komt
/// gewoon als "fout"/onscoorbaar in de resultaten te staan — de rest van de
/// sweep draait door.</summary>
public class BenchmarkService(RbRulesDbContext db, AskService ask)
{
    /// <summary>Aantal herhalingen per model in een sweep (#174-issue-eis:
    /// "elk 2 runs" — consistentie zichtbaar maken, geen losse constante
    /// zonder betekenis).</summary>
    public const int RunsPerModel = 2;

    /// <summary>Default-modellenlijst als AI_BENCHMARK_MODELS niet gezet is:
    /// een betaalbare spreiding over de modellen die rb-ai daadwerkelijk kan
    /// inzetten (rb-ai/src/ai.ts's MODEL-record gebruikt claude-sonnet-4-6
    /// voor cheap/research/agentic en claude-opus-4-8 voor hard) plus de
    /// nieuwere goedkope en midden-tier varianten, zodat de sweep ook
    /// toekomstige modelkeuzes voor rb-ai zelf beoordeelt. Bewust geen
    /// duurdere/afwijkend-gedragende modellen (bv. claude-fable-5: andere
    /// refusal-/thinking-semantiek, hogere kosten) in de default — die kan
    /// Sjoerd expliciet toevoegen via AI_BENCHMARK_MODELS.</summary>
    private static readonly string[] DefaultModels =
    [
        "claude-haiku-4-5", "claude-sonnet-4-6", "claude-sonnet-5", "claude-opus-4-8",
    ];

    /// <summary>Leest AI_BENCHMARK_MODELS (comma-gescheiden) of valt terug op
    /// <see cref="DefaultModels"/>. Publiek zodat JobCatalog en de admin-UI
    /// dezelfde lijst kunnen tonen (geschatte sweep-omvang) zonder een run te
    /// starten.</summary>
    public static IReadOnlyList<string> ResolveModels()
    {
        var raw = Environment.GetEnvironmentVariable("AI_BENCHMARK_MODELS");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultModels;
        var models = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
        return models.Count > 0 ? models : DefaultModels;
    }

    public async Task<BenchmarkRunResult> RunAsync(
        string? label, Action<string> progress, CancellationToken ct = default)
    {
        var questions = await LoadQuestionsAsync(ct);
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

        var (run, keyed, correct) = await RunOneAsync(
            questions, label, model: null, runIndex: null, sweepId: null, progress, ct);
        await FinalizeRunAsync(run, questions.Count, keyed, correct, ct);

        var scoreLabel = run.ScorePercent is { } sp ? $"{sp}%" : "nog geen sleutel";
        return new(run.Id, questions.Count, keyed, correct,
            $"{questions.Count} vragen · {keyed} gekeyed · score {scoreLabel}");
    }

    /// <summary>Model-sweep (#174): de volledige vragenset door elk model uit
    /// <paramref name="models"/> (null/leeg ⇒ <see cref="ResolveModels"/>),
    /// elk <see cref="RunsPerModel"/>× — sequentieel, model per model, run per
    /// run. Bewust géén eigen parallellisatie: elke ask-aanroep gaat toch al
    /// door rb-ai's globale gelijktijdigheids-cap (#155, AI_MAX_CONCURRENCY)
    /// heen — een sequentiële sweep voegt precies één wachtende aanvraag per
    /// keer aan die rij toe en kan de VM dus nooit omvertrekken, zonder de
    /// complexiteit van een eigen lokale semafoor hier (KISS). De geschatte
    /// omvang (N modellen × <see cref="RunsPerModel"/> × vragen) gaat vóór de
    /// eerste ask-aanroep al als progress-regel én run_log-rij de deur uit,
    /// zodat het job-detail-paneel de kostenverwachting toont vóór er ook
    /// maar één LLM-call gedaan is.</summary>
    public async Task<BenchmarkSweepResult> RunSweepAsync(
        IReadOnlyList<string>? models, Action<string> progress, CancellationToken ct = default)
    {
        var resolvedModels = models is { Count: > 0 } ? models : ResolveModels();
        var questions = await LoadQuestionsAsync(ct);
        if (questions.Count == 0)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "benchmarksweep", Status = "error",
                Detail = "geen benchmarkvragen — seed ontbreekt (verwacht bij de volgende opstart)",
            });
            await db.SaveChangesAsync(ct);
            return new(0, [], "geen benchmarkvragen — seed ontbreekt");
        }

        // SweepId dubbelt als sorteerbare "wanneer"-waarde voor het
        // verloop-over-tijd-overzicht (#174) — geen aparte tabel nodig voor
        // puur een groepeersleutel.
        var sweepId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var totalCalls = resolvedModels.Count * RunsPerModel * questions.Count;
        var sizeMessage =
            $"sweep {resolvedModels.Count} modellen ({string.Join(", ", resolvedModels)}) " +
            $"× {RunsPerModel} runs × {questions.Count} vragen = {totalCalls} ask-aanroepen";
        progress(sizeMessage);
        db.RunLogs.Add(new RunLog
        {
            Kind = "benchmarksweep", Ref = sweepId.ToString(), Status = "ok",
            Detail = $"gestart — {sizeMessage}",
        });
        await db.SaveChangesAsync(ct);

        var runIds = new List<long>();
        var summaries = new List<string>();
        for (var m = 0; m < resolvedModels.Count; m++)
        {
            var model = resolvedModels[m];
            for (var runIndex = 1; runIndex <= RunsPerModel; runIndex++)
            {
                var prefix = $"model {model} ({m + 1}/{resolvedModels.Count}), run {runIndex}/{RunsPerModel}";
                progress($"{prefix} — starten");
                var (run, keyed, correct) = await RunOneAsync(
                    questions, label: $"sweep {model} run {runIndex}", model, runIndex, sweepId,
                    p => progress($"{prefix}, {p}"), ct);
                await FinalizeRunAsync(run, questions.Count, keyed, correct, ct);
                runIds.Add(run.Id);
                var scoreLabel = run.ScorePercent is { } sp ? $"{sp}%" : "nog geen sleutel";
                summaries.Add($"{model} run {runIndex}: {scoreLabel}");
            }
        }

        var summary = string.Join(", ", summaries);
        db.RunLogs.Add(new RunLog
        {
            Kind = "benchmarksweep", Ref = sweepId.ToString(), Status = "ok",
            Detail = $"sweep afgerond — {summary}",
        });
        await db.SaveChangesAsync(ct);

        return new(sweepId, runIds,
            $"{resolvedModels.Count} modellen × {RunsPerModel} runs · {summary}");
    }

    private async Task<List<BenchmarkQuestion>> LoadQuestionsAsync(CancellationToken ct) =>
        await db.BenchmarkQuestions.OrderBy(q => q.Id).ToListAsync(ct);

    /// <summary>Eén volledige doorloop van de vragenset voor één (model,
    /// run)-combinatie — de kern die zowel RunAsync (model/runIndex/sweepId
    /// allemaal null) als RunSweepAsync (per model, per herhaling) aanroept.
    /// Maakt zelf de BenchmarkRun-rij aan en boekt de resultaten; de
    /// aanroeper rondt af (FinalizeRunAsync) zodra alle vragen klaar zijn —
    /// gesplitst zodat RunSweepAsync tussen twee runs door kan loggen zonder
    /// dat de score-afronding twee keer geschreven wordt.</summary>
    private async Task<(BenchmarkRun Run, int Keyed, int Correct)> RunOneAsync(
        List<BenchmarkQuestion> questions, string? label, string? model, int? runIndex, long? sweepId,
        Action<string> progress, CancellationToken ct)
    {
        var run = new BenchmarkRun
        {
            Label = label, QuestionCount = questions.Count,
            Model = model, RunIndex = runIndex, SweepId = sweepId,
        };
        db.BenchmarkRuns.Add(run);
        await db.SaveChangesAsync(ct); // Run-id nu bekend voor de resultaatrijen.

        var keyed = 0;
        var correct = 0;
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            progress($"vraag {i + 1}/{questions.Count} · {q.Category} #{q.Id}");

            var composed = BenchmarkPrompt.BuildQuestion(q.Question, q.Options);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AskResult result;
            try
            {
                // BENCHMARK-VLAG (#158) + model-sweep-override (#174): het
                // enige punt waarop deze run zich onderscheidt van een
                // normale vraag — zie AskService.AskOptions.
                result = await ask.AskAsync(
                    composed, images: null, history: null,
                    options: new AskOptions { Benchmark = true, Model = model }, ct: ct);
            }
            catch (Exception ex)
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "benchmark", Ref = q.ExternalKey, Status = "error",
                    Detail = $"vraag {q.ExternalKey} mislukt" +
                        (model is null ? "" : $" (model {model})") + $": {ex.Message}",
                });
                continue;
            }
            sw.Stop();

            var chosen = BenchmarkPrompt.ParseChoice(result.Answer, q.Options.Length);
            // Correct is uitsluitend null zonder officiële sleutel — een
            // parse-mislukking op een gekeyde vraag is gewoon fout (null-index
            // matcht geen CorrectIndex), nooit een aparte "fout"-status. Een
            // onbekend model (#174) valt in dezelfde bak: AskService/
            // RbAiClient degraderen een rb-ai-fout al naar
            // RbAiClient.UnavailableAnswer zonder exception, dus die vraag
            // komt hier gewoon als onscoorbaar/fout resultaat binnen — geen
            // aparte foutafhandeling nodig voor "onbekend model".
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
        return (run, keyed, correct);
    }

    private async Task FinalizeRunAsync(
        BenchmarkRun run, int questionCount, int keyed, int correct, CancellationToken ct)
    {
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.KeyedCount = keyed;
        run.CorrectCount = correct;
        run.ScorePercent = keyed == 0 ? null : Math.Round(100.0 * correct / keyed, 1);

        var scoreLabel = run.ScorePercent is { } sp ? $"{sp}%" : "nog geen sleutel";
        db.RunLogs.Add(new RunLog
        {
            Kind = "benchmark", Ref = run.Id.ToString(), Status = "ok",
            Detail = $"{questionCount} vragen, {keyed} gekeyed, {correct} correct ({scoreLabel})" +
                (run.Model is null ? "" : $" — model {run.Model}, run {run.RunIndex}/{RunsPerModel}"),
        });
        await db.SaveChangesAsync(ct);
    }
}
