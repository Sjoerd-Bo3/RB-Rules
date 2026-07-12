using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record SetReleaseRunResult(int Triggers, string Detail);

/// <summary>Evolutie-raamwerk (#52): set-release als event. De change-
/// classifier herkent set-releases al; deze service laat dat event de
/// volledige keten draaien: card-sync → nieuwe-mechanieken-detectie →
/// claims-harvest (#50, sinds #108 echt aangeroepen) → embeddings →
/// graph-sync → primer-herziening. Elke stap is best-effort — een
/// haperende externe dienst stopt de keten niet, en het resultaat per stap
/// is zichtbaar in run_log (kind "setrelease").</summary>
public class SetReleaseService(
    RbRulesDbContext db,
    CardSyncService cards,
    MechanicMiningService mining,
    ClaimMiningService claims,
    CardEmbeddingPipeline embeddings,
    GraphSyncService graph,
    PrimerService primer)
{
    /// <summary>Draait de keten voor alle set-release-changes die nog niet
    /// behandeld zijn (grootboek: run_log kind "setrelease", ref "change:id").
    /// De grootboek-regel wordt vóór de keten geschreven zodat een deels
    /// mislukte run niet elke tick opnieuw triggert — herstel loopt via de
    /// handmatige job en de foutdetails in run_log.</summary>
    public async Task<SetReleaseRunResult> RunForPendingAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var handled = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "setrelease" && l.Ref != null && l.Ref.StartsWith("change:"))
            .Select(l => l.Ref!)
            .ToListAsync(ct);
        var handledSet = handled.ToHashSet();

        var releases = await db.Changes.AsNoTracking()
            .Where(c => c.ChangeType == "set-release")
            .OrderBy(c => c.DetectedAt)
            .Select(c => new { c.Id, c.SourceId, c.Summary })
            .ToListAsync(ct);
        var pending = releases.Where(c => !handledSet.Contains($"change:{c.Id}")).ToList();
        if (pending.Count == 0) return new(0, "geen onverwerkte set-releases");

        foreach (var change in pending)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "setrelease", Ref = $"change:{change.Id}", Status = "info",
                Detail = $"keten getriggerd ({change.SourceId}): {change.Summary ?? "set-release"}",
            });
        }
        await db.SaveChangesAsync(ct);

        var detail = await RunChainAsync(progress, ct);
        return new(pending.Count, detail);
    }

    /// <summary>De volledige keten, in de volgorde van het issue. Eén keer
    /// draaien dekt ook meerdere tegelijk gedetecteerde releases — alle
    /// stappen zijn idempotent (sync/mine/embed pakken alleen wat nodig is).</summary>
    public async Task<string> RunChainAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<string>();
        async Task Step(string label, Func<Task<string>> run)
        {
            progress?.Invoke($"{results.Count + 1}/6 · {label}");
            try { results.Add($"{label}: {await run()}"); }
            catch (Exception ex) { results.Add($"{label}: FOUT — {ex.Message}"); }
        }

        await Step("kaarten", async () =>
        {
            var r = await cards.SyncAsync(p => progress?.Invoke($"1/6 · kaarten — {p}"), ct);
            return $"{r.Cards} kaarten via {r.Source}";
        });
        await Step("mechanieken", async () =>
        {
            // Ruime batch-limiet: een nieuwe set is honderden kaarten ineens.
            var r = await mining.RunAsync(
                maxBatches: 60, p => progress?.Invoke($"2/6 · mechanieken — {p}"), ct);
            return $"{r.Mined} gemined, {r.NewCandidates} keyword-kandidaten, {r.Remaining} resterend";
        });
        await Step("claims-harvest", async () =>
        {
            // Stap 3 (#108): een verse set brengt een golf nieuwe community-
            // inhoud mee. Gecapt op de standaard-cap van de nachtelijke job —
            // wat de cap niet haalt pakt die job vanzelf op (documenten
            // blijven ongemarkeerd, #92); rb-ai/Ollama-uitval degradeert al
            // per stap binnen ClaimMiningService zelf.
            var r = await claims.RunAsync(
                progress: p => progress?.Invoke($"3/6 · claims — {p}"), ct: ct);
            return r.Message;
        });
        await Step("embeddings", async () =>
        {
            var r = await embeddings.RunAsync(
                progress: p => progress?.Invoke($"4/6 · embeddings — {p}"), ct: ct);
            return $"{r.Embedded} geembed";
        });
        await Step("graph", async () =>
        {
            var r = await graph.SyncAsync(ct);
            return $"{r.Cards} cards";
        });
        await Step("primer-herziening", async () =>
        {
            // force: false — ontbrekende/draft-concepten worden (her)schreven;
            // goedgekeurde docs blijven staan. Of nieuwe keywords de flow raken
            // beslist de beheerder via de kandidatenqueue en her-generatie.
            var r = await primer.GenerateAsync(
                progress: p => progress?.Invoke($"6/6 · primer — {p}"), ct: ct);
            return $"{r.Written} drafts, {r.Skipped} goedgekeurd gelaten, {r.Failed} mislukt";
        });

        var detail = string.Join(" · ", results);
        var failed = results.Any(r => r.Contains("FOUT"));
        db.RunLogs.Add(new RunLog
        {
            Kind = "setrelease", Ref = "keten",
            Status = failed ? "error" : "ok", Detail = detail,
        });
        await db.SaveChangesAsync(ct);
        return detail;
    }
}
