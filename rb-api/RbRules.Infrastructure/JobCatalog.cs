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
/// name="Drained"/> meldt machine-leesbaar of de job geen VERS werk liet
/// liggen voor een volgende run — de meeste jobs verwerken hun hele werklast
/// in één keer (default true); een per-run gecapte job zet dit op false
/// zolang zijn cap geraakt is (claims/clarify/relations/decks via hun eigen
/// CapHit-veld; mine via Remaining − Failed). Vers werk ≠ failures
/// (review-fix #190): items die zojuist FAALDEN tellen niet — een directe
/// herhaling faalt vrijwel zeker opnieuw (rb-ai down, poison item), dus die
/// horen bij de volgende run/tick, niet bij een drain-lus. Paden (#190)
/// herhalen een Drain-stap tot dit true is, met een max-herhalingen-vangrail
/// én een no-progress-guard in PathRunner — zónder string-matching op de
/// detailtekst.</summary>
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
            // Lambda-wrap (niet als method-group): RunAllAsync draagt sinds #245
            // trailing optionele params (mechanicMaxBatches/deadline) voor de nachtrun.
            new("all", (sp, report, ct) => RunAllAsync(sp, report, ct)),
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
            // Brein-projectie (#227, §3.5): de brein-lagen die "graph" niet dekt
            // (CanonicalEntity/MechanicPredicate/OntologyVersion) idempotent naar
            // Neo4j. Additief naast "graph" (aparte service/transactie, eigen
            // ref-namespace) en logisch ná "graph" (de basis-graaf moet er zijn).
            // Bewust GEEN stap in de "alles"-keten — Neo4j-afhankelijk, draait als
            // expliciete beheerdersactie (zelfde lijn als "graph"/"reason").
            new("breinprojectie", BreinProjectionAsync),
            // Redeneer-laag (#227, §5): Neo4j-native inferentie (afgeleide edges)
            // + bounded contradictie-detectie (→ misvattingen/reviewqueue). Loopt
            // logisch ná "graph" (de projectie moet er zijn); bewust GEEN stap in
            // de "alles"-keten — de reasoner is Neo4j-afhankelijk en draait als
            // expliciete beheerdersactie (zelfde lijn als "graph" die apart staat).
            new("reason", ReasonAsync),
            // OWL2-RL-nachtaudit (#227) — SKELETON per beslissing: een pure
            // zelf-toets van de afgedwongen schema-bron (OntologySchema), geen
            // OWL-runtime. Optioneel, nooit in "alles".
            new("owlaudit", OwlAuditAsync),
            new("primer", PrimerAsync),
            new("interactions", InteractionsAsync),
            // Brein-mining (#226, §3.1/§3.4): tool-forced, ontologie-begrensde
            // extractie via rb-ai → entity-resolutie (fase 1) → fase-2-promotie-poort
            // → atomair feit+provenance. Twee losse, handmatige jobs — bewust GEEN
            // stap in de "alles"-keten (LLM-zwaar, rb-ai-afhankelijk; expliciete
            // beheerdersbeslissing, zelfde lijn als "graph"/"reason"/"claims"). De
            // extractie is de eerste live rb-ai-koppeling van de fase-2/5-vorm; de
            // promotie-poort en atomaire persistentie zijn al bewezen (ReifiedInteractionTests).
            // Canonieke entiteitenlaag vullen (#250): het ENIGE pad dat
            // CanonicalEntity-rijen aandraagt. De mining resolveert bewust alleen
            // (leest), dus zonder deze stap blijft de entiteitenlaag leeg en vindt
            // "breinmine-predicaten" nul subjects. Deterministisch (vocabulaire +
            // regeltekst, geen LLM), idempotent en goedkoop — daarom staat hij
            // logisch vóór de twee mining-jobs en als eerste brein-stap in de
            // nachtrun.
            new("breinentiteiten", BreinRegisterEntitiesAsync),
            new("breinmine-interacties", BreinMineInteractionsAsync),
            new("breinmine-predicaten", BreinMinePredicatesAsync),
            // Nachtrun (#245): de volledige ONGECAPTE keten in één job —
            // "alles bijwerken" (met ongecapte mechaniek-mining) → brein-interacties
            // → brein-predicaten → projectie → reason. Draait automatisch in het
            // nachtvenster (ScanScheduler, 00:00–11:00 lokaal, default) én is
            // handmatig te starten. Deadline = venster-einde als binnen het venster
            // gestart, anders geen deadline (handmatige volledige drain). Overdag
            // blijven de losse jobs hierboven gecapt; dit is de enige uncapped route.
            new("nachtrun", RunNightlyAsync),
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
            // Changeconsolidatie (#206): changes die hetzelfde event vanuit
            // meerdere bronnen melden (bv. een officiële én een
            // community-ban-update) samenvoegen tot één primaire kaart met
            // bevestiging(en) — draait ná "classify" (ChangeType/Summary
            // moeten al ingevuld zijn) in het ingest-pad.
            new("consolidatechanges", ConsolidateChangesAsync),
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
            // Relatie-triage (#199 v1): LLM-aanbeveling (accept/reject/unsure
            // + motivering) per open relatievoorstel — een aanbevelings-
            // machine, geen autoriteitspad (zie RelationTriageService). Loopt
            // ná "relations" (er moet iets te triageren zijn) en vóór "graph"
            // (de graph-projectie kijkt alleen naar Status, niet naar de
            // aanbeveling — de volgorde is dus geen harde afhankelijkheid,
            // maar wel de logische plek in het kennis-pad).
            new("relationtriage", RelationTriageAsync),
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
            // Gerichte brein-mining-reset (#263): zet ALLEEN de mining-laag terug
            // (interacties + hun watermark; de tweede knop ook de canonieke
            // entiteiten/predicaten) zodat een verbeterde extractie dezelfde pool
            // opnieuw kan minen. Twee losse namen i.p.v. een verborgen modus-vlag —
            // zelfde keuze als "benchmark" vs. "benchmarksweep" hierboven: de scope
            // blijft zichtbaar in het jobs-paneel én in het run_log. Bewust GEEN
            // stap in "all", een pad of de nachtrun (destructief, expliciet).
            new("breinreset-interacties", (sp, report, ct) =>
                BreinResetAsync(sp, report, BreinResetScope.Interactions, ct)),
            new("breinreset-volledig", (sp, report, ct) =>
                BreinResetAsync(sp, report, BreinResetScope.InteractionsAndEntities, ct)),
        }.ToDictionary(j => j.Name);

    private static async Task<JobOutcome> RunAllAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct,
        int mechanicMaxBatches = 25, DateTimeOffset? deadline = null)
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
                maxBatches: mechanicMaxBatches, deadline: deadline,
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

    /// <summary>Nachtrun (#245): de volledige ONGECAPTE kennis-keten in één job.
    /// Deadline = venster-einde (<see cref="NightlyRunSettings"/>) als binnen het
    /// venster gestart, anders geen deadline (handmatige volledige drain — bv. de
    /// beheer-knop overdag). Elke stap best-effort in volgorde: alles bijwerken
    /// (ongecapte mechaniek-mining) → canonieke entiteiten (#250) → brein-interacties
    /// → brein-predicaten → projectie → reason. De mining-services stoppen zelf netjes op de deadline;
    /// hun watermark bewaart de voortgang voor de volgende nacht.</summary>
    private static async Task<JobOutcome> RunNightlyAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<NightlyRunSettings>();
        var tz = Domain.NightlyWindow.ResolveTimeZone(settings.TimeZoneId);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline =
            Domain.NightlyWindow.InWindow(now, tz, settings.StartHour, settings.EndHour)
                ? Domain.NightlyWindow.Deadline(now, tz, settings.EndHour)
                : null;

        var parts = new List<string>();
        async Task Stage(string label, Func<Task<string>> run)
        {
            report($"nachtrun · {label}");
            try { parts.Add($"{label}: {await run()}"); }
            catch (Exception ex) { parts.Add($"{label}: FOUT — {ex.Message}"); }
        }

        // 1. Alles bijwerken, met ONGECAPTE mechaniek-mining + deadline.
        await Stage("alles bijwerken (ongecapt)", async () =>
            (await RunAllAsync(sp, p => report($"nachtrun · {p}"), ct,
                mechanicMaxBatches: Domain.NightlyWindow.UncappedBatches, deadline: deadline)).Detail);
        // 2. Canonieke entiteiten registreren (#250) — deterministisch en goedkoop,
        // maar wél de voorwaarde voor stap 4: zonder entiteiten vindt de predicaat-
        // mining nul subjects. Draait ná de kaart-sync (nieuwe set = nieuwe keywords)
        // en vóór de mining, die alleen tegen deze laag resolveert.
        await Stage("canonieke entiteiten", async () =>
            (await sp.GetRequiredService<EntityResolutionService>().RegisterExistingMechanicsAsync(
                Domain.CanonicalEntityKinds.Keyword, p => report($"nachtrun · {p}"), ct)).Summary);
        // 3. Brein-interacties, ongecapt.
        await Stage("brein-interacties (ongecapt)", async () =>
            (await sp.GetRequiredService<BreinInteractionMiningService>().RunAsync(
                maxFocusCards: Domain.NightlyWindow.UncappedItems, deadline: deadline,
                progress: p => report($"nachtrun · {p}"), ct: ct)).Summary);
        // 4. Brein-predicaten, ongecapt.
        await Stage("brein-predicaten (ongecapt)", async () =>
            (await sp.GetRequiredService<BreinPredicateMiningService>().RunAsync(
                maxSubjects: Domain.NightlyWindow.UncappedItems, deadline: deadline,
                progress: p => report($"nachtrun · {p}"), ct: ct)).Summary);
        // 5. Projectie + reason (Neo4j, geen LLM) op wat er nu staat.
        await Stage("brein-projectie", async () =>
            (await sp.GetRequiredService<BreinProjectionService>().ProjectAsync(progress: report, ct: ct)).Summary);
        await Stage("reason", async () =>
            (await sp.GetRequiredService<ReasoningService>().RunAsync(progress: report, ct: ct)).Summary);

        return new(string.Join(" · ", parts));
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
        // #190 (review-fix): vers-werk-semantiek. Remaining telt óók de
        // zojuist gefaalde kaarten mee (Mechanics blijft null) — een kale
        // Remaining==0 zou een pad-drain bij rb-ai-uitval of een poison card
        // tot MaxRepeats futiele herhalingen kosten. Gedraineerd = geen
        // kaarten meer die deze run niet eens aan de beurt kwamen; failures
        // zijn geen verse werklast (een directe herhaling faalt vrijwel
        // zeker opnieuw — de volgende run/tick probeert ze gewoon nog eens).
        return new($"{r.Mined} kaarten gemined, {r.Remaining} resterend",
            Drained: r.Remaining - r.Failed <= 0);
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
            + $"{r.Relations} relaties, {r.MiningRuns} runs, {r.Assertions} assertions");
    }

    private static async Task<JobOutcome> BreinProjectionAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("brein-lagen (canonieke entiteiten, mechanic-predicaten, ontologie-versies) naar Neo4j projecteren");
        var r = await sp.GetRequiredService<BreinProjectionService>().ProjectAsync(progress: report, ct: ct);
        return new(r.Summary);
    }

    private static async Task<JobOutcome> ReasonAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        report("monotone inferentie draaien + bounded contradictie-detectie");
        var r = await sp.GetRequiredService<ReasoningService>().RunAsync(progress: report, ct: ct);
        return new(r.Summary);
    }

    private static Task<JobOutcome> OwlAuditAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        // Pure zelf-toets tegen de afgedwongen schema-bron — geen IO, geen Neo4j.
        report("ontologie-consistentie toetsen (OntologySchema)");
        var findings = Domain.Reasoning.OntologyConsistencyAudit.Run();
        var detail = findings.Count == 0
            ? "ontologie consistent (geen bevindingen)"
            : $"{findings.Count} bevinding(en): " +
              string.Join("; ", findings.Select(f => $"{f.Code} — {f.Message}"));
        return Task.FromResult(new JobOutcome(detail));
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

    private static async Task<JobOutcome> BreinRegisterEntitiesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<EntityResolutionService>()
            .RegisterExistingMechanicsAsync(Domain.CanonicalEntityKinds.Keyword, report, ct);
        // Ongecapt: het vocabulaire is klein (tientallen termen) en de stap doet
        // geen LLM-werk — één run verwerkt altijd alles.
        return new(r.Summary);
    }

    private static async Task<JobOutcome> BreinMineInteractionsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BreinInteractionMiningService>()
            .RunAsync(progress: report, ct: ct);
        // #190 vers-werk-semantiek: per-run gecapt; CapHit → nog focus-kaarten over.
        return new(r.Summary, Drained: !r.CapHit);
    }

    private static async Task<JobOutcome> BreinMinePredicatesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BreinPredicateMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Summary, Drained: !r.CapHit);
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
        // #190 (review-fix): ClassifyPendingAsync is ONgecapt — één run
        // verwerkt de hele backlog, dus wat na de run resteert zijn precies
        // de zojuist gefaalde items (plus eventueel tussentijds binnengekomen
        // changes). Kale Remaining==0 zou bij rb-ai-uitval een pad-drain de
        // volledige falende backlog MaxRepeats keer laten herkauwen.
        // Vers-werk-semantiek: alleen niet-geprobeerde items tellen.
        return new(
            $"{r.Classified} changes alsnog geclassificeerd, {r.Failed} mislukt, {r.Remaining} resterend",
            Drained: r.Remaining - r.Failed <= 0);
    }

    private static async Task<JobOutcome> ConsolidateChangesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<ChangeConsolidationService>().RunAsync(progress: report, ct: ct);
        return new(r.Message);
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
        // #188 increment 3: de anker-herstel-pas draait als tweede stap van
        // dezelfde job — hij werkt de bestaande pending-achterstand af
        // ("onderwerp niet herkend", issue #199: 117/133 items) die de
        // extractiestap hierboven niet meer aanraakt (die items zijn al
        // eerder gemíned). Eigen cap/CapHit, meegeteld in Drained zodat het
        // #190-drain-pad ook deze achterstand tot nul afwerkt.
        var repair = await sp.GetRequiredService<CorrectionReevaluationService>()
            .RepairPendingAnchorsAsync(progress: report, ct: ct);
        return new($"{r.Message} · {repair.Message}", Drained: !r.CapHit && !repair.CapHit);
    }

    private static async Task<JobOutcome> RelationsAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<RelationMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Message, Drained: !r.CapHit);
    }

    private static async Task<JobOutcome> RelationTriageAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<RelationTriageService>()
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
        // #190 (review-fix): de job is per-run gecapt (max pagina's) en het
        // result meldt dat al machine-leesbaar — doorgeven i.p.v. discarden.
        return new(r.Message, Drained: !r.CapHit);
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

    private static async Task<JobOutcome> BreinResetAsync(
        IServiceProvider sp, Action<string> report, BreinResetScope scope, CancellationToken ct)
    {
        report(scope == BreinResetScope.InteractionsAndEntities
            ? "brein-mining-laag terugzetten (interacties, watermark, canonieke entiteiten en predicaten)"
            : "brein-mining-laag terugzetten (interacties en het mined-watermark)");
        var r = await sp.GetRequiredService<BreinMiningResetService>().ResetAsync(scope, ct);
        return new(r.Message);
    }
}
