using Microsoft.Extensions.DependencyInjection;

namespace RbRules.Infrastructure;

/// <summary>Definitie van een admin-achtergrondjob: naam + uit te voeren werk.
/// Het werk krijgt een scoped IServiceProvider (JobRunner opent de scope),
/// een voortgangs-reporter en een token; het resultaat is de detail-regel
/// voor run_log/admin.</summary>
public sealed record JobDefinition(
    string Name,
    Func<IServiceProvider, Action<string>, CancellationToken, Task<string>> Run);

/// <summary>Catalogus van admin-jobs (#59: de ±150-regel switch in
/// AdminEndpoints is weg). Een nieuwe job is één registratie in
/// <see cref="All"/>; de endpoints en JobRunner blijven onaangeraakt.</summary>
public static class JobCatalog
{
    public static JobDefinition? Find(string name) =>
        All.TryGetValue(name, out var job) ? job : null;

    private static readonly Dictionary<string, JobDefinition> All =
        new JobDefinition[]
        {
            // Eén knop voor alles: elke stap best-effort in de juiste volgorde —
            // een haperende stap (Ollama/LLM even weg) stopt de rest niet.
            new("all", RunAllAsync),
            new("scan", ScanAsync),
            new("cards", CardsAsync),
            new("embed", EmbedAsync),
            new("mine", MineAsync),
            new("rules", RulesAsync),
            new("bans", BansAsync),
            new("graph", GraphAsync),
            new("primer", PrimerAsync),
            new("interactions", InteractionsAsync),
            // Bronnenjacht (#63, stap 2): rb-ai doorzoekt het web (task
            // "research", #64) naar nieuwe regel-/uitlegbronnen. Vondsten
            // worden alleen als voorstel gelogd (run_log, kind "scout") —
            // opname in het register blijft een beheerdersbeslissing.
            new("scout", ScoutAsync),
            // Backfill (#58): álle changes zonder samenvatting/duiding of met
            // type "unknown" alsnog classificeren — de scan-retry pakt alleen
            // de laatste 14 dagen. Best-effort: wat mislukt blijft staan.
            new("classify", ClassifyAsync),
        }.ToDictionary(j => j.Name);

    private static async Task<string> RunAllAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
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
    }

    private static async Task<string> ScanAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
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
    }

    private static async Task<string> CardsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<CardSyncService>().SyncAsync(report, ct);
        return $"{r.Sets} sets, {r.Cards} kaarten via {r.Source}";
    }

    private static async Task<string> EmbedAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<CardEmbeddingPipeline>()
            .RunAsync(progress: report, ct: ct);
        return $"{r.Embedded} kaarten geembed, {r.Skipped} al actueel";
    }

    private static async Task<string> MineAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<MechanicMiningService>()
            .RunAsync(progress: report, ct: ct);
        return $"{r.Mined} kaarten gemined, {r.Remaining} resterend";
    }

    private static async Task<string> RulesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        // Handmatige run = volledige herbouw, zodat parser-verbeteringen
        // ook op bestaande documenten landen.
        var r = await sp.GetRequiredService<RuleChunkPipeline>()
            .RunAsync(force: true, report, ct);
        return $"{r.Sum(x => x.Chunks)} sectie-chunks over {r.Count} bronnen (herbouwd)";
    }

    private static async Task<string> BansAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("officiële documenten structureren via LLM");
        var r = await sp.GetRequiredService<BanErrataSyncService>().SyncAsync(ct);
        return $"{r.Bans} bans, {r.Errata} errata gestructureerd";
    }

    private static async Task<string> GraphAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("canonieke kaarten, domeinen, tags en mechanieken naar Neo4j schrijven (variant-knopen worden opgeruimd)");
        var r = await sp.GetRequiredService<GraphSyncService>().SyncAsync(ct);
        return $"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics";
    }

    private static async Task<string> PrimerAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<PrimerService>()
            .GenerateAsync(progress: report, ct: ct);
        return $"{r.Written} primer-docs geschreven (drafts), {r.Skipped} goedgekeurd gelaten, {r.Failed} mislukt";
    }

    private static async Task<string> InteractionsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<InteractionService>()
            .MineAsync(progress: report, ct: ct);
        return $"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd";
    }

    private static async Task<string> ScoutAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<SourceScoutService>()
            .RunAsync(progress: report, ct: ct);
        return r.Message;
    }

    private static async Task<string> ClassifyAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<ChangeClassificationService>()
            .ClassifyPendingAsync(progress: report, ct: ct);
        return $"{r.Classified} changes alsnog geclassificeerd, {r.Failed} mislukt, {r.Remaining} resterend";
    }
}
