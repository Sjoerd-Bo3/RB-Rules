using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één eval-run (#387) voor job/beheer.</summary>
public sealed record EvalRunOutcome(string RunId, bool Passed, int Cases, int Failures, int Shadow, string Message);

/// <summary>Per-case-regel zoals opgeslagen in <see cref="EvalRunRecord.ResultsJson"/>.</summary>
public sealed record EvalCaseRunRow(
    string CaseId, string Status, bool Counted,
    double Recall, double Relevancy, double F1, double CitationPrecision, double ContradictionRecall,
    IReadOnlyList<string> Violations);

/// <summary>De eval-set door de echte /ask-flow (#387): elk geval dat van
/// kracht is gaat met <c>AskOptions.Benchmark = true</c> (geen metric, trace,
/// geheugen of leer-neveneffect) door <see cref="AskService"/>, het antwoord
/// wordt via <see cref="EvalRunMapping"/> geabstraheerd tot ids, en de pure
/// harness scoort: harde gate (<see cref="EvalGateEvaluator"/>: verzonnen
/// citaties, verboden claims) plus de 2σ-baseline-diff per vraagklasse
/// (<see cref="BaselineDiffGate"/>) tegen de vastgelegde Ring-A-baseline. Zonder
/// baseline wordt deze run de baseline. Elke run is een <see cref="EvalRunRecord"/>
/// met per-case-uitkomsten en samples, plus een run_log-regel.
///
/// Kosten: één LLM-call per geval (het antwoordmodel), dus een run van honderd
/// gevallen is een bewuste beheerdersbeslissing — geen stap in de nachtrun.</summary>
public class EvalRunService(RbRulesDbContext db, AskService ask, EvalCaseService cases)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public const EvalRing Ring = EvalRing.A;

    public async Task<EvalRunOutcome> RunAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var all = await cases.DomainCasesAsync(ct);
        var inEffect = all.Where(c => c.IsInEffect(today)).ToList();
        if (inEffect.Count == 0)
            return new("", true, 0, 0, 0, "geen eval-gevallen van kracht — promoveer eerst vragen vanuit de traces of het geheugen");

        var runs = new List<EvalCaseRun>();
        var failed = 0;
        for (var i = 0; i < inEffect.Count; i++)
        {
            var c = inEffect[i];
            progress?.Invoke($"eval {i + 1}/{inEffect.Count}: {c.Question}");
            EvalRunResult result;
            try
            {
                var r = await ask.AskAsync(c.Question, options: new AskOptions { Benchmark = true }, ct: ct);
                result = r.Ok
                    ? EvalRunMapping.ToRunResult(
                        r.Citations.Select(x => x.Section), r.Answer, c.ForbiddenClaims)
                    : new EvalRunResult();
                if (!r.Ok) failed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Eén omgevallen vraag is een lege run voor dat geval (recall 0),
                // geen afgebroken eval: de uitval is dan zichtbaar in de score.
                result = new EvalRunResult();
                failed++;
            }
            runs.Add(new EvalCaseRun(c, result));
        }

        var gate = EvalGateEvaluator.Evaluate(runs.Select(r => (r.Case, r.Run)), today);
        var samples = EvalHarness.Samples(runs, Ring, today);
        var baselineRows = await db.EvalBaselines.Where(b => b.Ring == Ring.ToString()).ToListAsync(ct);
        var passed = gate.Passed;
        var memo = EvalRunMapping.ClassSummary(samples);
        if (baselineRows.Count == 0)
        {
            await WriteBaselineAsync(samples, ct);
            memo += " · eerste run: vastgelegd als baseline";
        }
        else
        {
            var baseline = new EvalBaseline(baselineRows.Select(b => b.ToCell()));
            var diff = BaselineDiffGate.Evaluate(Ring, baseline, samples);
            if (!diff.Passed)
            {
                passed = false;
                memo += " · regressie t.o.v. baseline: " + string.Join(", ",
                    diff.GatingRegressions.Select(r => $"{r.QueryType}/{r.Metric} {r.BaselineMean:0.00}→{r.CurrentMean:0.00}"));
            }
        }
        if (gate.GatingFailures.Count > 0)
            memo += " · harde overtredingen: " + string.Join(", ",
                gate.GatingFailures.Select(f => $"{f.CaseId} ({string.Join("; ", f.Violations)})"));
        if (failed > 0) memo += $" · {failed} vraag/vragen zonder antwoord";

        var rows = gate.Results.Select(r => new EvalCaseRunRow(
            r.CaseId, r.Status.ToString().ToLowerInvariant(), r.CountedTowardGate,
            r.Metrics.Recall, r.Metrics.Relevancy, r.Metrics.F1, r.Metrics.CitationPrecision,
            r.Metrics.ContradictionRecall, r.Violations)).ToList();
        var run = new EvalRunRecord
        {
            Id = Ulid.NewUlid(),
            Ring = Ring.ToString(),
            // Geen git-SHA in de container-env (de deploy pint IMAGE_TAG alleen op
            // het image); de promptversie-hash is de epoch-stempel die er wél is.
            GitSha = null,
            LlmModel = AskPathModels.Resolve("cheap"),
            PromptVersion = AskService.PromptVersion,
            Passed = passed,
            CaseCount = gate.Results.Count,
            GatingFailureCount = gate.GatingFailures.Count,
            ShadowCount = gate.ShadowObservations.Count,
            Memo = memo,
            ResultsJson = JsonSerializer.Serialize(rows, Json),
            SamplesJson = JsonSerializer.Serialize(samples, Json),
        };
        db.EvalRuns.Add(run);
        db.RunLogs.Add(new RunLog
        {
            Kind = "eval", Ref = run.Id, Status = passed ? "ok" : "error",
            Detail = $"{gate.Results.Count} gevallen ({gate.ShadowObservations.Count} shadow), " +
                     (passed ? "gate gehaald" : "gate NIET gehaald") + " — " + memo,
        });
        await db.SaveChangesAsync(ct);

        return new(run.Id, passed, gate.Results.Count, gate.GatingFailures.Count,
            gate.ShadowObservations.Count,
            (passed ? "gate gehaald: " : "gate NIET gehaald: ") + memo);
    }

    /// <summary>Leg de samples van een eerdere run vast als de nieuwe Ring-A-
    /// baseline (na een geaccordeerde verbetering). False = run onbekend of
    /// zonder samples.</summary>
    public async Task<bool> RecordBaselineFromRunAsync(string runId, CancellationToken ct)
    {
        var run = await db.EvalRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run?.SamplesJson is null) return false;
        List<ClassifiedSample> samples;
        try
        {
            samples = JsonSerializer.Deserialize<List<ClassifiedSample>>(run.SamplesJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }
        if (samples.Count == 0) return false;
        await WriteBaselineAsync(samples, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Vervang de Ring-A-baseline door de cellen uit deze samples
    /// (zelfde context; de aanroeper slaat op).</summary>
    private async Task WriteBaselineAsync(IReadOnlyList<ClassifiedSample> samples, CancellationToken ct)
    {
        var old = await db.EvalBaselines.Where(b => b.Ring == Ring.ToString()).ToListAsync(ct);
        db.EvalBaselines.RemoveRange(old);
        var baseline = EvalBaseline.FromSamples(Ring, samples);
        foreach (var cell in baseline.Cells)
            db.EvalBaselines.Add(EvalBaselineRecord.FromCell(cell, null, AskService.PromptVersion));
    }

    public async Task<IReadOnlyList<EvalRunRecord>> RecentRunsAsync(int take, CancellationToken ct) =>
        await db.EvalRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
}
