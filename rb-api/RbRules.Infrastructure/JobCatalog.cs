using Microsoft.Extensions.DependencyInjection;

namespace RbRules.Infrastructure;

/// <summary>Definitie van een admin-achtergrondjob: naam + uit te voeren werk.
/// Het werk krijgt een scoped IServiceProvider (JobRunner opent de scope),
/// een voortgangs-reporter en een token; het resultaat is <see
/// cref="JobOutcome"/> (detail-regel voor run_log/admin + of de job
/// gedraineerd is).
///
/// <paramref name="RunUncapped"/> (#258) is de optionele ONGECAPTE variant van
/// dezelfde job: geen per-run cap, wel een deadline. Alleen de dure miners
/// hebben er een — zij zijn de enige jobs met een per-run budget dat de
/// nachtrun juist wil loslaten. Het bestaat als aparte delegate en niet als
/// vlag op <paramref name="Run"/> omdat alleen die paar jobs een zinvolle
/// invulling hebben; de rest doet sowieso zijn hele werklast in één run.</summary>
public sealed record JobDefinition(
    string Name,
    Func<IServiceProvider, Action<string>, CancellationToken, Task<JobOutcome>> Run,
    Func<IServiceProvider, Action<string>, DateTimeOffset?, CancellationToken, Task<JobOutcome>>?
        RunUncapped = null);

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
            // Sinds #258 een DUNNE ALIAS op een pad (JobPaths.AllUpdate) i.p.v.
            // een eigen, met de hand geschreven keten: dezelfde stappen, maar nu
            // met een run_log-regel per stap en drain op de gecapte miners. De
            // naam blijft "all" — rb-web, de docs en het run_log-grootboek kennen
            // hem, en het pad-mechanisme zit erachter, niet ervoor.
            new("all", (sp, report, ct) =>
                PathRunner.RunAsync(JobPaths.AllUpdate, sp, report, ct)),
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
            new("mine", MineAsync, MineUncappedAsync),
            new("rules", RulesAsync),
            // Incrementele regelindexering (#258): álleen nieuwe/gewijzigde
            // documenten (force:false). Dit is wat de ketens nodig hebben — de
            // losse "rules"-knop hierboven herbouwt bewust ALLES (force:true, na
            // een parser-verbetering) en zou in een nachtelijke keten elke nacht
            // de complete regelindex her-chunken én her-embedden voor niets.
            // Twee namen i.p.v. een verborgen modus-vlag, zelfde keuze als
            // "scan" vs. "feeds" en "benchmark" vs. "benchmarksweep".
            new("rules-index", RulesIndexAsync),
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
            // De OWL2-RL-audit was hier een job (#227) maar las geen data en
            // raakte geen database: hij kon alleen falen als de GECOMPILEERDE
            // OntologySchema intern inconsistent was. Dat is een unit-test, geen
            // beheerdersactie — sinds #258 draait hij als CI-assert
            // (ContradictionDetectorTests), waar hij een kapot schema
            // tegenhoudt vóór de merge in plaats van erna.
            new("primer", PrimerAsync),
            // LEGACY (#258): de paar-lexicale, conditie-loze interactie-miner uit
            // S3, inhoudelijk opgevolgd door BreinInteractionMiningService (de
            // gereïficeerde, gekwalificeerde interacties). Bewust UIT elke keten
            // gehaald — hij kostte elke nachtrun LLM-budget dat de opvolger nodig
            // heeft, en beide vechten om dezelfde rb-ai-semafoor. Blijft
            // registreerd (dus handmatig startbaar) zolang het leespad nog op de
            // oude tabel terugvalt; zie InteractionService.NeighborsAsync voor de
            // migratiebrug en het criterium waarop deze job weg mag.
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
            new("breinmine-interacties", BreinMineInteractionsAsync, BreinMineInteractionsUncappedAsync),
            new("breinmine-predicaten", BreinMinePredicatesAsync, BreinMinePredicatesUncappedAsync),
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

    /// <summary>Nachtrun (#245; sinds #258 een PAD): de volledige ONGECAPTE keten
    /// (<see cref="JobPaths.Nightly"/>) door <see cref="PathRunner"/>. Deze job is
    /// nog maar één ding: hij bepaalt de DEADLINE en geeft die aan het pad mee.
    ///
    /// Deadline = venster-einde (<see cref="NightlyRunSettings"/>) als binnen het
    /// venster gestart, anders geen deadline (handmatige volledige drain — bv. de
    /// beheer-knop overdag). De ongecapte stappen krijgen 'm mee; de
    /// mining-services stoppen zelf netjes op de deadline en hun watermark bewaart
    /// de voortgang voor de volgende nacht.
    ///
    /// Wat #258 hier oplost: de keten stond met de hand uitgeschreven in dit
    /// bestand (RunNightlyAsync + RunAllAsync), met eigen volgorde, eigen
    /// best-effort-afhandeling en ZONDER per-stap-run_log. Nu erft de nachtrun de
    /// drain-semantiek, de per-stap-historie en de afbreek-afhandeling van het
    /// pad-mechanisme, en staat de volgorde op één plek (JobPaths).</summary>
    private static async Task<JobOutcome> RunNightlyAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        // Op het gebruiksmoment gelezen (#254): een venster dat in beheer is bijgesteld
        // geldt meteen voor de eerstvolgende run, ook zonder herstart.
        var settings = await sp.GetRequiredService<ManagedSettingsService>().NightlyAsync(ct);
        var tz = Domain.NightlyWindow.ResolveTimeZone(settings.TimeZoneId);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline =
            Domain.NightlyWindow.InWindow(now, tz, settings.StartHour, settings.EndHour)
                ? Domain.NightlyWindow.Deadline(now, tz, settings.EndHour)
                : null;

        return await PathRunner.RunAsync(
            JobPaths.Nightly, sp, report, ct, deadline: deadline);
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

    /// <summary>Ongecapte mechaniek-mining (#258): de per-run batch-cap eraf, de
    /// nachtrun-deadline erin. Zelfde service, zelfde vers-werk-semantiek — alleen
    /// het budget verschilt, precies wat de nachtrun van deze stap wil.</summary>
    private static Task<JobOutcome> MineUncappedAsync(
        IServiceProvider sp, Action<string> report, DateTimeOffset? deadline, CancellationToken ct) =>
        MineAsync(sp, report, ct, Domain.NightlyWindow.UncappedBatches, deadline);

    private static Task<JobOutcome> MineAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct) =>
        MineAsync(sp, report, ct, maxBatches: 25, deadline: null);

    private static async Task<JobOutcome> MineAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct,
        int maxBatches, DateTimeOffset? deadline)
    {
        var r = await sp.GetRequiredService<MechanicMiningService>()
            .RunAsync(maxBatches: maxBatches, deadline: deadline, progress: report, ct: ct);
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

    /// <summary>Incrementele regelindexering (#258): alleen documenten die nog
    /// geen chunks hebben — het ketengedrag dat de oude RunAllAsync inline had
    /// (force:false). Zie de registratie hierboven voor waarom dit een eigen
    /// naam is en niet een modus op "rules".</summary>
    private static async Task<JobOutcome> RulesIndexAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<RuleChunkPipeline>()
            .RunAsync(force: false, report, ct);
        return new($"{r.Sum(x => x.Chunks)} sectie-chunks over {r.Count} nieuwe/gewijzigde bronnen");
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

    private static async Task<JobOutcome> PrimerAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<PrimerService>()
            .GenerateAsync(progress: report, ct: ct);
        return new($"{r.Written} primer-docs geschreven (drafts), {r.Skipped} goedgekeurd gelaten, "
            + $"{r.Failed} mislukt, {r.Untranslated} zonder NL-weergave (tonen het Engels)");
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

    /// <summary>Ongecapte brein-interactie-mining (#258): alle focus-kaarten binnen
    /// de nachtrun-deadline i.p.v. de per-run cap.</summary>
    private static async Task<JobOutcome> BreinMineInteractionsUncappedAsync(
        IServiceProvider sp, Action<string> report, DateTimeOffset? deadline, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BreinInteractionMiningService>().RunAsync(
            maxFocusCards: Domain.NightlyWindow.UncappedItems, deadline: deadline,
            progress: report, ct: ct);
        return new(r.Summary, Drained: !r.CapHit);
    }

    private static async Task<JobOutcome> BreinMinePredicatesAsync(
        IServiceProvider sp, Action<string> report, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BreinPredicateMiningService>()
            .RunAsync(progress: report, ct: ct);
        return new(r.Summary, Drained: !r.CapHit);
    }

    /// <summary>Ongecapte brein-predicaat-mining (#258).</summary>
    private static async Task<JobOutcome> BreinMinePredicatesUncappedAsync(
        IServiceProvider sp, Action<string> report, DateTimeOffset? deadline, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<BreinPredicateMiningService>().RunAsync(
            maxSubjects: Domain.NightlyWindow.UncappedItems, deadline: deadline,
            progress: report, ct: ct);
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
