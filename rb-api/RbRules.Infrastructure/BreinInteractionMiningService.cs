using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één brein-interactie-mining-run: het aantal verwerkte
/// mechanic-subjecten en focus-kaarten, de door de poort gehaalde interacties per
/// tier, en of de per-run-cap geraakt is (drain-signaal, #190).</summary>
/// <param name="MechanicSubjects">Verwerkte mechanic-subjecten uit de
/// mechanic-niveau-vraag (#286) — de goedkope pass die mech↔mech dekt.</param>
/// <param name="CallMetrics">De ingebouwde meting (#286): aanroepen, gemiddelde
/// wandkloktijd en gemiddeld aantal aangeboden refs per fase.</param>
public sealed record BreinInteractionMiningResult(
    int FocusCards, int Extracted, int Promoted, int Candidates,
    int Hypothesized, int Rejected, int Failed, bool CapHit,
    int SkippedKnown = 0, string? FailureDetail = null,
    int MechanicSubjects = 0, string? CallMetrics = null)
{
    public string Summary =>
        $"{MechanicSubjects} mechanics + {FocusCards} kaarten, {Extracted} kandidaten geëxtraheerd → " +
        $"{Promoted} gepromoveerd, {Candidates} kandidaat, {Hypothesized} hypothese, " +
        $"{Rejected} verworpen, {SkippedKnown} al-bekend (kaart↔eigen-keyword), " +
        $"{Failed} rb-ai-uitval" +
        (string.IsNullOrEmpty(FailureDetail) ? "" : $" ({FailureDetail})") +
        (string.IsNullOrEmpty(CallMetrics) ? "" : $"; {CallMetrics}");
}

