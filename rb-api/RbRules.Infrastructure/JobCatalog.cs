using Microsoft.Extensions.DependencyInjection;

namespace RbRules.Infrastructure;

/// <summary>Definitie van een admin-achtergrondjob: naam + uit te voeren werk.
/// Het werk krijgt een scoped IServiceProvider (JobRunner opent de scope),
/// een voortgangs-reporter en een token; het resultaat is <see
/// cref="JobOutcome"/> (detail-regel voor run_log/admin + of de job
/// gedraineerd is).</summary>
public sealed record JobDefinition(
    string Name,
    Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> Run);

/// <summary>Resultaat van één jobrun (#190): <paramref name="Detail"/> is de
/// bestaande detail-regel (run_log/admin, ongewijzigd gedrag); <paramref
/// name="Drained"/> meldt machine-leesbaar of de job niets liet liggen voor
/// een volgende run — de meeste jobs verwerken hun hele werklast in één keer
/// (default true); een per-run gecapte job (mine/classify/claims/clarify/
/// relations) zet dit op false zolang zijn cap geraakt is. Paden (#190)
/// herhalen een Drain-stap tot dit true is (met een max-herhalingen-vangrail)
/// — zónder string-matching op de detailtekst.</summary>
public sealed record JobOutcome(string Detail, bool Drained = true);

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
            // Bron-feeds (#167): losse, snelle trigger die alleen de feed-
            // crawl draait (geen volledige bron-scan) — handig om net
            // toegevoegde feeds meteen te verifiëren. De reguliere "scan"
            // hierboven (en dus ook "all") draait de feed-crawl sowieso al
            // als eerste stap (IngestService.ScanAsync); dit is puur de
            // korte, geïsoleerde variant.
            new("feeds", FeedsAsync),
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
            // komen als SourceProposal in de reviewqueue (beheer →
            // Bronvoorstellen) — opname in het register blijft een
            // beheerdersbeslissing.
            new("scout", ScoutAsync),
            // Backfill (#58): álle changes zonder samenvatting/duiding of met
            // type "unknown" alsnog classificeren — de scan-retry pakt alleen
            // de laatste 14 dagen. Best-effort: wat mislukt blijft staan.
            new("classify", ClassifyAsync),
            // Kennislaag 2 (#50): claims destilleren uit community-bronnen in
            // het register (trust >= 3), met corroboratie en officiële toets.
            new("claims", ClaimsAsync),
            // FAQ-/clarificatie-concept-extractie (#177): losse verduidelij-
            // kingen uit officiële FAQ-artikelen (trust 1) als geverifieerde
            // rulings met eigen, gefocuste embedding — apart van "claims"
            // omdat de bron hier al officieel is (direct verified, geen
            // corroboratie/officiële-toets nodig) en apart van de bron-scan
            // zelf (LLM-mining hoort niet in IngestService.ScanAsync's
            // voetafdruk, zelfde motivatie als "claims" hierboven).
            new("clarify", ClarifyAsync),
            // Dynamische relaties (#116): de LLM ontdekt relaties over de
            // kennislagen heen; voorstellen + nieuwe kind-labels landen in de
            // reviewqueue en gaan pas via de graph-job de graph in.
            new("relations", RelationsAsync),
            // Evolutie-raamwerk (#52): de volledige set-release-keten
            // (sync -> nieuwe mechanieken -> embeddings -> graph -> primer).
            new("setrelease", SetReleaseAsync),
            // Piltover Archive-decks (#15): publieke deck-pagina's via de
            // sitemap, throttled en gecapt per run — bewust géén stap in de
            // "alles"-keten (een backfill-run duurt met de netiquette-throttle
            // tot ~10 minuten en heeft geen volgorde-afhankelijkheid).
            new("decks", DecksAsync),
            // Judge-benchmark (#158): vaste vragenset door de ask-pipeline met
            // de isolatie-vlag aan — bewust géén stap in de "alles"-keten (een
            // benchmarkrun meet kwaliteit, hij hoort niet bij het bijwerken
            // van de kennisbank).
            new("benchmark", BenchmarkAsync),
            // Model-sweep (#174): een eigen job in plaats van "benchmark" te
            // laten vertakken — de kosten zijn wezenlijk anders
            // (N_modellen × 2 × vragen ask-aanroepen versus 1×) en een
            // sweep is altijd een expliciete, dure beheerdersbeslissing
            // (issue-eis: "draai als expliciete admin-job, niet automatisch").
            // Twee losse knoppen houden dat onderscheid zichtbaar in het
            // jobs-paneel in plaats van een verborgen modus-vlag op
            // "benchmark" — zelfde precedent als "scan" vs. de losse "feeds".
            new("benchmarksweep", BenchmarkSweepAsync),
            // Wipe-mechanisme voor de LLM-afgeleide kennislaag (#187): gooit
            // claims, primer-docs, correcties en relaties weg (+ reset de
            // mining-markers) zodat een her-run met de Engelse prompts
            // schoon opnieuw opbouwt. Bewust GEEN stap in "all" en géén
            // automatische her-generatie hierna — expliciete, destructieve
            // beheerdersbeslissing (zie KnowledgeRegenerationService).
            new("regenerateknowledge", RegenerateKnowledgeAsync),
        }.ToDictionary(j => j.Name);

    private static async Task<JobOutcome> RunAllAsync(
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
            return $"{r.CardsSummary}{r.RepairSummary}";
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
            return $"{r.Cards} cards, {r.Sections} secties, {r.Claims} claims";
        });
        await Step("interacties", async () =>
        {
            var r = await sp.GetRequiredService<InteractionService>().MineAsync(
                progress: p => report($"8/8 · interacties — {p}"), ct: ct);
            return $"{r.Verified} geverifieerd";
        });
        return new(string.Join(" · ", results));
    }

    private static async Task<JobOutcome> ScanAsync(
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
        return new(string.Join(", ", r.Select(x => $"{x.SourceId}={x.Status}")));
    }

    private static async Task<JobOutcome> FeedsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        // Handmatige/losse trigger: forceer alle enabled feeds ongeacht
        // cadence (zelfde onlyDue:false-keuze als de "Bronnen scannen"-actie).
        var r = await sp.GetRequiredService<FeedCrawlService>().RunAsync(
            onlyDue: false, progress: report, ct: ct);
        return new(r.Message);
    }

    private static async Task<JobOutcome> CardsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<CardSyncService>().SyncAsync(report, ct);
        return new($"{r.Sets} sets, {r.CardsSummary}{r.RepairSummary}");
    }

    private static async Task<JobOutcome> EmbedAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<CardEmbeddingPipeline>()
            .RunAsync(progress: report, ct: ct);
        return new($"{r.Embedded} kaarten geembed, {r.Skipped} al actueel");
    }

    private static async Task<JobOutcome> MineAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<MechanicMiningService>()
            .RunAsync(progress: report, ct: ct);
        // #190: machine-leesbaar drain-signaal (geen string-matching) — deze
        // job is al capped/Remaining-bewust (MiningResult), dus Drained is
        // gewoon "niets meer resterend".
        return new($"{r.Mined} kaarten gemined, {r.Remaining} resterend", Drained: r.Remaining == 0);
    }

    private static async Task<JobOutcome> RulesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        // Handmatige run = volledige herbouw, zodat parser-verbeteringen
        // ook op bestaande documenten landen.
        var r = await sp.GetRequiredService<RuleChunkPipeline>()
            .RunAsync(force: true, report, ct);
        return new($"{r.Sum(x => x.Chunks)} sectie-chunks over {r.Count} bronnen (herbouwd)");
    }

    private static async Task<JobOutcome> BansAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("officiële documenten structureren via LLM");
        var r = await sp.GetRequiredService<BanErrataSyncService>().SyncAsync(ct);
        return new($"{r.Bans} bans, {r.Errata} errata gestructureerd");
    }

    private static async Task<JobOutcome> GraphAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("kaarten + facetten én de kennislagen (secties, concepten, claims, bronnen, errata, changes, relaties) naar Neo4j projecteren");
        var r = await sp.GetRequiredService<GraphSyncService>().SyncAsync(ct);
        return new($"{r.Cards} cards, {r.Domains} domains, {r.Tags} tags, {r.Mechanics} mechanics, "
            + $"{r.Sections} secties, {r.Concepts} concepten, {r.Claims} claims, "
            + $"{r.Sources} bronnen, {r.Errata} errata, {r.Changes} changes, "
            + $"{r.Relations} relaties");
    }

    private static async Task<JobOutcome> PrimerAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<PrimerService>()
            .GenerateAsync(progress: report, ct: ct);
        return new($"{r.Written} primer-docs geschreven (drafts), {r.Skipped} goedgekeurd gelaten, {r.Failed} mislukt");
    }

    private static async Task<JobOutcome> InteractionsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<InteractionService>()
            .MineAsync(progress: report, ct: ct);
        return new($"{r.Candidates} kandidaten beoordeeld, {r.Verified} interacties geverifieerd");
    }

    private static async Task<JobOutcome> ScoutAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<SourceScoutService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message);
    }

    private static async Task<JobOutcome> ClassifyAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<ChangeClassificationService>()
            .ClassifyPendingAsync(progress: report, ct: ct);
        return new(
            $"{r.Classified} changes alsnog geclassificeerd, {r.Failed} mislukt, {r.Remaining} resterend",
            Drained: r.Remaining == 0);
    }

    private static async Task<JobOutcome> ClaimsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<ClaimMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message, Drained: !r.CapHit);
    }

    private static async Task<JobOutcome> ClarifyAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<ClarificationMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message, Drained: !r.CapHit);
    }

    private static async Task<JobOutcome> RelationsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<RelationMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message, Drained: !r.CapHit);
    }
    private static async Task<JobOutcome> SetReleaseAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct) =>
        new(await sp.GetRequiredService<SetReleaseService>().RunChainAsync(report, ct));

    private static async Task<JobOutcome> DecksAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<DeckIngestService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message);
    }

    private static async Task<JobOutcome> BenchmarkAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BenchmarkService>()
            .RunAsync(label: null, progress: report, ct: ct);
        return new(r.Message);
    }

    private static async Task<JobOutcome> BenchmarkSweepAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BenchmarkService>()
            .RunSweepAsync(models: null, progress: report, ct: ct);
        return new(r.Message);
    }

    private static async Task<JobOutcome> RegenerateKnowledgeAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("afgeleide kennis (claims, primer-docs, correcties, relaties) verwijderen");
        var r = await sp.GetRequiredService<KnowledgeRegenerationService>().WipeAsync(ct);
        return new(r.Message);
    }
}