/// <summary>Brein-mining-orkestratie voor gekwalificeerde interacties (#226, §3.1 +
/// §3.4). ADDITIEF naast de bestaande <see cref="InteractionService"/>/<see cref="InteractionMiner"/>
/// (lexicaal-paar-gebaseerd, conditie-loos): deze pijplijn haalt tool-forced,
/// ontologie-begrensde kandidaten bij rb-ai (<c>/extract/interactions</c>,
/// spiegelt <see cref="InteractionExtraction"/>), entity-resolvet de rol-refs (fase 1)
/// VÓÓR kandidaat-creatie, en laat elke kandidaat door de fase-2-promotie-poort
/// (<see cref="InteractionPromotionService"/>) — schema ∧ (lexicaal ∨ consensus) ∧
/// verdict, met de cold-start-tier voor emergente card×card-hypotheses. Feit +
/// provenance worden atomair door die service gepersisteerd; deze klasse voegt geen
/// eigen graaf-write toe.
///
/// Degradatie is het verwachte pad: rb-ai null → dat item wordt overgeslagen
/// (Failed++), er wordt GEEN half feit geschreven, en de job rondt netjes af. De
/// selectie is bewust bounded (cap per run) en idempotent via de dedupe-sleutel van
/// de promotie-poort — herhaald draaien maakt geen duplicaten.
///
/// <b>Herijkt in #249</b> (tautologie): kaart↔eigen-keyword wordt niet meer geminded,
/// de aanbieding draagt buurt-keywords + regelsecties, en de lexicale poort eist dat
/// het bewijs een RELATIE uitdrukt. De deterministische graph-projectie blijft
/// ONGEMOEID: kaart→mechanic-edges bestaan gewoon door, ze komen alleen niet meer uit
/// een dure LLM-omweg.
///
/// <b>PARALLEL sinds #279</b>: de kosten zitten vrijwel volledig in de rb-ai-call per
/// item, dus items gaan met meerdere workers tegelijk (<see cref="BreinMiningSettings.
/// Concurrency"/>), elk met een eigen <see cref="RbRulesDbContext"/> uit de
/// <see cref="IDbContextFactory{TContext}"/> (DbContext is niet thread-safe), met de
/// promotie-poort achter één schrijf-slot (lees-dan-schrijf op een unieke index
/// verdraagt geen gelijktijdigheid) en een volgorde-onafhankelijke uitkomst. Zonder
/// factory (unit-tests op EF InMemory) valt de lus terug op één worker en exact het
/// oude sequentiële pad.
///
/// <b>Herijkt in #286 — de vraag zelf.</b> De meting op productie was ondubbelzinnig:
/// zelfde kaarttekst, alleen het aangeboden vocabulaire verschilt. 3 refs → 200 na
/// 49,0s; 39 refs → afgekapt na 92,1s (de 90s-timeout van rb-ai). Wat de duur drijft
/// is het AANTAL AANGEBODEN REFS, niet de kaarttekst; de 49s bij 3 refs is grotendeels
/// vaste SDK-opstartkost. Dezelfde sidecar en hetzelfde model deden de
/// predicaat-extractie (kort subject + definitie) in ~9,5s met 0% uitval, tegenover
/// ~33s en 45-55% uitval hier. Drie gevolgen, alle drie doorgevoerd:
/// <list type="number">
/// <item><b>Minder vocabulaire per kaart-aanroep.</b> De oude aanbieding stuurde de
/// keywords van de HELE buurt mee (focus + 4 partners), in de praktijk 39 refs. Nu
/// bepaalt <see cref="InteractionOffering"/> deterministisch wat relevant is: de
/// gedrukte keywords van de kaart plus alleen die buren die aantoonbaar samen met zo'n
/// gedrukt keyword in één AANGEBODEN bewijs-eenheid staan, onder een harde begroting
/// (<see cref="OfferingLimits.Card"/>). Dat is geen bezuiniging maar de enige vorm die
/// meeschaalt: het vocabulaire groeit met elke set, dus een ongelimiteerde aanbieding
/// is een schaalklip. De oplopende uitval (45 → 47 → 55%) bevestigde dat — een gefaalde
/// kaart krijgt geen watermark, komt terug, en de pool verzwaart zichzelf.</item>
/// <item><b>De vraag op mechanic-niveau waar dat kan.</b> 38 mechanics tegenover 1311
/// kaarten, en "Equip modificeert Might" geldt voor élke kaart met [Equip]. De
/// mechanic-pass (<see cref="MineMechanicAsync"/>) draait daarom VÓÓR de kaart-pass en
/// dekt mech↔mech in ~35× minder aanroepen. Zie de dekkings-paragraaf hieronder: hij
/// VERVANGT de kaart-pass niet.</item>
/// <item><b>Rijkere vraag per aanroep.</b> Omdat de vaste kosten toch betaald zijn, is
/// een extra veld over tekst die al in de prompt staat vrijwel gratis. De extractie
/// vraagt nu ook naar <c>governed_by</c> — welke aangeboden regelsectie de interactie
/// normatief verankert — als gesloten enum. Dat vult
/// <see cref="Interaction.GovernedByRef"/>, dat sinds #226 bestond maar altijd null
/// bleef.</item>
/// </list>
///
/// <b>Dekking: wat mechanic-niveau NIET dekt.</b> Expliciet benoemd, want stilzwijgend
/// inleveren is precies de val waar deze codebase al twee keer in liep. De
/// mechanic-pass biedt UITSLUITEND keyword-rollen aan (subject + directe buren);
/// kaarten zijn er bewijs, geen rol. Hij kan dus per constructie niet vinden:
/// <list type="bullet">
/// <item>kaart↔kaart-interacties — de emergente cold-start-hypotheses;</item>
/// <item>kaart↔ander-kaarts-keyword — een kaart die een keyword beïnvloedt zonder het
/// zelf te dragen.</item>
/// </list>
/// Daarom BLIJFT de kaart-pass draaien, met alle roltypen die hij had. Wat er wél
/// verandert is dat een kaart-aanroep een kleiner vocabulaire ziet; de mech↔mech-paren
/// die daardoor bij een individuele kaart wegvallen worden door de mechanic-pass
/// uitputtender gedekt dan één kaart ze ooit kon aanbieden (elk subject krijgt zijn
/// eigen aanroep mét zijn directe buren). Eén restgat blijft en is geen regressie: een
/// mech↔mech-paar waarvan de twee leden NERGENS samen voorkomen — geen gedeelde kaart,
/// geen gedeelde regelsectie — wordt door geen van beide passes aangeboden. Dat paar
/// was voorheen alleen bij toeval bereikbaar en had per definitie geen bewijs.
///
/// <b>Meten, niet gokken</b> (acceptatiecriterium #286): elke rb-ai-call wordt geteld
/// mét zijn wandkloktijd en het aantal aangeboden refs, per fase, en dat komt als
/// <see cref="BreinInteractionMiningResult.CallMetrics"/> in het run-detail. Let op de
/// woordkeus: wandkloktijd, niet denktijd — de Claude Agent SDK retryt intern tot 10×
/// met backoff, dus een trage call kan interne herhalingen bevatten.</summary>
public class BreinInteractionMiningService(
    RbRulesDbContext db, RbAiClient ai, EntityResolutionService entityResolution,
    InteractionPromotionService promotion,
    IDbContextFactory<RbRulesDbContext>? dbFactory = null,
    BreinMiningSettings? settings = null)
{
    private const int DefaultMaxFocusCards = 40;
    private const int DefaultMaxPartners = 3;
    private const int DefaultMaxMechanicSubjects = 40;

    /// <summary>Hoeveel regelsecties de planner per item ter overweging krijgt. Een
    /// bovengrens op het VOORWERK, niet op de aanbieding zelf (dat is
    /// <see cref="OfferingLimits.MaxSections"/>): zonder deze grens loopt de
    /// paar-diversiteitscheck over het hele officiële corpus.</summary>
    private const int MaxSectionCandidates = 12;

    /// <summary>Hoeveel kaarten die de mechaniek dragen de planner ter overweging
    /// krijgt (mechanic-niveau). Zie <see cref="MaxSectionCandidates"/>.</summary>
    private const int MaxCarrierCandidates = 8;

    /// <summary>Harde bovengrens op het aantal refs in één kaart-aanroep — de meting
    /// uit #286 in code. Publiek omdat de regressietest hem als plafond gebruikt:
    /// zodra er weer een ongelimiteerd vocabulaire meegaat, valt die test om.</summary>
    public static int MaxOfferedRefsPerCard => OfferingLimits.Card.MaxRefs;

    /// <summary>De prompt-versie-stempel op de <see cref="MiningRun"/> — bump bij een
    /// wijziging aan de extractie-prompt/vorm (stale-conditie voor her-mining, §3.5).
    /// v3 (#286): begrensde, relevantie-gestuurde aanbieding, een mechanic-niveau-pass
    /// en een governed_by-vraag in dezelfde aanroep.</summary>
    public const string PromptVersion = "breinmine-interactions-v3";

    public async Task<BreinInteractionMiningResult> RunAsync(
        int maxFocusCards = DefaultMaxFocusCards, int maxPartners = DefaultMaxPartners,
        int maxMechanicSubjects = DefaultMaxMechanicSubjects,
        DateTimeOffset? deadline = null, Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var windowLexicon = InteractionQualifierLexicon.Windows;
        var statusLexicon = InteractionQualifierLexicon.Statuses;

        // Voortgangs-watermark (#226-review defect 1/2; herzien in #249-review).
        //
        // Het watermark kwam uit de Assertion-provenance (DERIVED_FROM = card:X,
        // FactKind = interaction). Die proxy kan het noodzakelijke onderscheid
        // principieel niet maken: een Assertion ontstaat ALLEEN op het accept-pad, dus
        // elke kaart die niets promoveerde liet géén spoor achter en bleef eeuwig aan
        // de kop van de wachtrij staan. Nu een EXPLICIETE markering per kaart
        // (Card.InteractionsMinedAt); het oude Assertion-watermark loopt als achtervang
        // mee zodat de al-verwerkte productiekaarten na deploy niet één keer gratis
        // opnieuw gemined worden.
        var minedRefs = await db.Assertions.AsNoTracking()
            .Where(a => a.FactKind == FactKinds.Interaction)
            .Select(a => a.DerivedFromRef)
            .Distinct()
            .ToListAsync(ct);
        var minedIds = minedRefs
            .Select(r => BrainRef.TryParse(r, out var br) && br.Kind == BrainRefKind.Card ? br.Key : null)
            .Where(k => k is not null).Select(k => k!)
            .Distinct().ToList();

        // Focus-kaarten: canonieke printings met tekst die keywords dragen (die tekst
        // ís het bewijsanker), minus de al-verwerkte. Bounded per run; herhaald draaien
        // is idempotent én schuift op.
        var focus = await db.Cards
            .Where(c => c.VariantOf == null && c.Mechanics != null
                        && c.TextPlain != null && c.TextPlain != ""
                        && c.InteractionsMinedAt == null
                        && !minedIds.Contains(c.RiftboundId))
            .OrderBy(c => c.RiftboundId)
            .Take(maxFocusCards + 1)
            .ToListAsync(ct);
        var cardCapHit = focus.Count > maxFocusCards;
        if (cardCapHit) focus = focus.Take(maxFocusCards).ToList();

        // Mechanic-subjecten (#286): levende mechanic/keyword-entiteiten zonder
        // watermark. Ze zijn met tientallen, niet duizenden — de goedkope pass.
        var subjects = await db.CanonicalEntities.AsNoTracking()
            .Where(e => e.Status != CanonicalEntityStatus.Merged
                        && (e.Kind == CanonicalEntityKinds.Mechanic
                            || e.Kind == CanonicalEntityKinds.Keyword)
                        && e.InteractionsMinedAt == null)
            .OrderBy(e => e.Id)
            .Take(maxMechanicSubjects + 1)
            .ToListAsync(ct);
        var mechanicCapHit = subjects.Count > maxMechanicSubjects;
        if (mechanicCapHit) subjects = subjects.Take(maxMechanicSubjects).ToList();

        if (focus.Count == 0 && subjects.Count == 0)
            return new(0, 0, 0, 0, 0, 0, 0, false);

        // Het volledige levende keyword-vocabulaire: de kandidaat-buren van de
        // mechanic-pass. Dit is de lijst die met elke set groeit — precies waarom hij
        // per aanroep begrensd móet worden en niet integraal meegestuurd mag worden.
        var vocabulary = await db.CanonicalEntities.AsNoTracking()
            .Where(e => e.Status != CanonicalEntityStatus.Merged
                        && (e.Kind == CanonicalEntityKinds.Mechanic
                            || e.Kind == CanonicalEntityKinds.Keyword))
            .OrderBy(e => e.CanonicalLabel)
            .Select(e => e.CanonicalLabel)
            .ToListAsync(ct);

        // Kaart-pool één keer laden (versla defect 5): zowel de partner-buurt
        // (kaart-niveau) als de dragende kaarten (mechanic-niveau) worden hier
        // in-memory uit gefilterd i.p.v. de kaarttabel per item opnieuw te
        // materialiseren.
        var cardPool = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Mechanics != null
                        && c.TextPlain != null && c.TextPlain != "")
            .OrderBy(c => c.RiftboundId)
            .Select(c => new CardLite(c.RiftboundId, c.Name, c.Type, c.Mechanics, c.TextPlain))
            .ToListAsync(ct);

        // Officiële regeltekst als bewijsbron (#249): één keer geladen, per item
        // in-memory gefilterd. Dit is de bron waar keyword↔keyword-relaties
        // daadwerkelijk staan opgeschreven — de kaartteksten alleen leverden vrijwel
        // geen mech↔mech op.
        //
        // Alléén trust-tier-1-bronnen (#249-review): rule_chunks bevat ook de
        // community-gidsen (trust 3), en een parafrase daaruit zou als "bewijszin
        // gevonden" een LLM-voorstel direct promoveren. Dat breekt de kennislagen
        // (officieel > … > community, docs/KNOWLEDGE.md).
        var officialSourceIds = await db.Sources.AsNoTracking()
            .Where(s => s.TrustTier == 1)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var ruleSections = await db.RuleChunks.AsNoTracking()
            .Where(c => c.Text != "" && officialSourceIds.Contains(c.SourceId))
            .OrderBy(c => c.SourceId).ThenBy(c => c.ChunkIndex)
            .Select(c => new SectionLite(c.SourceId, c.SectionCode, c.Text))
            .ToListAsync(ct);

        var run = await StartRunAsync(windowLexicon, statusLexicon, ct);

        var tally = new RunTally();
        // Schrijf-slot om de promotie-poort (#279): zie de klasse-samenvatting —
        // lees-dan-schrijf op een unieke sleutel verdraagt geen gelijktijdigheid.
        using var gate = new SemaphoreSlim(1, 1);
        // Resolutie-cache over de hele run: de mapping surface → canoniek label is
        // context-onafhankelijk, dus workers mogen hem delen. Scheelt duizenden
        // identieke resolve-queries op een pool met veel herhaalde keywords.
        var labelCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var context = new PassContext(
            run.Id, cardPool, ruleSections, vocabulary, windowLexicon, statusLexicon,
            tally, gate, labelCache);

        // FASE 1 — mechanic-niveau. Bewust eerst: klein van payload, en de mech↔mech-
        // kennis die hij oplevert hoeft de kaart-pass daarna niet meer te ontdekken.
        await RunPhaseAsync(
            subjects.Count, deadline,
            (index, ctx, resolution, gatekeeper, token) => MineMechanicAsync(
                subjects[index], ctx, gatekeeper, context, token),
            i => progress?.Invoke($"mechanic-interacties extraheren via rb-ai: {i}/{subjects.Count}"),
            tally.CountMechanic, tally, ct);

        // FASE 2 — kaart-niveau, met de begrensde aanbieding.
        var limits = OfferingLimits.Card with { MaxPartnerCards = Math.Max(0, maxPartners) };
        await RunPhaseAsync(
            focus.Count, deadline,
            (index, ctx, resolution, gatekeeper, token) => MineCardAsync(
                focus[index], ctx, resolution, gatekeeper, context, limits, token),
            i => progress?.Invoke($"interacties extraheren via rb-ai: {i}/{focus.Count}"),
            tally.CountCard, tally, ct);

        run.Candidates = tally.Extracted;
        run.Verified = tally.Promoted;
        run.Rejected = tally.Rejected;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // FocusCards/MechanicSubjects = daadwerkelijk verwerkt (bij een deadline-stop
        // minder dan gepland). CapHit ⇔ er blijft vers werk liggen: cap geraakt óf
        // deadline afgekapt.
        return new(tally.CardsProcessed, tally.Extracted, tally.Promoted, tally.Candidates,
            tally.Hypothesized, tally.Rejected, tally.Failed,
            cardCapHit || mechanicCapHit || tally.DeadlineHit,
            tally.SkippedKnown, tally.FailureDetail,
            tally.MechanicsProcessed, tally.CallMetrics);
    }

    /// <summary>De worker-lus, één keer geschreven voor beide passes (#286). Workers
    /// trekken hun werk uit een gedeelde teller — klaar is klaar, dus een traag item
    /// houdt de rest niet op (statisch verdelen zou dat wél doen) — en elk item krijgt
    /// een eigen unit-of-work: verse <see cref="RbRulesDbContext"/> uit de factory, met
    /// de services daarop. Zonder factory (tests) hard terug naar één worker en de
    /// gedeelde scoped context: parallel draaien op één DbContext is geen optimalisatie
    /// maar corruptie.</summary>
    private async Task RunPhaseAsync(
        int itemCount, DateTimeOffset? deadline,
        Func<int, RbRulesDbContext, EntityResolutionService, InteractionPromotionService,
            CancellationToken, Task> mine,
        Action<int> report, Func<int> count, RunTally tally, CancellationToken ct)
    {
        if (itemCount == 0) return;
        var cursor = -1;

        async Task WorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref cursor);
                if (index >= itemCount) return;

                // Nachtrun-deadline (#245): stop netjes op venster-einde — de al
                // verwerkte items dragen hun watermark, de rest volgt de volgende
                // nacht. Elke worker stopt bij zijn eerstvolgende item; wat al draait
                // maakt zijn extractie gewoon af.
                if (deadline is { } dl && DateTimeOffset.UtcNow >= dl)
                {
                    tally.HitDeadline();
                    return;
                }

                report(count());

                await using var owned = dbFactory is null
                    ? null
                    : await dbFactory.CreateDbContextAsync(ct);
                var ctx = owned ?? db;
                await mine(
                    index, ctx,
                    owned is null ? entityResolution : new EntityResolutionService(ctx),
                    owned is null ? promotion : new InteractionPromotionService(ctx),
                    ct);
            }
        }

        var workers = dbFactory is null
            ? 1
            : Math.Clamp((settings ?? BreinMiningSettings.Default).Concurrency, 1, itemCount);
        if (workers == 1)
            await WorkerAsync();
        else
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkerAsync()));
    }

    // ── Fase 1: mechanic-niveau ──────────────────────────────────────────────

    /// <summary>Eén mechanic-subject: aanbieding bouwen → rb-ai → parse → promotie-
    /// poort. Alleen KEYWORD-rollen (subject + directe buren); kaarten en regelsecties
    /// zijn bewijs, nooit rol. Daarmee is de vraag letterlijk "hoe grijpen deze twee
    /// mechanieken in elkaar?" en kan hij niet terugvallen op de kaart↔eigen-keyword-
    /// tautologie die #249 uitroeide — de tautologie-poort heeft hier per constructie
    /// niets te doen.
    ///
    /// Geen entity-resolutie nodig: het subject ÍS een canonieke entiteit (fase 1 is
    /// hier al gebeurd), en de buren komen uit datzelfde canonieke vocabulaire.</summary>
    private async Task MineMechanicAsync(
        CanonicalEntity subject, RbRulesDbContext ctx,
        InteractionPromotionService gatekeeper, PassContext context, CancellationToken ct)
    {
        var label = subject.CanonicalLabel.Trim();
        if (label.Length == 0)
        {
            await MarkEntityMinedAsync(ctx, subject, context.RunId, ct);
            return;
        }

        // Kandidaat-bewijs: kaarten die de mechaniek dragen (canoniek label of alias)
        // en officiële secties die haar noemen. Beide bounded — dit is voorwerk, de
        // planner kiest er de definitieve aanbieding uit.
        var surfaces = new HashSet<string>(
            new[] { subject.CanonicalLabel }.Concat(subject.AltLabels),
            StringComparer.OrdinalIgnoreCase);
        var carriers = context.CardPool
            .Where(c => (c.Mechanics ?? []).Any(m => surfaces.Contains((m ?? "").Trim())))
            .Take(MaxCarrierCandidates)
            .Select(c => new OfferingCard(
                BrainRef.Card(c.RiftboundId).Format(), c.Name, CardEntityType(c.Type),
                c.TextPlain ?? "", []))
            .ToList();
        var sections = SectionCandidates(context.RuleSections, [label]);

        var plan = InteractionOffering.ForMechanic(
            label, context.Vocabulary, carriers, sections, OfferingLimits.Mechanic);

        // Minder dan twee rollen = geen paar om over te redeneren. Dat is een
        // DETERMINISTISCHE uitkomst, geen uitval: opnieuw aanbieden levert opnieuw
        // niets. Wel markeren, anders bezet dit subject eeuwig de wachtrij-kop.
        if (plan.Refs.Count < 2)
        {
            await MarkEntityMinedAsync(ctx, subject, context.RunId, ct);
            return;
        }

        var offer = BuildOffer(plan, subject.Definition);
        var succeeded = await ExtractAndPromoteAsync(
            offer, BrainRef.Mechanic(label).Format(), MiningPhase.Mechanic,
            ownKeywordRefs: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
            gatekeeper, context, ct);

        if (succeeded) await MarkEntityMinedAsync(ctx, subject, context.RunId, ct);
    }

    // ── Fase 2: kaart-niveau ─────────────────────────────────────────────────

    /// <summary>Eén focus-kaart: aanbieding bouwen → rb-ai → parse → promotie-poort.
    /// Draait op <paramref name="ctx"/> (eigen context per kaart in de parallelle
    /// stand) met de services die dáárop staan; raakt de gedeelde scoped context nooit
    /// aan.</summary>
    private async Task MineCardAsync(
        Card card, RbRulesDbContext ctx, EntityResolutionService resolution,
        InteractionPromotionService gatekeeper, PassContext context,
        OfferingLimits limits, CancellationToken ct)
    {
        var focusRef = BrainRef.Card(card.RiftboundId).Format();
        var mechanics = card.Mechanics ?? [];

        // Entity-resolutie (fase 1) draait VÓÓR de refs ontstaan zodat synoniem-
        // varianten ("Deflect"/"Deflecting") op één ref landen (versla #2).
        var focusLabels = await ResolveLabelsAsync(resolution, mechanics, context.LabelCache, ct);

        // Partner-buurt: kaarten die minstens één mechaniek delen. Bounded VÓÓR de
        // resolutie — de oude code resolveerde de keywords van de hele buurt en stuurde
        // ze allemaal mee; precies de 39 refs die de timeout veroorzaakten (#286).
        var mechSet = mechanics.Select(m => (m ?? "").Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partners = new List<OfferingCard>();
        var ownKeywordRefs = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [focusRef] = KeywordRefs(focusLabels),
        };
        if (limits.MaxPartnerCards > 0 && mechanics.Length > 0)
        {
            foreach (var p in context.CardPool)
            {
                if (partners.Count >= limits.MaxPartnerCards) break;
                if (string.Equals(p.RiftboundId, card.RiftboundId, StringComparison.Ordinal)) continue;
                if (!(p.Mechanics ?? []).Any(m => mechSet.Contains((m ?? "").Trim()))) continue;

                var pRef = BrainRef.Card(p.RiftboundId).Format();
                var pLabels = await ResolveLabelsAsync(
                    resolution, p.Mechanics ?? [], context.LabelCache, ct);
                partners.Add(new OfferingCard(
                    pRef, p.Name, CardEntityType(p.Type), p.TextPlain ?? "", pLabels));
                ownKeywordRefs[pRef] = KeywordRefs(pLabels);
            }
        }

        var sections = SectionCandidates(context.RuleSections, focusLabels);
        var plan = InteractionOffering.ForCard(
            new OfferingCard(focusRef, card.Name, CardEntityType(card.Type),
                card.TextPlain ?? "", focusLabels),
            partners, sections, context.Vocabulary, limits);

        if (plan.Refs.Count < 2)
        {
            // Niets zinnigs om over te redeneren — deterministisch, geen uitval. Wel
            // markeren (#249-review).
            await MarkCardMinedAsync(ctx, card, context.RunId, ct);
            return;
        }

        var offer = BuildOffer(plan, definition: null);
        var succeeded = await ExtractAndPromoteAsync(
            offer, focusRef, MiningPhase.Card, ownKeywordRefs, gatekeeper, context, ct);

        // Het watermark hoort bij "deze kaart is aangeboden en beantwoord", niet bij
        // "deze kaart leverde een feit op" (#249-review) — maar NOOIT bij rb-ai-uitval
        // of een kapotte envelop: die kaart moet juist terugkomen.
        if (succeeded) await MarkCardMinedAsync(ctx, card, context.RunId, ct);
    }

    // ── Gedeelde kern: aanroep → parse → poort ───────────────────────────────

    /// <summary>De rb-ai-call en alles erna, identiek voor beide passes. Geeft terug of
    /// de EXTRACTIE geslaagd is (rb-ai antwoordde én de envelop parseerde) — de
    /// aanroeper hangt daar zijn watermark aan.</summary>
    private async Task<bool> ExtractAndPromoteAsync(
        Offer offer, string derivedFromRef, MiningPhase phase,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownKeywordRefs,
        InteractionPromotionService gatekeeper, PassContext context, CancellationToken ct)
    {
        var vocab = new ExtractionVocab(
            offer.Plan.Refs.Select(r => new OfferedRef(r.Ref, r.Label, r.Type)).ToList(),
            context.WindowLexicon, context.StatusLexicon, offer.SectionRefs);

        // Wandkloktijd, niet denktijd: de Agent SDK retryt intern met backoff, dus deze
        // meting bevat mogelijk herhalingen. Precies daarom meten we hem — dat is wat de
        // 90s-timeout van rb-ai óók ziet.
        var clock = Stopwatch.StartNew();
        var call = await context.Ai(ai, offer, vocab, ct);
        clock.Stop();
        context.Tally.Call(phase, clock.ElapsedMilliseconds, offer.Plan.Refs.Count);

        if (call.Raw is null)
        {
            // Degradatie: rb-ai weg → geen half feit, sla dit item over. De OORZAAK
            // wordt wel geteld (#251/#281) zodat het run-detail laat zien of dit
            // rate-limits, timeouts of onleesbare antwoorden waren — en sinds #279 of
            // het onze eigen sidecar-cap was (ConcurrencyLimited).
            context.Tally.Fail(call.Outcome, call.Reason);
            return false;
        }

        var byRef = offer.Plan.Refs.ToDictionary(r => r.Ref, StringComparer.Ordinal);
        var parsed = InteractionExtraction.ParseDetailed(call.Raw, vocab);
        if (parsed.Malformed)
        {
            // HTTP 200 met een afgekapte of schema-vreemde body is UITVAL, geen leeg
            // resultaat (#251-review): stil tot [] reduceren telde parse-fouten als
            // geslaagd werk en maakte de uitvalmeting blind.
            context.Tally.Fail(AiCallOutcome.Unparseable);
            return false;
        }
        // Geldig antwoord zonder kandidaten is geslaagd werk, geen uitval — apart
        // geteld zodat "rb-ai gaf niets" en "rb-ai wist niets" niet op één hoop komen.
        context.Tally.Ai(parsed.Items.Count == 0 ? AiCallOutcome.Empty : AiCallOutcome.Ok);

        foreach (var ix in parsed.Items)
        {
            if (!byRef.TryGetValue(ix.FromRef, out var from)
                || !byRef.TryGetValue(ix.ToRef, out var to))
                continue; // buiten de aangeboden set — parse gate't dit al, dubbel slot

            // Tautologie-poort (#249): kaart↔eigen-keyword is al deterministisch bekend
            // (HAS_MECHANIC uit Card.Mechanics, GraphSyncService) — niet minen, niet
            // promoveren, en ook niet als "geëxtraheerd" tellen: het is geen kandidaat
            // maar herkauwde kennis. Apart geteld zodat de cockpit ziet hoe vaak het
            // model er nog naartoe trekt.
            if (InteractionTautology.IsCardOwnKeywordPair(from.Ref, to.Ref, ownKeywordRefs))
            {
                context.Tally.SkipKnown();
                continue;
            }
            context.Tally.Extract();

            var conditions = ix.Conditions
                .Select(c => new InteractionConditionInput(c.OnKind, c.SubjectRole, c.Value, c.Operator))
                .ToList();

            // Lexicale steun (§3.4, verscherpt in #249): bestaat er ÉÉN bewijs-eenheid —
            // een aangeboden kaart óf een regelsectie — die een RELATIE tussen beide
            // rollen uitdrukt? Beide rollen verankerd binnen één eenheid (geen
            // cross-card-toeval) én minstens één van beide TEXTUEEL: een kaart die
            // alleen zichzelf verankert bewijst niets over een relatie.
            var lexical = offer.Evidence.Any(unit => InteractionEvidence.ExpressesRelation(
                Anchor(unit, from), Anchor(unit, to)));

            var request = new InteractionPromotionRequest(
                AgentRef: from.Ref, AgentType: from.Type,
                PatientRef: to.Ref, PatientType: to.Type,
                Kind: ix.Kind,
                DerivedFromRef: derivedFromRef,
                // #286: de sectie die het model aanwees, al door de enum-poort én de
                // parser-poort — nooit een sectie buiten de aanbieding.
                GovernedByRef: ix.GovernedByRef,
                Conditions: conditions,
                LexicalSupport: lexical,
                ConsensusCount: 1, // één extractie-pass = één bron
                LlmVerdictInteracts: ix.Interacts);

            // Geserialiseerd: twee items kunnen hetzelfde paar voorstellen, en de poort
            // doet lees-dan-schrijf op een unieke sleutel.
            await context.Gate.WaitAsync(ct);
            InteractionPromotionResult result;
            try
            {
                result = await gatekeeper.PromoteAsync(request, context.RunId, ct: ct);
            }
            finally
            {
                context.Gate.Release();
            }
            context.Tally.Gate(result.Outcome);
        }

        return true;
    }

    // ── Aanbieding → prompt ──────────────────────────────────────────────────

    /// <summary>Zet een <see cref="OfferingPlan"/> om in de prompt-tekst, de
    /// bewijs-eenheden en de citeerbare sectie-refs.
    ///
    /// De prompt-tekst draagt ref-headers zodat het model refs↔kaarten mapt; de
    /// evidence-teksten zijn de RAUWE bron-teksten (per eenheid gescheiden) — de
    /// lexicale poort mag niet triviaal slagen op een label dat we zelf in een header
    /// plakten, en co-occurrence moet binnen ÉÉN eenheid gelden. Een kaart die geen
    /// aangeboden rol is (mechanic-pass) krijgt bewust géén ref-header en telt niet als
    /// identiteits-anker: zij is bewijs, geen rol.</summary>
    private static Offer BuildOffer(OfferingPlan plan, string? definition)
    {
        var offeredRefs = plan.Refs.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        var promptParts = new List<string>();
        var evidence = new List<EvidenceUnit>();

        if (!string.IsNullOrWhiteSpace(definition))
        {
            promptParts.Add($"[definitie] {definition}");
            evidence.Add(new(CardRef: null, definition!));
        }

        foreach (var c in plan.Cards)
        {
            var isRole = offeredRefs.Contains(c.Ref);
            promptParts.Add(isRole
                ? $"[{c.Ref} — {c.Name}] {c.Text}"
                : $"[kaart {c.Name}] {c.Text}");
            evidence.Add(new(isRole ? c.Ref : null, c.Text));
        }

        var sectionRefs = new List<string>();
        foreach (var s in plan.Sections)
        {
            promptParts.Add($"[regels {s.Label}] {s.Text}");
            evidence.Add(new(CardRef: null, s.Text));
            if (s.Ref is { Length: > 0 } r) sectionRefs.Add(r);
        }

        return new(plan, string.Join("\n", promptParts), evidence, sectionRefs);
    }

    /// <summary>Kandidaat-regelsecties voor één item: officiële secties die minstens
    /// één ankerlabel noemen, bounded. Het echte selectiecriterium (≥2 aangeboden
    /// labels + paar-diversiteit) staat in <see cref="InteractionOffering"/>; dit is
    /// alleen het voorwerk-filter dat voorkomt dat die check over het hele corpus
    /// loopt.</summary>
    private static List<OfferingSection> SectionCandidates(
        IReadOnlyList<SectionLite> sections, IReadOnlyList<string> anchorLabels)
    {
        var picked = new List<OfferingSection>();
        if (anchorLabels.Count == 0) return picked;

        foreach (var s in sections)
        {
            if (picked.Count >= MaxSectionCandidates) break;
            if (!anchorLabels.Any(l => TermMatch.ContainsWord(s.Text, l))) continue;

            // Alleen een sectie mét §-code is citeerbaar als GOVERNED_BY-anker; zonder
            // code blijft de tekst gewoon bewijs.
            var code = s.SectionCode is { Length: > 0 } c ? c : null;
            picked.Add(new OfferingSection(
                code is null ? null : BrainRef.Section(s.SourceId, code).Format(),
                code is null ? s.SourceId : $"{s.SourceId} §{code}",
                s.Text));
        }
        return picked;
    }

    // ── Watermarks ───────────────────────────────────────────────────────────

    /// <summary>Zet het voortgangs-watermark op een verwerkte focus-kaart
    /// (#249-review). Schrijft door de context van de worker (#279) en bewaart per
    /// kaart, niet aan het einde van de run: de focus-lijst is geladen op de gedeelde
    /// scoped context en die mag een worker niet aanraken. Bijvangst: een run die
    /// halverwege sneuvelt houdt de tot dan toe gedane kaarten gemarkeerd.</summary>
    private static async Task MarkCardMinedAsync(
        RbRulesDbContext ctx, Card card, string runId, CancellationToken ct)
    {
        var tracked = await ctx.Cards.FirstOrDefaultAsync(
            c => c.RiftboundId == card.RiftboundId, ct);
        if (tracked is null) return;
        tracked.InteractionsMinedAt = DateTimeOffset.UtcNow;
        tracked.InteractionsMinedByRunId = runId;
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>Idem voor een mechanic-subject (#286).</summary>
    private static async Task MarkEntityMinedAsync(
        RbRulesDbContext ctx, CanonicalEntity subject, string runId, CancellationToken ct)
    {
        var tracked = await ctx.CanonicalEntities.FirstOrDefaultAsync(e => e.Id == subject.Id, ct);
        if (tracked is null) return;
        tracked.InteractionsMinedAt = DateTimeOffset.UtcNow;
        tracked.InteractionsMinedByRunId = runId;
        await ctx.SaveChangesAsync(ct);
    }

    // ── Hulpstukken ──────────────────────────────────────────────────────────

    /// <summary>Resolveert keyword-surface-forms tegen de canonieke laag (fase 1): bij
    /// een match het canonieke label, anders de magnitude-vrije basis ("Assault 2" →
    /// "Assault"). Nooit een nieuwe entiteit registreren hier — dat blijft de
    /// entity-resolution-job; deze mining leest alleen. De resolver komt van de
    /// aanroeper omdat hij op de context van díe worker staat (#279); de cache is
    /// gedeeld omdat de mapping context-onafhankelijk is.</summary>
    private static async Task<IReadOnlyList<string>> ResolveLabelsAsync(
        EntityResolutionService resolution, IEnumerable<string?> surfaces,
        ConcurrentDictionary<string, string> cache, CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in surfaces)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!cache.TryGetValue(raw, out var label))
            {
                var res = await resolution.ResolveAsync(raw, CanonicalEntityKinds.Keyword, ct);
                label = res.Entity?.CanonicalLabel ?? Magnitude.Parse(raw).BaseLabel.Trim();
                cache[raw] = label;
            }
            if (label.Length > 0 && seen.Add(label)) result.Add(label);
        }
        return result;
    }

    private static IReadOnlySet<string> KeywordRefs(IEnumerable<string> labels) =>
        labels.Select(l => BrainRef.Mechanic(l).Format()).ToHashSet(StringComparer.Ordinal);

    private async Task<MiningRun> StartRunAsync(
        IReadOnlyList<string> windows, IReadOnlyList<string> statuses, CancellationToken ct)
    {
        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = FactKinds.Interaction,
            LlmModel = "claude-sonnet-4-6",
            PromptVersion = PromptVersion,
            VocabSnapshot = TextUtils.Sha256(string.Join('|',
                InteractionKinds.All.Concat(windows).Concat(statuses))),
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Kaart-<see cref="EntityType"/> uit het printed type (Unit/Legend/…);
    /// valt terug op de Card-umbrella als het type ontbreekt of niet als
    /// kaart-subklasse parseert — altijd een geldige HAS_ROLE-filler.</summary>
    private static EntityType CardEntityType(string? type) =>
        type is { } t && OntologySchema.ParseEntityType(t) is { } et
            && OntologySchema.IsA(et, EntityType.Card)
            ? et : EntityType.Card;

    /// <summary>Hoe sterk verankert bewijs-eenheid <paramref name="unit"/> rol
    /// <paramref name="role"/> (versla defect 3/4; #249)? <see cref="EvidenceAnchor.Identity"/>
    /// wanneer de eenheid de rol ZÉLF is (een card-rol draagt zijn bewijs in zijn eigen
    /// tekst — de kaartnaam hoeft er niet in te staan), <see cref="EvidenceAnchor.Textual"/>
    /// wanneer het rol-label letterlijk in díe tekst voorkomt, anders
    /// <see cref="EvidenceAnchor.None"/>. Alleen een textueel anker kan bewijs voor een
    /// RELATIE zijn.</summary>
    private static EvidenceAnchor Anchor(EvidenceUnit unit, OfferedRefCandidate role) =>
        string.Equals(unit.CardRef, role.Ref, StringComparison.Ordinal)
            ? EvidenceAnchor.Identity
            : TermMatch.ContainsWord(unit.Text, role.Label)
                ? EvidenceAnchor.Textual
                : EvidenceAnchor.None;

    // ── Typen ────────────────────────────────────────────────────────────────

    /// <summary>Welke pass een aanroep deed — de as waarlangs de meting uitsplitst.</summary>
    private enum MiningPhase { Mechanic, Card }

    /// <summary>Alles wat beide passes delen en dat per run één keer wordt
    /// klaargezet.</summary>
    private sealed record PassContext(
        string RunId,
        IReadOnlyList<CardLite> CardPool,
        IReadOnlyList<SectionLite> RuleSections,
        IReadOnlyList<string> Vocabulary,
        IReadOnlyList<string> WindowLexicon,
        IReadOnlyList<string> StatusLexicon,
        RunTally Tally,
        SemaphoreSlim Gate,
        ConcurrentDictionary<string, string> LabelCache)
    {
        /// <summary>De rb-ai-aanroep zelf, op één plek zodat beide passes gegarandeerd
        /// dezelfde payload-vorm sturen.</summary>
        public Task<AiExtraction> Ai(
            RbAiClient ai, Offer offer, ExtractionVocab vocab, CancellationToken ct) =>
            ai.ExtractStructuredDetailedAsync(
                "/extract/interactions",
                new
                {
                    system = InteractionExtraction.SystemPrompt,
                    text = offer.PromptText,
                    refs = offer.Plan.Refs.Select(r => new { r.Ref, r.Label }),
                    sections = offer.SectionRefs,
                    kinds = InteractionKinds.All,
                    conditionKinds = InteractionConditionKinds.All,
                    roles = InteractionRoles.All,
                    windowLexicon = WindowLexicon,
                    statusLexicon = StatusLexicon,
                }, ct);
    }

    /// <summary>Eén uitgewerkte aanbieding: het plan plus de daaruit afgeleide
    /// prompt-tekst, bewijs-eenheden en citeerbare sectie-refs.</summary>
    private sealed record Offer(
        OfferingPlan Plan, string PromptText,
        IReadOnlyList<EvidenceUnit> Evidence, IReadOnlyList<string> SectionRefs);

    /// <summary>Eén bewijs-eenheid voor de lexicale poort: een kaarttekst
    /// (<paramref name="CardRef"/> gezet — die kaart is dan zijn eigen identiteits-
    /// anker) of tekst zonder rol (regelsectie, definitie, of een kaart die alleen
    /// bewijs is).</summary>
    private sealed record EvidenceUnit(string? CardRef, string Text);

    private sealed record CardLite(
        string RiftboundId, string Name, string? Type, string[]? Mechanics, string? TextPlain);
    private sealed record SectionLite(string SourceId, string? SectionCode, string Text);

    /// <summary>De run-tellers, gedeeld door alle workers (#279). Eén slot om álle
    /// mutaties: de tellers zijn onderling afhankelijk (Failed hoort bij de oorzaak in
    /// de <see cref="AiOutcomeTally"/>) en optellen is microseconden werk náást een
    /// LLM-call van tientallen seconden, dus fijnmaziger synchroniseren koopt niets en
    /// kost leesbaarheid.
    ///
    /// Sinds #286 telt hij ook de MÉTING mee (acceptatiecriterium): per fase het aantal
    /// aanroepen, de opgetelde wandkloktijd en het opgetelde aantal aangeboden refs.
    /// Zonder die drie is elke uitspraak over "de vraag is nu goedkoper" een gok — en
    /// gokken is hier al drie keer misgegaan.</summary>
    private sealed class RunTally
    {
        private readonly object _gate = new();
        private readonly AiOutcomeTally _ai = new();
        private readonly Dictionary<MiningPhase, CallStats> _calls = new();
        private int _cardsProcessed, _mechanicsProcessed;
        private int _extracted, _promoted, _candidates, _hypothesized;
        private int _rejected, _failed, _skippedKnown;
        private bool _deadlineHit;

        private sealed class CallStats
        {
            public int Count;
            public long Millis;
            public long Refs;
        }

        /// <summary>Het volgnummer voor de voortgangsmelding. Bij meerdere workers is
        /// dit "de zoveelste die is opgepakt", niet "de zoveelste die klaar is" —
        /// items ronden immers door elkaar af.</summary>
        public int CountCard() { lock (_gate) return ++_cardsProcessed; }
        public int CountMechanic() { lock (_gate) return ++_mechanicsProcessed; }

        public void Call(MiningPhase phase, long millis, int refs)
        {
            lock (_gate)
            {
                if (!_calls.TryGetValue(phase, out var s)) _calls[phase] = s = new();
                s.Count++;
                s.Millis += millis;
                s.Refs += refs;
            }
        }

        public void Ai(AiCallOutcome outcome) { lock (_gate) _ai.Add(outcome); }

        /// <summary>Telt één uitval, met de fijnmazige reden die rb-ai meestuurde
        /// (#281) — zo staat er "5xx×22 (max_turns×14, spawn×8)" in het run-detail
        /// in plaats van alleen "5xx×22". Null blijft het gedrag van vóór #281.</summary>
        public void Fail(AiCallOutcome outcome, string? reason = null)
        {
            lock (_gate) { _failed++; _ai.Add(outcome, reason); }
        }

        public void Extract() { lock (_gate) _extracted++; }
        public void SkipKnown() { lock (_gate) _skippedKnown++; }
        public void HitDeadline() { lock (_gate) _deadlineHit = true; }

        public void Gate(InteractionGateOutcome outcome)
        {
            lock (_gate)
            {
                switch (outcome)
                {
                    case InteractionGateOutcome.Promoted: _promoted++; break;
                    case InteractionGateOutcome.Candidate: _candidates++; break;
                    case InteractionGateOutcome.ModelHypothesizedUnruled: _hypothesized++; break;
                    case InteractionGateOutcome.Rejected: _rejected++; break;
                }
            }
        }

        public int CardsProcessed { get { lock (_gate) return _cardsProcessed; } }
        public int MechanicsProcessed { get { lock (_gate) return _mechanicsProcessed; } }
        public int Extracted { get { lock (_gate) return _extracted; } }
        public int Promoted { get { lock (_gate) return _promoted; } }
        public int Candidates { get { lock (_gate) return _candidates; } }
        public int Hypothesized { get { lock (_gate) return _hypothesized; } }
        public int Rejected { get { lock (_gate) return _rejected; } }
        public int Failed { get { lock (_gate) return _failed; } }
        public int SkippedKnown { get { lock (_gate) return _skippedKnown; } }
        public bool DeadlineHit { get { lock (_gate) return _deadlineHit; } }

        public string? FailureDetail
        {
            get
            {
                lock (_gate) return _ai.Summary is { Length: > 0 } detail ? detail : null;
            }
        }

        /// <summary>De meting in één leesbare regel per fase. "wandklok" is geen
        /// slordigheid maar het punt: de Agent SDK retryt intern met backoff, dus deze
        /// tijd kan herhalingen bevatten — en dát is precies de tijd waartegen de
        /// 90s-timeout van rb-ai afrekent.</summary>
        public string? CallMetrics
        {
            get
            {
                lock (_gate)
                {
                    var parts = new List<string>();
                    foreach (var phase in new[] { MiningPhase.Mechanic, MiningPhase.Card })
                    {
                        if (!_calls.TryGetValue(phase, out var s) || s.Count == 0) continue;
                        var name = phase == MiningPhase.Mechanic ? "mechanic" : "kaart";
                        var seconds = s.Millis / 1000.0 / s.Count;
                        var refs = (double)s.Refs / s.Count;
                        parts.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} {1}× (gem. {2:0.0}s wandklok, {3:0.0} refs)",
                            name, s.Count, seconds, refs));
                    }
                    return parts.Count == 0 ? null : "meting: " + string.Join(", ", parts);
                }
            }
        }
    }
}
