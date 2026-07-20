using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Infrastructure;

/// <summary>PublishedAt/UpdatedAt (#168): "laatst bijgewerkt" (een echte
/// content-wijziging) of anders "geldig sinds" (publicatiedatum) — beide null
/// als de bron geen van beide draagt. Puur weergave: de her-ordening op
/// recency (bij gelijke TrustTier) is al toegepast vóórdat deze citaties
/// gebouwd worden (zie AskCoreAsync, Precedence.ReorderTiedByTier).</summary>
public record Citation(
    int N, string SourceName, string Url, string? Section, int Trust,
    string? Text = null, string? PdfUrl = null, int? Page = null,
    IReadOnlyList<ParentSection>? Parents = null,
    DateTimeOffset? PublishedAt = null, DateTimeOffset? UpdatedAt = null);

public record AskCard(
    string RiftboundId, string Name, string? Type, string? Supertype,
    string[] Domains, int? Energy, int? Might, string? TextPlain,
    string[]? Mechanics, string? ImageUrl, bool Banned,
    string? SetName, DateOnly? LegalFrom, string Legality,
    // Presentatie voor de kaartwidget (#269/#270): juiste verhouding en een
    // echte alt-tekst i.p.v. alleen de kaartnaam.
    int? ImageWidth = null, int? ImageHeight = null, string? ImageAltText = null);

public record AskTurn(string Question, string Answer);

public record AskClaimSource(string SourceName, string Url);

/// <summary>Community-claim in het antwoord (#51): het "Community-consensus"-
/// blok onder het antwoord toont deze apart van de officiële citaties, met
/// trust-label en uitklapbare bronnen.</summary>
public record AskClaim(
    string TopicType, string TopicRef, string Statement,
    int Corroboration, double TrustScore, string OfficialStatus,
    IReadOnlyList<AskClaimSource> Sources);

public record AskMisconceptionSource(string SourceName, string Url, string? Quote);

/// <summary>Gedocumenteerde misvatting in het antwoord (#125): het misvatting-
/// blok naast de community-consensus toont beide bewijzen — het community-
/// citaat (bron + quote) én de officiële weerlegging (met §-link waar de
/// weerlegging een sectie noemt).</summary>
public record AskMisconception(
    string TopicType, string TopicRef, string Statement,
    string Rebuttal, string? RebuttalSection,
    IReadOnlyList<AskMisconceptionSource> Sources);

/// <summary>Token-optelling van een AskResult (#158): puur additief veld zodat
/// de benchmark (duur/tokens per vraag) bij het bestaande resultaat kan
/// zonder AskCoreAsync's interne AiUsage-boekhouding naar buiten te trekken.
/// Null = geen enkele call gaf usage terug (zelfde "onbekend ≠ 0"-regel als
/// AskMetric/AskTrace).</summary>
public record AskUsageInfo(long? InputTokens, long? OutputTokens);

public record AskResult(
    string Answer, IReadOnlyList<Citation> Citations,
    IReadOnlyList<AskCard> Cards, string QuestionType,
    bool Ok = true, IReadOnlyList<AskClaim>? Claims = null,
    IReadOnlyList<AskMisconception>? Misconceptions = null,
    // Aanpak-terugmelding (#153): welke aanpak het wérd ("auto"|"fast"|
    // "thorough") en — als de gebruikerskeuze niet gehonoreerd is — waarom
    // (AgenticGate.Reason*). De UI toont daarop de nette melding
    // ("quota op — automatisch beantwoord").
    string? Approach = null, string? ApproachReason = null,
    AskUsageInfo? Usage = null);

/// <summary>De benchmark-vlag (#158) — het ENE punt waarmee een benchmarkrun
/// door de ask-aanroep reist zonder de flow te herschrijven. Benchmark = true
/// laat retrieval/prompt/agentic-gate exact zoals een normale vraag, maar
/// onderdrukt ieder leer-/meetneveneffect: geen ask_trace- en geen
/// ask_metric-rij, en geen agentic-relatie-terugkoppeling (#120). Claims en
/// geverifieerde rulings worden door AskCoreAsync toch al alleen gelezen,
/// nooit geschreven, dus die blijven vanzelf buiten schot. De meerkeuze-
/// opties zelf zitten NIET hier — die gaan als gewone tekst in de
/// `question` mee (zie BenchmarkPrompt in RbRules.Domain).</summary>
public sealed record AskOptions
{
    public static readonly AskOptions Default = new();
    public bool Benchmark { get; init; }
    /// <summary>Model-sweep (#174): expliciete modeloverride voor déze ask-
    /// aanroep — reist ongewijzigd door naar rb-ai (RbAiClient → SDK-
    /// query()-modeloverride). Alleen zinvol samen met Benchmark = true;
    /// null laat het bestaande cheap/hard/agentic-gedrag intact.</summary>
    public string? Model { get; init; }
}

/// <summary>Vroege metadata voor het streamingpad (#31): vraagtype, citaties
/// en community-claims staan al vast vóór de LLM-call — de UI kan daarmee de
/// citatielijst en [[rule:…]]-widgets renderen terwijl het antwoord nog
/// binnenstroomt. Betrokken kaarten volgen pas in het slotframe (die worden
/// tegen het volledige antwoord gematcht).</summary>
public record AskStreamMeta(
    string QuestionType, IReadOnlyList<Citation> Citations,
    IReadOnlyList<AskClaim>? Claims,
    // Aanpak-terugmelding (#153), al vóór het antwoord: bij een agentic run
    // zit tussen meta en de eerste delta een lange stille wachtfase — de UI
    // kan een quota-terugval dus het best meteen melden.
    string? Approach = null, string? ApproachReason = null);

/// <summary>Rulings-Q&A met hybride retrieval (audit-fix: niet meer alleen
/// vector): vector-zoek + Postgres full-text, gefuseerd met RRF; daarna
/// kaartfeiten + geverifieerde rulings + antwoord via rb-ai met [n]-citaten.
/// Embedding-uitval is een verwacht pad (#100): dan vervallen alleen de
/// vector-kanalen en degradeert de vraag naar FTS + naam/mechaniek/lexicaal —
/// nooit een kale 500. Sinds #152 draaien de onafhankelijke lees-kanalen
/// concurrent, elk op een eigen context uit <paramref name="dbFactory"/>
/// (DbContext is niet thread-safe); zonder factory (tests op EF InMemory)
/// draaien dezelfde kanalen gewoon één voor één op de scoped context. Eén
/// uitvallend kanaal levert een leeg kanaal plus een marker in de trace —
/// nooit een 500. De kanalen leveren aan vaste slots en de prompt-opbouw
/// blijft sequentieel: zelfde input geeft byte-voor-byte dezelfde prompt.
/// <paramref name="rewriteCache"/> (#152, optioneel — singleton in
/// Program.cs) is een kleine LRU op de genormaliseerde vraag: een
/// cache-hit slaat de rewrite-call over; een null-uitkomst (rewrite-uitval)
/// wordt nooit gecacht.</summary>
public class AskService(
    RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
    AgenticRelationService agenticRelations,
    RequestUserContext userContext, ILogger<AskService> logger,
    IDbContextFactory<RbRulesDbContext>? dbFactory = null,
    RewriteCache? rewriteCache = null,
    AgenticInFlightTracker? agenticInFlight = null,
    BreinRetrievalService? brein = null)
{
    private const int TopK = 8;

    // Brein-GraphRAG-retrieval (#228) ACHTER de default-uit feature-flag. Null (de
    // meeste constructors/tests) óf flag-uit ⇒ /ask draait EXACT zoals nu: geen
    // brein-call, geen extra latency, geen gedragswijziging. Alleen wanneer de
    // service bestaat ÉN Enabled is, verrijkt AskCoreAsync de context (best-effort,
    // met nette terugval bij elke fout).
    private readonly BreinRetrievalService? _brein = brein;

    // #153: de in-flight-reservering is proces-breed (singleton in DI). De
    // optionele parameter houdt de vele test-constructors ongewijzigd — die
    // krijgen elk een eigen verse tracker, wat voor hun scenario's volstaat.
    private readonly AgenticInFlightTracker _agenticInFlight = agenticInFlight ?? new();

    // De "ruling-skill": toon en spelregels — de structuur komt per vraagtype
    // uit QuestionRouter.StructureFor (interne router, geen extra LLM-call).
    private const string BasePrompt = """
        Je bent de rulings-assistent van Riftbound Rules Companion. Je geeft
        oordelen zoals een toernooi-scheidsrechter: beslist, neutraal en
        controleerbaar. Antwoord in het Nederlands; laat Engelse speltermen
        (Deflect, showdown, exhaust, Hidden, …) onvertaald. Antwoord in
        markdown en volg exact de structuur van het opgegeven vraagtype.

        REGELS:
        - Schrijf 'Oordeel' en 'Zekerheid' exact als losse regels in de vorm
          `**Oordeel:** …` en `**Zekerheid:** …` — géén ##-koppen daarvoor,
          geen scheidingslijnen (---). De overige kopjes wél als ### koppen.
        - Baseer je uitsluitend op de meegegeven context-fragmenten en
          kaartgegevens. Ontbreekt het antwoord daarin: Zekerheid = Onzeker,
          en zeg wat er nodig is. Nooit gokken zonder dat label.
        - Officiële bronnen (lagere trust = betrouwbaarder) gaan vóór community.
          GEVERIFIEERDE RULINGS zijn gezaghebbend en gaan vóór alles.
        - Kaartgegevens in de context zijn gezaghebbend voor stats/mechanieken.
        - Een kaartfeit met "NOG NIET LEGAAL" betekent: die kaart zit in een
          set die nog niet is verschenen. Benoem dat expliciet (setnaam +
          datum) zodra je die kaart noemt of aanbeveelt — zeker bij meta-,
          lijst- of legaliteitsvragen — en presenteer zo'n kaart nooit als nu
          speelbaar.
        - Kort is beter: geen inleiding, geen herhaling van de vraag.

        CITATEN — de site toont de meegegeven fragmenten zelf als uitklapbare
        citatielijst onder het antwoord; die lijst is de enige plek waar de
        regelsecties staan. Verwijs in de lopende tekst met [1], [2], … (de
        nummers van de fragmenten) op de plek waar je op een fragment leunt.
        Bouw NOOIT een eigen "Regelbasis"-sectie, markdown-tabel of opsomming
        die alleen §-nummers/bronnen herhaalt — dat dubbelt de lijst onderaan.

        Bij een meegestuurde foto (board state): begin de Uitleg met stap 1 =
        een feitelijke beschrijving van wat je op de foto ziet (welke kaarten,
        zones, exhausted/ready) en benoem expliciet wat je NIET zeker kunt
        lezen; betrek daarna alleen zekere waarnemingen in het oordeel.

        WIDGETS — het antwoord wordt gerenderd met interactieve blokken.
        Plaats markers op een eigen regel:
        - [[rule:466.2.c]] direct onder de stap die op die sectie leunt —
          de site toont daar een uitklapbaar regelblok met volledige tekst,
          ouderregels en PDF-link. Alleen voor §-codes uit de context.
        - [[card:Exacte Kaartnaam]] bij de eerste inhoudelijke vermelding
          van een kaart — de site toont een kaart-widget met beeld en stats.
          Alleen voor kaarten uit de kaartgegevens.
        Spaarzaam en functioneel (2 tot 5 per antwoord), nooit dezelfde
        marker twee keer; verwijzen met [n]/§ in de lopende tekst blijft
        daarnaast gewoon nodig.
        """;

    private const int MaxHistoryTurns = 3;
    private const int MaxHistoryAnswerChars = 900;

    public Task<AskResult> AskAsync(
        string question, IReadOnlyList<RbAiClient.AiImage>? images = null,
        IReadOnlyList<AskTurn>? history = null,
        AskApproach approach = AskApproach.Auto,
        AskOptions? options = null,
        CancellationToken ct = default) =>
        AskCoreAsync(question, images, history, approach, options ?? AskOptions.Default,
            onMeta: null, onDelta: null, ct);

    /// <summary>Streamende variant (#31): identieke retrieval en afronding als
    /// <see cref="AskAsync"/> (één pass), maar het antwoord komt via
    /// <paramref name="onDelta"/> woord-voor-woord binnen en de citaties gaan
    /// vooraf via <paramref name="onMeta"/>. Het resultaat is hetzelfde
    /// AskResult als slotframe — inclusief dezelfde degradatie bij AI-uitval.</summary>
    public Task<AskResult> AskStreamingAsync(
        string question, IReadOnlyList<RbAiClient.AiImage>? images,
        IReadOnlyList<AskTurn>? history,
        Func<AskStreamMeta, Task> onMeta, Func<string, Task> onDelta,
        AskApproach approach = AskApproach.Auto,
        AskOptions? options = null,
        CancellationToken ct = default) =>
        AskCoreAsync(question, images, history, approach, options ?? AskOptions.Default,
            onMeta, onDelta, ct);

    private async Task<AskResult> AskCoreAsync(
        string question, IReadOnlyList<RbAiClient.AiImage>? images,
        IReadOnlyList<AskTurn>? history, AskApproach approach, AskOptions options,
        Func<AskStreamMeta, Task>? onMeta, Func<string, Task>? onDelta,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Per-fase-metingen (#152): wandkloktijd van rewrite, embed-batches,
        // retrieval-totaal en de afrondende AI-call — als compacte JSON op de
        // trace (AskPhases). Puur Stopwatch-werk: kan een vraag nooit laten
        // falen. De fasen overlappen elkaar (parallelle pipeline), dus de som
        // is bewust niet gelijk aan de totale duur.
        long rewriteMs = 0, embedMs = 0, retrievalMs = 0, aiMs = 0;
        var retrievalStart = sw.ElapsedMilliseconds;
        // Doorvragen (#41): eerdere rondes gaan gecapt mee; de retrieval
        // kijkt naar follow-up + eerdere vragen zodat nieuwe relevante §'s
        // bijgeladen worden.
        var turns = (history ?? []).TakeLast(MaxHistoryTurns).ToList();
        var retrievalText = turns.Count == 0
            ? question
            : string.Join("\n", turns.Select(t => t.Question).Append(question));

        // 0. Rewrite-overlap (#152): de query-rewrite (#66, één goedkope
        // LLM-call — typo's corrigeren, NL→EN speltermen, zoekqueries en
        // lexicale termen; bij doorvragen #41 gaat de historie mee) is de
        // traagste voorbereidingsstap. Die start daarom METEEN, samen met het
        // embedden van de ruwe retrievaltekst; alles wat niet van de rewrite
        // afhangt (naam-match, FTS op de ruwe tekst, banlijst) draait terwijl
        // de rewrite loopt. Uitval of onzin-output van de rewrite = verwacht
        // pad: rewrite blijft null en we zoeken met de rauwe vraag(+historie),
        // het gedrag van vóór #66.
        var rewriteTask = RunRewriteAsync(retrievalText, ct);
        var rawEmbedTask = EmbedRawAsync(retrievalText, ct);

        // 0b. Naam-match in SQL (review-fix #43: geen full-table naar de
        // client) + interne router: vraagtype stuurt structuur en bronnen-bias.
        // Vanaf hier zijn de lees-kanalen ChannelResult-taken (#152): mét
        // factory concurrent op eigen contexten, zonder factory (tests, EF
        // InMemory) één voor één op de scoped context. Eén uitvallend kanaal
        // levert een leeg kanaal plus een marker in de trace — nooit een 500.
        var qLower = $"{string.Join("\n", turns.Select(t => t.Question))}\n{question}"
            .ToLowerInvariant();
        var agenticMode = AgenticGate.ParseMode(Environment.GetEnvironmentVariable("ASK_AGENTIC"));

        var mentionsTask = await StartDbChannelAsync("naam-match", false,
            ctx => CardsNamedIn(ctx, qLower).AnyAsync(ct), ct);

        // Agentic-gate-invoer (#107, review): trigger (a) is een
        // interactievráág, dus alleen kaartnamen in de HUIDIGE vraag tellen —
        // anders blijft na één interactievraag élke follow-up escaleren.
        // Gededupliceerd op langste naam (Domain), omdat de substring-match
        // "Jinx" ook binnen "Jinx, Loose Cannon" raakt. Alleen berekend als
        // de flag aan staat: het off-pad doet géén extra query — en het
        // Fast-pad (#153) evenmin, want dat escaleert per definitie niet.
        // Aanpak-keuze (#153), server-authoritatief: alleen een ingelogde
        // vrager (gezet door de quota-filter) mag de gate sturen — anoniem
        // wordt het veld genegeerd en blijft alles Auto.
        var requestedApproach = userContext.User is null ? AskApproach.Auto : approach;
        var gateTask = agenticMode == AgenticMode.Off || requestedApproach == AskApproach.Fast
            ? Task.FromResult(new ChannelResult<List<string>>([], null))
            : await StartDbChannelAsync("agentic-gate", new List<string>(),
                ctx => CardsNamedIn(ctx, question.ToLowerInvariant())
                    .Select(c => c.Name).Distinct().ToListAsync(ct), ct);

        // 0c. Full-text op de rúwe tekst, alvast tijdens de rewrite. De
        // afweging (#152): FTS zocht sinds #66 op de genormaliseerde Engelse
        // zoekzin — de bronnen zijn Engels, dus die matcht wezenlijk beter
        // dan een Nederlandse ruwe vraag. Daarom verderop: levert de rewrite
        // een ándere zoektekst op, dan draait de FTS opnieuw op die tekst en
        // telt alléén die lijst mee in de RRF-fusie — de resultaten blijven
        // zo byte-voor-byte gelijk aan vóór de overlap. De vroege ruwe run is
        // alleen "verspild" als de rewrite iets wezenlijks veranderde, en
        // kostte dan tóch geen wandkloktijd (hij liep onder de rewrite).
        var rawFtsTask = await StartChannelAsync<List<(long Id, string SourceId)>>(
            "fts", [], () => FullTextChunksAsync(retrievalText, ct), ct);

        // De router heeft de naam-match nodig; daarna kan de banlijst
        // (alleen Legaliteit) alsnog onder de rewrite starten.
        var mentionsResult = await mentionsTask;
        var mentionsCard = mentionsResult.Value;
        var type = QuestionRouter.Classify(question, mentionsCard);

        // Brein-GraphRAG-verrijking (#228) — ALLEEN achter de default-uit flag. Start
        // hem hier, naast de AI-onafhankelijke lees-kanalen, zodat hij (indien aan)
        // met de bestaande retrieval overlapt i.p.v. serieel latency toe te voegen.
        // Flag uit óf geen service (de meeste paden) ⇒ deze tak levert null: geen
        // brein-call, geen extra latency, exact het bestaande /ask-gedrag. De
        // flag-poort zit sinds #254 IN EnrichAsync (beheerde instelling, op het
        // gebruiksmoment gelezen) zodat een toggle in beheer direct werkt zonder
        // herstart — de poort blijft vóór elke orchestrator-aanroep staan. Een
        // benchmarkrun (#158) blijft eveneens buiten schot (isolatie). EnrichAsync
        // slikt zelf elke niet-annulering-fout (null terug), dus dit kan /ask nooit
        // laten falen.
        var breinTask = _brein is not null && !options.Benchmark
            ? _brein.EnrichAsync(question, type, ct)
            : Task.FromResult<BreinEnrichment?>(null);

        // 0d. Legaliteitsvragen krijgen de actuele banlijst als gezaghebbend
        // blok — rewrite-onafhankelijk, dus ook alvast tijdens de rewrite.
        var bansTask = type != QuestionType.Legaliteit
            ? Task.FromResult(new ChannelResult<List<string>>([], null))
            : await StartDbChannelAsync("banlijst", new List<string>(),
                ctx => ctx.BanEntries.AsNoTracking()
                    .OrderBy(b => b.Name)
                    .Take(40)
                    .Select(b => $"- {b.Name} ({b.Kind})")
                    .ToListAsync(ct), ct);

        // 0e. Deck-meta (kennislaag 3, #267): het deck-gebruikssignaal van het
        // kaartdossier (aandeel recente decks, gemiddeld aantal exemplaren,
        // top-co-occurrence) als expliciet gelabeld laag-3-blok. De poort
        // (DeckMetaRetrieval.ShouldRetrieve, Domain) bewaakt de hotpath: het
        // kanaal start uitsluitend bij een kaart-/lijstvraag mét herkende
        // kaartnaam — beide gegevens zijn er op dit punt al voor de router,
        // dus de poort zelf kost géén query, en elke andere vraag (m.n. een
        // regelvraag zonder kaarten) doet géén enkele deck-query. Het kanaal
        // is rewrite-onafhankelijk en draait, net als de banlijst, alvast
        // onder de rewrite-call.
        var deckMetaTask = DeckMetaRetrieval.ShouldRetrieve(type, mentionsCard)
            ? await StartDbChannelAsync(
                "deck-meta", (Block: "", Refs: new List<string>()),
                ctx => DeckMetaChannelAsync(ctx, qLower, ct), ct)
            : Task.FromResult(new ChannelResult<(string Block, List<string> Refs)>(
                ("", []), null));

        // Token-teller per vraag (#121): opgeteld over álle rb-ai-calls die
        // deze vraag kost (rewrite + antwoord; bij agentic de hele run).
        // Blijft null zolang geen enkele call usage teruggaf (oude rb-ai,
        // AI-uitval) — de metric boekt dan "onbekend", niet 0.
        AiUsage? usage = null;

        var rewriteOutcome = await rewriteTask;
        rewriteMs = rewriteOutcome.Ms;
        usage = AddUsage(usage, rewriteOutcome.Usage);
        var rewrite = rewriteOutcome.Rewrite;
        var searchText = rewrite?.NormalizedQuestion ?? retrievalText;

        // 1. Vector-kanaal: de genormaliseerde zoekzin plus de extra
        // zoekqueries uit de rewrite; elke query is een eigen ranked list
        // voor de RRF-fusie hieronder. De ruwe-vraag-embedding die onder de
        // rewrite al gemaakt is wordt hergebruikt zodra de zoekzin letterlijk
        // de ruwe tekst is (altijd bij rewrite-uitval); alleen wat nog
        // ontbreekt gaat in een tweede Ollama-batch — de embedded teksten en
        // hun volgorde blijven exact die van vóór de overlap.
        // Best-effort (#100): Ollama weg of model niet gepulld mag /ask nooit
        // in een kale 500 laten eindigen — dan blijft qv null, vervallen álle
        // vector-kanalen (regels, primer, rulings-matching, claims,
        // semantische kaart-buren) en draait de vraag door op FTS +
        // naam/mechaniek/lexicale kanalen (patroon RuleSearchService).
        var rawEmbed = await rawEmbedTask;
        embedMs = rawEmbed.Ms;
        Vector[] queryVectors = [];
        if (rewrite is null)
        {
            queryVectors = rawEmbed.Vec is null ? [] : [rawEmbed.Vec];
        }
        else
        {
            // Ordinal: alleen bij een letterlijk identieke zoekzin is de
            // vroege embedding gegarandeerd dezelfde als wat de oude
            // één-batch zou maken.
            var reuseRaw = rawEmbed.Vec is not null && searchText == retrievalText;
            var extraTexts = new List<string>();
            if (!reuseRaw) extraTexts.Add(searchText);
            extraTexts.AddRange(rewrite.SearchQueries
                .Where(q => !q.Equals(searchText, StringComparison.OrdinalIgnoreCase)));
            if (extraTexts.Count == 0)
            {
                queryVectors = reuseRaw ? [rawEmbed.Vec!] : [];
            }
            else
            {
                var extraSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var extra = await embeddings.EmbedAsync([.. extraTexts], ct);
                    queryVectors = reuseRaw ? [rawEmbed.Vec!, .. extra] : extra;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // de vrager zelf haakte af — niet maskeren
                }
                catch (Exception ex)
                {
                    // Alles-of-niets, precies zoals de oude één-batch: een
                    // half embedde queryset zou een ándere RRF-mix geven dan
                    // vóór de overlap ooit kon bestaan.
                    queryVectors = [];
                    logger.LogWarning(ex,
                        "Embedding voor /ask mislukt — vector-kanalen overgeslagen, " +
                        "degradatie naar FTS + lexicale kanalen");
                }
                embedMs += extraSw.ElapsedMilliseconds;
            }
        }
        var qv = queryVectors.Length > 0 ? queryVectors[0] : null;

        // 1b. Alle resterende kanalen starten hier (#152) en landen op vaste
        // slots: mét factory draaien ze concurrent, de fusie en de
        // prompt-opbouw hieronder blijven sequentieel en deterministisch.
        var vectorTasks = new List<Task<ChannelResult<List<(long Id, string SourceId)>>>>();
        foreach (var vec in queryVectors)
            vectorTasks.Add(await StartDbChannelAsync<List<(long Id, string SourceId)>>(
                "regels-vector", [], ctx => RuleChunkHitsAsync(ctx, vec, ct), ct));

        // 2. Full-text-kanaal — eigen (virtual) methode, zie FullTextChunksAsync.
        // Gaf de rewrite een wezenlijk andere zoektekst, dan hier de her-run
        // (zie de afweging bij 0c); anders telt de vroege ruwe run.
        var ftsRerunTask = searchText == retrievalText
            ? null
            : await StartChannelAsync<List<(long Id, string SourceId)>>(
                "fts", [], () => FullTextChunksAsync(searchText, ct), ct);

        // 3b/4/5/5b/5c als kanalen; de blokken schuiven verderop ongewijzigd
        // in de vaste prompt-volgorde (kennispiramide #51).
        var primerTask = await StartDbChannelAsync(
            "primer", (Titles: new List<string>(), Block: "", Relevant: false),
            ctx => PrimerChannelAsync(ctx, qv, ct), ct);
        var cardContextTask = await StartDbChannelAsync(
            "kaartcontext", new CardContextResult("", [], [], false),
            ctx => CardContextAsync(ctx, question, qLower, qv, type, rewrite, ct), ct);
        var rulingsTask = await StartDbChannelAsync(
            "rulings", new List<string>(),
            ctx => RulingsChannelAsync(ctx, qv, ct), ct);
        var claimsTask = await StartDbChannelAsync(
            "claims", (Block: "", Refs: new List<string>(), Claims: new List<AskClaim>()),
            ctx => ClaimsChannelAsync(ctx, qv, type, ct), ct);
        var misconceptionsTask = await StartChannelAsync(
            "misvattingen",
            (Block: "", Refs: new List<string>(), Items: new List<AskMisconception>()),
            () => MisconceptionChannelAsync(qv, ct), ct);

        // Alle slots innen — óók als de fusie hieronder leeg blijkt: geen
        // zwevende taken, en de degradatie-markers zijn compleet.
        var rawFtsResult = await rawFtsTask;
        var ftsResult = ftsRerunTask is null ? rawFtsResult : await ftsRerunTask;
        var textHits = ftsResult.Value;
        var vectorLists = new List<List<(long Id, string SourceId)>>();
        var vectorFailed = false;
        foreach (var vectorTask in vectorTasks)
        {
            var r = await vectorTask;
            vectorLists.Add(r.Value);
            vectorFailed |= r.Failure is not null;
        }
        var gateResult = await gateTask;
        // Off- én Fast-pad (#153) doen geen gate-query: gateResult is dan leeg.
        var gateCardMentions = agenticMode == AgenticMode.Off || requestedApproach == AskApproach.Fast
            ? 0
            : AgenticGate.CountDistinctMentions(gateResult.Value);
        var bansResult = await bansTask;
        var primerResult = await primerTask;
        var cardContextResult = await cardContextTask;
        var rulingsResult = await rulingsTask;
        var claimsResult = await claimsTask;
        var misconceptionResult = await misconceptionsTask;
        var deckMetaResult = await deckMetaTask;

        // Degradatie-markers (#152) in vaste kanaalvolgorde — de reden staat
        // in de logs, de trace toont wélke kanalen leeg bleven.
        var failedChannels = new List<string>();
        void NoteFailure(string? failure)
        {
            if (failure is not null && !failedChannels.Contains(failure))
                failedChannels.Add(failure);
        }
        NoteFailure(mentionsResult.Failure);
        NoteFailure(gateResult.Failure);
        if (vectorFailed) NoteFailure("regels-vector");
        NoteFailure(ftsResult.Failure);
        NoteFailure(bansResult.Failure);
        NoteFailure(primerResult.Failure);
        NoteFailure(rulingsResult.Failure);
        NoteFailure(cardContextResult.Failure);
        NoteFailure(claimsResult.Failure);
        NoteFailure(misconceptionResult.Failure);
        NoteFailure(deckMetaResult.Failure);

        // 3. RRF-fusie (gedeelde Domain-helper, #44), met bron-bias per
        // vraagtype: toernooivragen tillen Tournament Rules-chunks op,
        // gewone rulings de Core Rules.
        var sourceBias = type switch
        {
            QuestionType.Toernooi => "tournament",
            QuestionType.Ruling or QuestionType.Definitie => "core",
            _ => null,
        };
        var topIds = RrfFusion.Fuse(
            [.. vectorLists, textHits],
            hit => hit.Id,
            take: TopK,
            bonus: hit => sourceBias != null &&
                hit.SourceId.Contains(sourceBias, StringComparison.OrdinalIgnoreCase) ? 0.008 : 0);
        if (topIds.Count == 0)
        {
            sw.Stop();
            // BENCHMARK-VLAG (#158): geen ask_metric-rij voor een benchmarkrun.
            if (!options.Benchmark)
                // De rewrite-call is al gemaakt — die tokens tellen mee (#121).
                await RecordMetricAsync(sw.ElapsedMilliseconds, type, images, ok: false, usage: usage);
            // Gedegradeerd (#100) én geen tekst-match: eerlijk melden wat er
            // aan de hand is — niet doen alsof de index leeg is.
            return new(qv is null
                    ? "Het zoeken is tijdelijk beperkt (semantisch zoeken niet beschikbaar) " +
                      "en de tekst-zoek vond niets voor deze vraag — formuleer de vraag " +
                      "anders of probeer het later opnieuw."
                    : "Er is nog geen geïndexeerde regeltekst — draai eerst de regel-index op /admin.",
                [], [], type.ToString(), Ok: false);
        }

        // Citatie-hydratie op de scoped context: alle kanalen zijn hierboven
        // geïnd, dus er draait niets meer concurrent op deze context.
        var chunks = await db.RuleChunks
            .Where(c => topIds.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.Text, c.SectionCode, c.Page, c.DocumentId, c.SourceId,
                s.Name, s.Url, s.TrustTier, s.PublishedAt, s.UpdatedAt,
            })
            .ToListAsync(ct);
        // Dictionary-lookup: een chunk kan tussen de twee query's verdwenen
        // zijn (her-index) — dan overslaan i.p.v. InvalidOperationException.
        var chunksById = chunks.ToDictionary(c => c.Id);
        var ordered = topIds.Where(chunksById.ContainsKey).Select(id => chunksById[id]).ToList();
        // Temporele precedentie (#168): bij gelijke TrustTier (twee even
        // gezaghebbende fragmenten die elkaar tegenspreken) krijgt de
        // recentste voorrang — een pure, stabiele tie-breaker bovenop de
        // RRF-fusie hierboven; de fusie/relevantierangorde zelf verandert niet.
        ordered = Precedence.ReorderTiedByTier(
            ordered, c => c.TrustTier, c => c.UpdatedAt ?? c.PublishedAt);

        // PDF-bestands-URL's voor deeplinks (…rules.pdf#page=N).
        var docIds = ordered.Select(c => c.DocumentId).Distinct().ToList();
        // Projectie: de Content-kolom (volledige PDF-tekst) hoort niet over
        // de lijn voor een id->FileUrl-map (review-fix refactor-golf).
        var fileUrls = await db.Documents
            .Where(d => docIds.Contains(d.Id) && d.FileUrl != null)
            .Select(d => new { d.Id, d.FileUrl })
            .ToDictionaryAsync(d => d.Id, d => d.FileUrl, ct);

        // Ouderketen per citatie (#39): een subregel als 466.2.c is zonder
        // § 466 en § 466.2 onleesbaar.
        var parentKeys = ordered
            .Where(c => c.SectionCode != null)
            .Select(c => (c.SourceId, Code: c.SectionCode!))
            .ToList();
        var parents = await RuleParentLookup.FetchAsync(db, parentKeys, ct);

        var citations = ordered.Select((c, i) => new Citation(
            i + 1, c.Name, c.Url, c.SectionCode, c.TrustTier,
            Text: c.Text, PdfUrl: fileUrls.GetValueOrDefault(c.DocumentId), Page: c.Page,
            Parents: c.SectionCode is null
                ? null
                : parents.GetValueOrDefault((c.SourceId, c.SectionCode)),
            PublishedAt: c.PublishedAt, UpdatedAt: c.UpdatedAt)).ToList();

        var context = string.Join("\n\n", ordered.Select((c, i) =>
            $"[{i + 1}] ({c.Name}, trust {c.TrustTier}{(c.SectionCode is null ? "" : $", §{c.SectionCode}")})\n{c.Text}"));

        // 3b–5c. De kanaal-resultaten van hierboven schuiven hier in hun
        // vaste slots; de blok-volgorde in de prompt is en blijft de
        // kennispiramide van #51.
        var (primerTitles, primerBlock, primerRelevant) = primerResult.Value;
        var cardContext = cardContextResult.Value;
        var cardBlock = cardContext.Block;

        // Banlijst (0d): een lege líjst is echte data ("geen bans bekend"),
        // maar een uitgevallen kanaal niet — dan géén blok in plaats van een
        // gezaghebbende bewering die we niet konden controleren.
        var banBlock = type != QuestionType.Legaliteit || bansResult.Failure is not null
            ? ""
            : bansResult.Value.Count == 0
                ? "\n\nBANLIJST (gezaghebbend): momenteel leeg — er zijn geen bans bekend."
                : "\n\nBANLIJST (gezaghebbend, actueel):\n" + string.Join("\n", bansResult.Value);

        var rulings = rulingsResult.Value;
        var rulingBlock = rulings.Count == 0
            ? ""
            : "\n\nGEVERIFIEERDE RULINGS (gezaghebbend):\n" +
              string.Join("\n", rulings.Select(r => $"- {r}"));

        var (claimsBlock, claimTraceRefs, askClaims) = claimsResult.Value;
        var (misconceptionBlock, misconceptionRefs, askMisconceptions) =
            misconceptionResult.Value;
        var (deckMetaBlock, deckMetaRefs) = deckMetaResult.Value;
        // Trace: zelfde kennislagen-veld als de claims, herkenbaar aan het
        // "misvatting:"- resp. "deckmeta:"-prefix — bewust geen eigen
        // kolom/migratie.
        claimTraceRefs.AddRange(misconceptionRefs);
        claimTraceRefs.AddRange(deckMetaRefs);

        // Alle retrieval-kanalen (incl. citatie-hydratie) zijn nu klaar (#152).
        retrievalMs = sw.ElapsedMilliseconds - retrievalStart;

        // Doorvraag-gesprek (#41): eerdere rondes gecapt in de prompt.
        var historyBlock = turns.Count == 0
            ? ""
            : "\n\nGESPREK TOT NU TOE (bouw hierop voort; herhaal niet wat al gezegd is):\n" +
              string.Join("\n", turns.Select(t =>
                  $"Vraag: {t.Question}\nAntwoord: {(t.Answer.Length > MaxHistoryAnswerChars ? t.Answer[..MaxHistoryAnswerChars] + "…" : t.Answer)}"));
        var questionLabel = turns.Count == 0 ? "Vraag" : "Vervolgvraag";

        // Agentic-gate (#107, docs/BRAIN.md §2.4): pas ná de retrieval
        // beslissen of deze vraag mag door-redeneren over het brein.
        //
        // Lege-retrieval-signaal, geherformuleerd (review #107): de letterlijke
        // gaps-definitie ("lege trace-velden", #52) is in gezonde productie
        // onbereikbaar — de nearest-neighbour-kanalen (vector-top-K, primer-
        // top-3, semantische kaartburen) geven áltijd wel iets terug, relevant
        // of niet. "De bank weet aantoonbaar niets" meten we daarom op de
        // kanalen die alleen bij een échte match iets opleveren: full-text
        // (textHits), kaartnaam/mechaniek/lexicaal (cardContext) en een
        // afstandsplafond op de primer-match. Semantische kaartburen tellen
        // bewust niet mee (kale top-K, geen bewijs). Embedding-uitval
        // (qv null, #100) kwalificeert bewust NIET: dan is de semantic_search
        // van het brein zelf óók degraded en zou het dure pad pieken op het
        // moment van minste opbrengst. Flag default off: zonder env verandert
        // er niets. NB: vindt de retrieval helemaal níets (topIds leeg), dan
        // is de vraag al vóór deze gate beantwoord met een eerlijke melding —
        // bewuste beperking: een lege kennisbank heeft de agent ook niets te
        // bieden.
        var emptyRetrieval = qv is not null
            && textHits.Count == 0
            && !cardContext.LexicalEvidence
            && !primerRelevant;
        var hasImage = images is { Count: > 0 };

        // Grondig-quotum (#153) mét TOCTOU-bescherming. Een permit is alléén
        // nodig als dit een écht gebruiker-geforceerde escalatie zou worden:
        // Grondig gekozen, flag aan, geen foto (vision-pad), en de gate zou 'm
        // niet zélf al escaleren (dan is de run gratis en boekt hij op de
        // gate — Fix 2). Voor die gevallen is quota irrelevant, dus quotaAvailable
        // = true houdt de attributie schoon.
        var autoWouldEscalate = AgenticGate.ShouldEscalate(
            type, gateCardMentions, emptyRetrieval, agenticMode, hasImage);
        var needsPermit = requestedApproach == AskApproach.Thorough
            && agenticMode != AgenticMode.Off
            && !hasImage
            && !autoWouldEscalate
            && userContext.User is not null;
        // De reservering combineert de db-teller (RequestUserContext.Usage,
        // door de quota-filter al geteld — geen tweede query) met de nu-lopende
        // reserveringen onder één korte lock, zodat gelijktijdige Grondig-
        // requests niet dezelfde teller zien. `using var` geeft de permit vrij
        // bij method-eind — óók bij een onverwachte exception of client-abort,
        // ná de agent-run en de metric-write.
        using var agenticPermit = needsPermit
            ? _agenticInFlight.TryReserve(
                userContext.User!.Id,
                userContext.Usage?.AgenticForced ?? 0,
                userContext.User!.DailyAgenticQuota)
            : null;
        var quotaAvailable = !needsPermit || agenticPermit is not null;
        var decision = AgenticGate.Decide(
            requestedApproach, agenticMode, type, gateCardMentions, emptyRetrieval,
            hasImage, quotaAvailable);
        var agentic = decision.Escalate;
        // Attributie voor metric/trace (#153): wie dwong de escalatie af.
        // "user"-rijen zijn tegelijk de teller van het Grondig-dagquotum.
        var escalatedBy = decision.Escalate
            ? (decision.DecidedBy == AskDecider.User ? "user" : "gate")
            : null;
        var approachUsed = AgenticGate.EffectiveApproach(decision, requestedApproach);

        // Streaming (#31): citaties/claims/vraagtype staan nu vast — vroeg
        // naar de UI zodat die alvast kan renderen terwijl het antwoord komt.
        // De aanpak-terugmelding (#153) gaat mee: een quota-terugval hoort
        // niet pas ná de lange agentic-wachtfase zichtbaar te worden.
        if (onMeta is not null)
            await onMeta(new AskStreamMeta(type.ToString(), citations, askClaims,
                approachUsed, decision.FallbackReason));

        // Met foto: het sterkere model — board-state-analyse vraagt echt zicht.
        // Blok-volgorde = de kennispiramide van #51: officieel (fragmenten,
        // rulings, kaartfeiten, banlijst) > primer (laag 1) > community-
        // interpretatie (laag 2) > deck-meta (laag 3, #267 — de zwakste laag) >
        // misvattingen (#125, negatieve kennis — onderaan, kleurt nooit het oordeel).
        // Brein-verrijking innen (zie waar breinTask start). Flag uit ⇒ null ⇒ leeg
        // blok ⇒ de prompt is byte-voor-byte de bestaande. Een client-abort (OCE met
        // ct geannuleerd) bubbelt bewust door; elke andere fout is al binnen
        // EnrichAsync afgevangen tot null.
        var breinEnrichment = await breinTask;
        var breinBlock = breinEnrichment?.PromptBlock ?? "";

        var prompt =
            $"Context-fragmenten:\n{context}{rulingBlock}{cardBlock}{banBlock}{primerBlock}{claimsBlock}{deckMetaBlock}{misconceptionBlock}{breinBlock}{historyBlock}\n\n{questionLabel}: {question}";
        var system = $"{BasePrompt}\n\n{QuestionRouter.StructureFor(type)}";
        var task = images is { Count: > 0 } ? "hard" : "cheap";

        // Bij escalatie vervángt de agentic call de finale LLM-call, mét
        // dezelfde contextblokken als startpunt (§2.4: de agent hoeft niet te
        // her-ontdekken wat de retrieval al vond). Bewust niet-streamend, óók
        // op de streamingroute: een multi-turn-agent streamt anders zijn
        // tussenstappen als deltas de UI in, en bij vangnet-inzet zouden twee
        // delta-stromen door elkaar lopen. Op succes gaat het antwoord als
        // één delta naar de UI; het final-frame blijft identiek.
        string? aiAnswer = null;
        string? brainSteps = null;
        string? agenticProposals = null;
        var agentAnswered = false;
        var clientGone = false;
        // Afrondende AI-fase (#152): bij agentic de hele agent-run (plus een
        // eventueel vangnet), bij streaming tot en met het slotframe.
        var aiSw = System.Diagnostics.Stopwatch.StartNew();
        if (agentic)
        {
            try
            {
                var agenticAnswer = await ai.AskAgenticAsync(prompt, system, images, ct, options.Model);
                // Usage van de agent-run (#121): de som over alle beurten die
                // rb-ai rapporteert — ook bij het vangnet hieronder blijft
                // een eventueel gemeten deel meetellen.
                usage = AddUsage(usage, agenticAnswer?.Usage);
                if (agenticAnswer?.Answer is { } agentText)
                {
                    aiAnswer = agentText;
                    agentAnswered = true;
                    brainSteps = agenticAnswer.Steps ?? "(agent deed geen tool-calls)";
                    // #120: het relatievoorstellen-blok dat rb-ai van het
                    // antwoord afsplitste; wordt in de afronding hieronder
                    // gevalideerd en opgeslagen (na de metric, vóór de trace).
                    agenticProposals = agenticAnswer.Relations;
                    if (onDelta is not null) await onDelta(agentText);
                }
                else
                {
                    // Vangnet (§2.4): agent faalt/timeout/leeg → de klassieke
                    // single-pass draait alsnog (hieronder). Geen dubbele
                    // kosten bij succes; de gebruiker merkt alleen extra
                    // wachttijd. Tool-calls die de agent vóór de uitval wél
                    // deed (uit rb-ai's fout-body) blijven in de trace staan.
                    brainSteps = (agenticAnswer?.Steps is { } partial ? partial + "\n" : "")
                        + "[vangnet: agent gaf geen antwoord — klassieke single-pass gedraaid]";
                    logger.LogWarning(
                        "agentic ask gaf geen antwoord — vangnet: klassieke single-pass");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client weggelopen tijdens de agentic call of de delta-flush
                // (review #107, zelfde invariant als #110/StreamAnswerAsync):
                // rb-ai aborteert de agent zelf al via de verbroken verbinding.
                // Hier géén vangnet meer starten — nieuwe LLM-kosten zonder
                // luisteraar — maar de afronding (metric/trace/quota) moet
                // wél landen; die draait hieronder op CancellationToken.None.
                clientGone = true;
                brainSteps ??= "[client afgehaakt tijdens de agentic call]";
            }
        }
        if (aiAnswer is null && !clientGone)
        {
            var single = onDelta is null
                // Model-sweep (#174): options.Model reist alleen hier expliciet
                // mee — StreamAnswerAsync blijft ongewijzigd omdat een
                // benchmarkrun (BenchmarkService) nooit streamt.
                ? await ai.AskWithUsageAsync(prompt, system, task, images, ct, options.Model)
                : await StreamAnswerAsync(prompt, system, task, images, onDelta, ct);
            aiAnswer = single?.Answer;
            usage = AddUsage(usage, single?.Usage);
        }
        aiMs = aiSw.ElapsedMilliseconds;
        var answer = aiAnswer ?? RbAiClient.UnavailableAnswer;

        // Kosten-eerlijkheid (#42, review #107): Model boekt het pad dat het
        // antwoord écht leverde — "agentic" (Sonnet-agent) alleen wanneer de
        // agent antwoordde; bij vangnet of niet-escaleren het gewone model.
        var usedModel = agentAnswered ? "agentic"
            : images is { Count: > 0 } ? "hard" : "cheap";

        // Vanaf hier bewust ZONDER request-token (review #31): de (LLM-)kosten
        // zijn gemaakt, dus de afronding — kaart-match, metric, trace — moet
        // ook landen als de client de tab al heeft dichtgeklapt. Anders
        // ontbreken juist de afgebroken vragen in AskMetrics/AskTraces en
        // divergeert het streamingpad van de niet-streamende route.
        var finishCt = CancellationToken.None;

        // Betrokken kaarten (herkend in vraag én antwoord) voor de kaart-
        // uitklap op de ruling-pagina.
        var cards = await MatchCardsAsync($"{qLower}\n{answer.ToLowerInvariant()}", finishCt);
        sw.Stop();

        // BENCHMARK-VLAG (#158): het hart van de isolatie-eis — een
        // benchmarkrun mag NIETS leren of meten buiten de benchmark-tabellen.
        // Retrieval en prompt hierboven zijn identiek aan een normale vraag;
        // vanaf hier onderdrukken we élk leer-/meetneveneffect: geen
        // ask_metric-rij (dus ook geen escalated_by-quotumtelling, #153), geen
        // agentic-relatie-terugkoppeling (#120) en geen ask_trace-rij (dus ook
        // geen phase-timings, #152, en geen ip_hash-stempel, #157).
        // BenchmarkService (Infrastructure) boekt zelf de benchmark_result-rij
        // met antwoord/duur/tokens.
        if (!options.Benchmark)
        {
            // Duurmeting voedt de echte "gemiddeld ±Xs"-indicatie op de vraag-
            // pagina (#59: uit het endpoint — de service meet hier toch al);
            // de token-teller (#121) voedt het kostenoverzicht in het beheer.
            await RecordMetricAsync(
                sw.ElapsedMilliseconds, type, images, ok: aiAnswer is not null,
                agentic: agentAnswered, model: usedModel, usage: usage,
                // #153: attributie op de póging (ook bij vangnet/abort) — de
                // kosten zijn dan al gemaakt, dus het quotum telt de rij mee.
                escalatedBy: escalatedBy);

            // Agentic-terugkoppeling (#120): door de agent ontdekte verbanden
            // als relatievoorstel achterlaten — het brein verrijkt zichzelf
            // al antwoordend. Best-effort én buiten de duurmeting: het
            // antwoord is al af (en op de streamingroute al verstuurd); een
            // haperende opslag mag het nooit alsnog blokkeren. De teller
            // landt in BrainSteps zodat de vraag-trace toont wat de agent
            // achterliet.
            if (agentAnswered && agenticProposals is not null)
            {
                try
                {
                    var harvest = await agenticRelations.StoreProposalsAsync(
                        question, agenticProposals, finishCt);
                    brainSteps += "\n" + harvest.TraceLine;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "relatievoorstellen uit de agentic ask niet opgeslagen");
                    brainSteps += "\n[relatievoorstellen: opslaan mislukt — zie logs]";
                }
            }

            // Denkstappen-trace voor het beheer (#40) — best-effort.
            try
            {
                db.AskTraces.Add(new AskTrace
                {
                    Question = question.Length > 500 ? question[..500] : question,
                    QuestionType = type.ToString(),
                    RewrittenQuery = rewrite is null ? null :
                        $"{rewrite.NormalizedQuestion} | queries: " +
                        $"{string.Join("; ", rewrite.SearchQueries)} | termen: " +
                        $"{string.Join("; ", rewrite.LexicalTerms)}",
                    SourceBias = sourceBias,
                    MentionsCard = mentionsCard,
                    MechanicMatches = string.Join(", ", cardContext.Mechanics),
                    // Degradatie-kanttekeningen (#100/#152): de beheerder moet in
                    // de trace kunnen zien dat een antwoord zonder vector-kanalen
                    // of met uitgevallen kanalen tot stand kwam (bewust zonder
                    // migratie — geen eigen kolom; de reden staat in de logs).
                    Sections = (qv is null
                            ? "[embedding-uitval: vector-kanalen overgeslagen] " : "")
                        + (failedChannels.Count > 0
                            ? $"[kanaal-uitval: {string.Join(", ", failedChannels)}] " : "")
                        + string.Join(", ", citations
                            .Where(c => c.Section != null).Select(c => $"§{c.Section}")),
                    ContextCards = string.Join(", ", cardContext.CardNames),
                    PrimerDocs = string.Join(", ", primerTitles),
                    CommunityClaims = string.Join(", ", claimTraceRefs),
                    VerifiedRulings = rulings.Count,
                    Model = usedModel,
                    HadImage = images is { Count: > 0 },
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    // Agentic ask (#107): Agentic = de agent leverde het antwoord;
                    // vangnet-inzet en client-aborts blijven zichtbaar via de
                    // marker in BrainSteps — dezelfde controleerbaarheid als de
                    // denkstappen.
                    Agentic = agentAnswered,
                    // #153: wie de escalatie afdwong (gate/gebruiker) — de badge
                    // in de beheer-traces; null als er niet geëscaleerd is.
                    EscalatedBy = escalatedBy,
                    BrainSteps = brainSteps,
                    // Het volledige gesprek (#143): het definitieve antwoord —
                    // op de streamingroute is dat het slotframe uit
                    // StreamAnswerAsync (of UnavailableAnswer bij uitval, Ok=false)
                    // — plus de gecapte doorvraag-beurten (#41) zoals ze als
                    // GESPREK-blok in de prompt meegingen.
                    Answer = answer,
                    History = SerializeHistory(turns),
                    // Per-fase-wandkloktijden (#152) — de fasen overlappen
                    // (parallelle pipeline), dus de som ≠ TotalMs.
                    PhaseTimings = new AskPhases(
                        rewriteMs, embedMs, retrievalMs, aiMs,
                        sw.ElapsedMilliseconds).ToJson(),
                    Ok = aiAnswer is not null,
                    UserId = userContext.User?.Id,
                    // Anonieme ask-geschiedenis (#157): zelfde IpHash-stempel als
                    // UserId hierboven — RequestUserContext.IpHash komt van de
                    // quota-filter (Api-laag), null zonder secret/IP.
                    IpHash = userContext.IpHash,
                });
                // Bewaar alleen de recente historie.
                var cutoff = await db.AskTraces
                    .OrderByDescending(t => t.CreatedAt)
                    .Skip(200)
                    .Select(t => t.CreatedAt)
                    .FirstOrDefaultAsync(finishCt);
                if (cutoff != default)
                    await db.AskTraces.Where(t => t.CreatedAt <= cutoff).ExecuteDeleteAsync(finishCt);
                await db.SaveChangesAsync(finishCt);
            }
            catch
            {
                // trace mag een antwoord nooit blokkeren
            }

            // Brein-AnswerTrace (#228, §6/#236): het immutable auditspoor van de
            // GraphRAG-retrieval — WELKE subgraaf/paden/trust-gewichten het antwoord
            // droegen, met de trust-waarde-toen. We schrijven de trace weg zodra het
            // brein-blok NIET-LEEG was, niet alleen bij dragende Supports: een NoPath-
            // oordeel, een gating-memo of een terugval-reden kleuren óók het antwoord
            // (ze staan in de prompt, AskService.cs breinBlock) terwijl Supports leeg
            // kan zijn — die beslissing mag geen onzichtbare state achterlaten (#236).
            // Een leeg blok (retrieval vond niets, prompt byte-identiek) → geen trace,
            // zodat de AnswerTraces-tabel niet met lege rijen vervuilt (#228-review).
            // Best-effort en apart van de AskTrace: een haperende trace-write mag het
            // antwoord (en de AskTrace) nooit blokkeren. Zichtbaar in de Brein-verkenner
            // (#236); Postgres = SoT, de Neo4j-USED_ASSERTION-projectie is een
            // integratie-follow-up.
            if (breinEnrichment is { PromptBlock.Length: > 0 })
            {
                try
                {
                    db.AnswerTraces.Add(breinEnrichment.Outcome.Trace);
                    await db.SaveChangesAsync(finishCt);
                }
                catch
                {
                    // brein-auditspoor mag een antwoord nooit blokkeren
                }
            }
        }

        return new(answer, citations, cards, type.ToString(),
            Ok: aiAnswer is not null, Claims: askClaims,
            Misconceptions: askMisconceptions,
            Approach: approachUsed, ApproachReason: decision.FallbackReason,
            Usage: usage is null ? null : new AskUsageInfo(usage.InputTokens, usage.OutputTokens));
    }

    /// <summary>Consumeert de rb-ai-stream: deltas door naar de UI, het
    /// done-frame is het volledige antwoord (met de token-usage van de run,
    /// #121). Een error-frame of een stream zonder done levert null — daarmee
    /// degradeert AskCoreAsync precies zoals bij de niet-streamende route
    /// (UnavailableAnswer, Ok=false).</summary>
    private async Task<RbAiClient.AiAnswer?> StreamAnswerAsync(
        string prompt, string system, string task,
        IReadOnlyList<RbAiClient.AiImage>? images,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        RbAiClient.AiAnswer? answer = null;
        try
        {
            await foreach (var frame in ai.AskStreamAsync(prompt, system, task, images, ct))
            {
                switch (frame.Type)
                {
                    case "delta" when !string.IsNullOrEmpty(frame.Text):
                        await onDelta(frame.Text);
                        break;
                    case "done":
                        answer = string.IsNullOrWhiteSpace(frame.Answer)
                            ? null
                            : new RbAiClient.AiAnswer(frame.Answer, frame.Usage);
                        break;
                    // "error" bewust genegeerd: answer blijft null → degradatie.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client weggelopen tijdens het streamen (review #31): geen fout —
            // we geven terug wat er is (meestal null), zodat AskCoreAsync de
            // afronding (metric/trace, zonder token) alsnog registreert i.p.v.
            // de vraag spoorloos te laten verdwijnen.
        }
        return answer;
    }

    /// <summary>Camel-case zoals de rest van de API-payloads — de beheer-UI
    /// leest de beurten rechtstreeks terug (#143).</summary>
    private static readonly JsonSerializerOptions HistoryJson =
        new(JsonSerializerDefaults.Web);

    /// <summary>Gesprek-snapshot voor de trace (#143): dezelfde gecapte
    /// beurten die als doorvraag-context meegingen (#41), als JSON
    /// `[{question, answer}]`; null bij een eerste vraag.</summary>
    private static string? SerializeHistory(IReadOnlyList<AskTurn> turns) =>
        turns.Count == 0 ? null : JsonSerializer.Serialize(turns, HistoryJson);

    /// <summary>Token-optelling per vraag (#121): null betekent "onbekend" en
    /// mag een wél gemeten deel niet wegdrukken — null + x = x; pas als álle
    /// calls zonder usage bleven, blijft het totaal null.</summary>
    private static AiUsage? AddUsage(AiUsage? a, AiUsage? b) =>
        a is null ? b : b is null ? a
            : new AiUsage(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);

    /// <summary>Uitkomst van één retrieval-kanaal (#152): het resultaat plus —
    /// bij uitval — de kanaalnaam voor de degradatie-marker in de trace.
    /// Kanalen gooien nooit (behalve echte client-annulering): één haperend
    /// kanaal betekent dat kanaal leeg, nooit een 500.</summary>
    private sealed record ChannelResult<T>(T Value, string? Failure);

    /// <summary>Eén lees-kanaal met eigen context (#152): mét factory een
    /// verse context per kanaal (zo kunnen kanalen concurrent draaien —
    /// DbContext is niet thread-safe), zonder factory de scoped context.</summary>
    private async Task<T> WithChannelDbAsync<T>(
        Func<RbRulesDbContext, Task<T>> query, CancellationToken ct)
    {
        if (dbFactory is null) return await query(db);
        await using var ctx = await dbFactory.CreateDbContextAsync(ct);
        return await query(ctx);
    }

    /// <summary>Kanaal-degradatie (#152): uitval van één kanaal levert de
    /// fallback plus de kanaalnaam (voor de trace-marker); details staan in
    /// de logs. Echte client-annulering bubbelt wél door.</summary>
    private async Task<ChannelResult<T>> ChannelAsync<T>(
        string name, T fallback, Func<Task<T>> run, CancellationToken ct)
    {
        try
        {
            return new(await run(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "retrieval-kanaal '{Kanaal}' uitgevallen — kanaal leeg, de vraag draait door",
                name);
            return new(fallback, name);
        }
    }

    /// <summary>Kanaal dat zelf een context nodig heeft: combinatie van
    /// <see cref="ChannelAsync{T}"/> en <see cref="WithChannelDbAsync{T}"/>.</summary>
    private Task<ChannelResult<T>> DbChannelAsync<T>(
        string name, T fallback, Func<RbRulesDbContext, Task<T>> query, CancellationToken ct) =>
        ChannelAsync(name, fallback, () => WithChannelDbAsync(query, ct), ct);

    /// <summary>Start één retrieval-kanaal (#152): mét factory draait het
    /// kanaal écht concurrent (de taak loopt door terwijl de aanroeper de
    /// volgende start); zonder factory draait het hier inline af — zonder
    /// eigen contexten is er geen veilig parallellisme, en de tests op EF
    /// InMemory delen één context. De aanroeper await't de teruggegeven taak
    /// op het vaste slot; de volgorde van resultaten is in beide standen
    /// identiek.</summary>
    private async Task<Task<ChannelResult<T>>> StartChannelAsync<T>(
        string name, T fallback, Func<Task<T>> run, CancellationToken ct)
    {
        var task = ChannelAsync(name, fallback, run, ct);
        if (dbFactory is null) await task;
        return task;
    }

    /// <summary>Start-variant voor kanalen die alleen een context nodig
    /// hebben.</summary>
    private Task<Task<ChannelResult<T>>> StartDbChannelAsync<T>(
        string name, T fallback, Func<RbRulesDbContext, Task<T>> query, CancellationToken ct) =>
        StartChannelAsync(name, fallback, () => WithChannelDbAsync(query, ct), ct);

    /// <summary>De query-rewrite-call (#66) als taak, zodat hij kan overlappen
    /// met de rewrite-onafhankelijke kanalen (#152). Rewrite-cache (#152,
    /// klein — LRU op de genormaliseerde vraag): een cache-hit slaat de hele
    /// rb-ai-call over (Ms = 0, geen usage — er is geen call gemaakt). Uitval
    /// of onzin-output is het bestaande degradatiepad: Rewrite null, zoeken
    /// met de rauwe tekst; een null-uitkomst wordt nooit gecacht, anders zou
    /// een tijdelijke rb-ai-hik het degradatiepad vastzetten voor de rest van
    /// de procesduur. Ms is de wandkloktijd van de call zelf (fase-meting).</summary>
    private async Task<(QueryRewrite? Rewrite, AiUsage? Usage, long Ms)> RunRewriteAsync(
        string retrievalText, CancellationToken ct)
    {
        var cacheKey = rewriteCache is null ? null : RewriteCache.NormalizeKey(retrievalText);
        if (cacheKey is not null)
        {
            var cached = rewriteCache!.TryGet(cacheKey);
            if (cached is not null) return (cached, null, 0);
        }

        var rewriteSw = System.Diagnostics.Stopwatch.StartNew();
        var res = await ai.AskWithUsageAsync(
            QueryRewriter.BuildPrompt(retrievalText), QueryRewriter.SystemPrompt, ct: ct);
        var rewrite = res is null ? null : QueryRewriter.Parse(res.Answer);
        if (rewrite is not null && cacheKey is not null) rewriteCache!.Set(cacheKey, rewrite);
        return (rewrite, res?.Usage, rewriteSw.ElapsedMilliseconds);
    }

    /// <summary>Embedding van de ruwe retrievaltekst, gestart naast de rewrite
    /// (#152). Best-effort (#100): uitval levert null — exact het bestaande
    /// vector-degradatiepad; echte client-annulering bubbelt wél door.</summary>
    private async Task<(Vector? Vec, long Ms)> EmbedRawAsync(
        string retrievalText, CancellationToken ct)
    {
        var rawSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var vecs = await embeddings.EmbedAsync([retrievalText], ct);
            return (vecs.Length > 0 ? vecs[0] : null, rawSw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // de vrager zelf haakte af — niet maskeren
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Embedding voor /ask mislukt — vector-kanalen overgeslagen, " +
                "degradatie naar FTS + lexicale kanalen");
            return (null, rawSw.ElapsedMilliseconds);
        }
    }

    /// <summary>Regels-vector-kanaal: nearest-neighbour per query-embedding —
    /// één ranked list per query voor de RRF-fusie (#152: per query een eigen
    /// kanaal, dus concurrent zodra er een factory is).</summary>
    private static async Task<List<(long Id, string SourceId)>> RuleChunkHitsAsync(
        RbRulesDbContext ctx, Vector vec, CancellationToken ct)
    {
        var hits = await ctx.RuleChunks.AsNoTracking()
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(vec))
            .Take(TopK * 2)
            .Select(c => new { c.Id, c.SourceId })
            .ToListAsync(ct);
        return [.. hits.Select(h => (h.Id, h.SourceId))];
    }

    /// <summary>Spelbegrip-primer (kennislaag 1, #49): goedgekeurde
    /// concept-docs die semantisch bij de vraag passen — geeft het model de
    /// flow van het spel, niet alleen losse §'s. Puur vector-gedreven, dus
    /// zonder query-vector (embedding-uitval, #100) vervalt dit kanaal.</summary>
    private static async Task<(List<string> Titles, string Block, bool Relevant)>
        PrimerChannelAsync(RbRulesDbContext ctx, Vector? qv, CancellationToken ct)
    {
        if (qv is null) return ([], "", false);
        // Distance gaat mee voor het lege-retrieval-signaal (#107): de
        // top-3 zelf is een kale nearest-neighbour (altijd gevuld zodra
        // er één approved doc bestaat) en zegt dus niets over relevantie.
        var primerDocs = await ctx.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer" && k.Status == "approved" && k.Embedding != null)
            .OrderBy(k => k.Embedding!.CosineDistance(qv))
            .Take(3)
            .Select(k => new { k.Title, k.Body, Distance = k.Embedding!.CosineDistance(qv) })
            .ToListAsync(ct);
        // Zelfde afstandsplafond als de claims-retrieval (#51): daarboven
        // is een buur wel "dichtstbijzijnd" maar niet aantoonbaar relevant.
        var relevant = primerDocs.Any(p => p.Distance <= ClaimRetrieval.MaxDistance);
        var block = primerDocs.Count == 0
            ? ""
            : "\n\nSPELBEGRIP (achtergrond, gedistilleerd uit de officiële regels; " +
              "de regels zelf blijven normatief):\n" +
              string.Join("\n\n", primerDocs.Select(p => $"[{p.Title}]\n{p.Body}"));
        return ([.. primerDocs.Select(p => p.Title)], block, relevant);
    }

    /// <summary>Geverifieerde rulings (self-learning override-laag) —
    /// semantisch gematcht op de vraag; zonder embedding (van de rulings óf
    /// van de vraag zelf, #100) vallen we terug op de recentste.</summary>
    private static async Task<List<string>> RulingsChannelAsync(
        RbRulesDbContext ctx, Vector? qv, CancellationToken ct)
    {
        var rulings = new List<string>();
        if (qv is not null)
            rulings = await ctx.Corrections.AsNoTracking()
                .Where(c => c.Status == "verified" && c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(3)
                .Select(c => c.Text)
                .ToListAsync(ct);
        if (rulings.Count == 0)
            rulings = await ctx.Corrections.AsNoTracking()
                .Where(c => c.Status == "verified")
                .OrderByDescending(c => c.VerifiedAt)
                .Take(3)
                .Select(c => c.Text)
                .ToListAsync(ct);
        return rulings;
    }

    /// <summary>Community-claims (kennislaag 2, #51): door de beheerder
    /// geaccepteerde claims, semantisch bij de vraag — een eigen, expliciet
    /// gelabeld kanaal, strikt gescheiden van de officiële lagen. Het
    /// router-gewicht bepaalt hoeveel er meegaan (ruling: weinig;
    /// lijst-/meta-vraag: meer). De afstand wordt bewust in-memory gecapt
    /// (bewezen vertaalbaar patroon, zie ClaimMiningService). Puur
    /// vector-gedreven, dus zonder query-vector (#100) vervalt dit kanaal.</summary>
    private static async Task<(string Block, List<string> Refs, List<AskClaim> Claims)>
        ClaimsChannelAsync(RbRulesDbContext ctx, Vector? qv, QuestionType type, CancellationToken ct)
    {
        if (qv is null) return ("", [], []);
        var claimHits = await ctx.Claims.AsNoTracking()
            .Where(c => c.Status == "accepted" && c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(ClaimRetrieval.TakeFor(type))
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement,
                c.Corroboration, c.TrustScore, c.OfficialStatus,
                Distance = c.Embedding!.CosineDistance(qv),
            })
            .ToListAsync(ct);
        var retrievedClaims = claimHits
            .Where(c => c.Distance <= ClaimRetrieval.MaxDistance)
            .ToList();
        var block = ClaimRetrieval.PromptBlock([.. retrievedClaims.Select(c =>
            new RetrievedClaim(c.TopicType, c.TopicRef, c.Statement,
                c.Corroboration, c.TrustScore, c.OfficialStatus))]);
        List<string> refs = [.. retrievedClaims.Select(c => $"{c.TopicType}:{c.TopicRef}")];

        // Bronnen per claim voor het uitklapbare "Community-consensus"-blok in
        // het antwoord (#51) — registernaam erbij voor leesbaarheid.
        var askClaims = new List<AskClaim>();
        if (retrievedClaims.Count > 0)
        {
            var claimIds = retrievedClaims.Select(c => c.Id).ToList();
            var claimSources = await ctx.ClaimSources.AsNoTracking()
                .Where(s => claimIds.Contains(s.ClaimId))
                .Join(ctx.Sources, cs => cs.SourceId, s => s.Id,
                    (cs, s) => new { cs.ClaimId, s.Name, cs.Url })
                .ToListAsync(ct);
            askClaims.AddRange(retrievedClaims.Select(c => new AskClaim(
                c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.OfficialStatus,
                [.. claimSources.Where(s => s.ClaimId == c.Id)
                    .Select(s => new AskClaimSource(s.Name, s.Url))])));
        }
        return (block, refs, askClaims);
    }

    /// <summary>Misvattingen-kanaal (#125): rejected/superseded claims mét
    /// weerlegging (StatusReason; de verwerp-notities van #124 komen hier
    /// later bij) zijn negatieve kennis — "zo zit het dus níet". Zelfde
    /// afstands-plafond als 5b, eigen cap en eigen label; de poort
    /// (ClaimRetrieval.SelectMisconceptions) is puur en getest, de query een
    /// virtual voorselectie (test-seam, zie MisconceptionCandidatesAsync) die
    /// zijn eigen kanaal-context beheert.</summary>
    private async Task<(string Block, List<string> Refs, List<AskMisconception> Items)>
        MisconceptionChannelAsync(Vector? qv, CancellationToken ct)
    {
        var misconceptions = ClaimRetrieval.SelectMisconceptions(
            await MisconceptionCandidatesAsync(qv, ct));
        var block = ClaimRetrieval.MisconceptionBlock(
            [.. misconceptions.Select(m => new RetrievedMisconception(
                m.TopicType, m.TopicRef, m.Statement, m.StatusReason!))]);
        List<string> refs = [.. misconceptions.Select(m =>
            $"misvatting:{m.TopicType}:{m.TopicRef}")];
        var items = new List<AskMisconception>();
        if (misconceptions.Count > 0)
        {
            // Beide bewijzen voor het misvatting-blok in de UI: het
            // community-citaat (bron + quote) én de officiële weerlegging.
            var misconceptionIds = misconceptions.Select(m => m.Id).ToList();
            var misconceptionSources = await WithChannelDbAsync(ctx =>
                ctx.ClaimSources.AsNoTracking()
                    .Where(s => misconceptionIds.Contains(s.ClaimId))
                    .Join(ctx.Sources, cs => cs.SourceId, s => s.Id,
                        (cs, s) => new { cs.ClaimId, s.Name, cs.Url, cs.QuoteExcerpt })
                    .ToListAsync(ct), ct);
            items.AddRange(misconceptions.Select(m => new AskMisconception(
                m.TopicType, m.TopicRef, m.Statement, m.StatusReason!,
                ClaimRetrieval.RebuttalSection(m.StatusReason!),
                [.. misconceptionSources.Where(s => s.ClaimId == m.Id)
                    .Select(s => new AskMisconceptionSource(s.Name, s.Url, s.QuoteExcerpt))])));
        }
        return (block, refs, items);
    }

    /// <summary>Deck-meta-kanaal (kennislaag 3, #267): het deck-gebruiks-
    /// signaal van het kaartdossier (DeckPopularityQuery — aandeel recente
    /// decks, gemiddeld aantal exemplaren, top-co-occurrence) voor de kaarten
    /// die letterlijk in de vraag genoemd worden, als expliciet gelabeld
    /// laag-3-blok (DeckMetaRetrieval, Domain). Bij meerdere matchende namen
    /// wint de langste (meest specifieke) naam — "Jinx, Loose Cannon" boven
    /// het substring-geraakte "Jinx" (zelfde motivatie als AgenticGate).
    /// Kaarten zonder enig deck-signaal leveren geen regel; zonder regels
    /// geen blok. Virtual als test-seam: de regressietest van #267 bewijst
    /// hiermee dat een niet-relevante vraag dit kanaal — en dus de
    /// deck-query's — nooit raakt.</summary>
    protected virtual async Task<(string Block, List<string> Refs)> DeckMetaChannelAsync(
        RbRulesDbContext ctx, string qLower, CancellationToken ct)
    {
        var namedCards = await CardsNamedIn(ctx, qLower)
            .OrderByDescending(c => c.Name.Length)
            .ThenBy(c => c.RiftboundId)
            .Take(DeckMetaRetrieval.MaxCards)
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);

        var items = new List<RetrievedDeckMeta>();
        var refs = new List<string>();
        foreach (var card in namedCards)
        {
            // CardsNamedIn filtert al op VariantOf == null, dus RiftboundId
            // ís de canonieke groeps-id die de deck-rijen dragen.
            var pop = await DeckPopularityQuery.ForCanonicalAsync(ctx, card.RiftboundId, ct);
            if (pop.DeckCount == 0) continue; // geen signaal → geen regel
            items.Add(new RetrievedDeckMeta(
                card.Name, pop.DeckCount, pop.RecentDeckCount, pop.Percentage,
                pop.AverageCopiesWhenPlayed, pop.ThinData,
                [.. pop.TopCoPlayed.Select(x => new DeckMetaCoPlay(x.Name, x.DeckCount))]));
            refs.Add($"deckmeta:card:{card.Name}");
        }
        return (DeckMetaRetrieval.PromptBlock(items), refs);
    }

    /// <summary>Full-text-kanaal (Engels — de bronnen zijn Engels; de rewrite
    /// levert een Engelse zoekzin, dus die matcht hier beter dan NL). Virtual
    /// als test-seam (#100): de tsvector-functies vertalen alleen naar
    /// Postgres, dus de degradatie-regressietest op EF InMemory vervangt dit
    /// kanaal door een simpele tekst-match. Beheert zijn eigen kanaal-context
    /// (#152): met factory een verse context, anders de scoped.</summary>
    protected virtual Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
        string searchText, CancellationToken ct) =>
        WithChannelDbAsync(async ctx =>
        {
            var hits = await ctx.RuleChunks.AsNoTracking()
                .Where(c => EF.Functions.ToTsVector("english", c.Text)
                    .Matches(EF.Functions.PlainToTsQuery("english", searchText)))
                .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                    .Rank(EF.Functions.PlainToTsQuery("english", searchText)))
                .Take(TopK * 2)
                .Select(c => new { c.Id, c.SourceId })
                .ToListAsync(ct);
            return hits.Select(h => (h.Id, h.SourceId)).ToList();
        }, ct);

    /// <summary>Nearest-neighbour-voorselectie voor het misvattingen-kanaal
    /// (#125): rejected/superseded claims mét weerlegging, op afstand van de
    /// vraag. De echte poort (weerlegging-eis, afstands-cap, cap) is
    /// ClaimRetrieval.SelectMisconceptions — puur en getest; die draait ook
    /// over wat deze query aanlevert. Virtual als test-seam: CosineDistance
    /// vertaalt alleen naar Postgres (zelfde reden als FullTextChunksAsync).
    /// Beheert zijn eigen kanaal-context (#152). Zonder query-vector
    /// (embedding-uitval, #100) vervalt het kanaal.</summary>
    protected virtual Task<List<MisconceptionCandidate>> MisconceptionCandidatesAsync(
        Vector? qv, CancellationToken ct)
    {
        if (qv is null) return Task.FromResult(new List<MisconceptionCandidate>());
        return WithChannelDbAsync(async ctx =>
        {
            var rows = await ctx.Claims.AsNoTracking()
                .Where(c => (c.Status == "rejected" || c.Status == "superseded")
                    && c.StatusReason != null && c.StatusReason != ""
                    && c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(ClaimRetrieval.MisconceptionCap)
                .Select(c => new
                {
                    c.Id, c.TopicType, c.TopicRef, c.Statement, c.Status, c.StatusReason,
                    Distance = c.Embedding!.CosineDistance(qv),
                })
                .ToListAsync(ct);
            return rows.Select(r => new MisconceptionCandidate(
                    r.Id, r.TopicType, r.TopicRef, r.Statement, r.Status, r.StatusReason,
                    r.Distance))
                .ToList();
        }, ct);
    }

    /// <summary>Canonieke kaarten waarvan de naam letterlijk in de (lowercase)
    /// tekst voorkomt — naam-match in SQL (#43), en het predicaat op één plek
    /// (#44: stond drie keer in deze service). Namen korter dan 4 tekens
    /// matchen niet ("Ax" zou overal in triggeren). Neemt de kanaal-context
    /// (#152): de aanroepende kanalen draaien elk op hun eigen context.</summary>
    private static IQueryable<Card> CardsNamedIn(RbRulesDbContext ctx, string lowerText) =>
        ctx.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Name.Length >= 4 &&
                        lowerText.Contains(c.Name.ToLower()));

    /// <summary>Duurmeting per vraag — best-effort en bewust zonder request-
    /// token: een afgebroken response mag een al gegeven antwoord niet uit de
    /// statistiek houden.</summary>
    private async Task RecordMetricAsync(
        long elapsedMs, QuestionType type, IReadOnlyList<RbAiClient.AiImage>? images,
        bool ok, bool agentic = false, string? model = null, AiUsage? usage = null,
        string? escalatedBy = null)
    {
        try
        {
            db.AskMetrics.Add(new AskMetric
            {
                DurationMs = (int)Math.Min(elapsedMs, int.MaxValue),
                QuestionType = type.ToString(),
                HadImage = images is { Count: > 0 },
                Ok = ok,
                // Account-koppeling + modelkeuze (#42): voedt de per-account-
                // quota en het kosten-overzicht in het beheer. Model = het
                // pad dat het antwoord echt leverde (cheap|hard|agentic).
                UserId = userContext.User?.Id,
                Model = model ?? (images is { Count: > 0 } ? "hard" : "cheap"),
                // #107: duurstatistiek toont het agentic-pad apart.
                Agentic = agentic,
                // #153: attributie gate/gebruiker — "user"-rijen zijn de
                // teller van het Grondig-dagquotum (UsageTodayAsync).
                EscalatedBy = escalatedBy,
                // #121: echte token-tellingen (som over alle calls van deze
                // vraag); null = geen usage ontvangen, onbekend ≠ 0.
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Meting mag een antwoord nooit blokkeren — en een mislukte
            // metric-rij mag niet als Added blijven hangen in de gedeelde
            // context, anders faalt de trace-save erna mee (review-fix).
            foreach (var entry in db.ChangeTracker.Entries<AskMetric>()
                         .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                         .ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    /// <summary>Betrokken kaarten ná het antwoord — draait sequentieel op de
    /// scoped context (alle kanalen zijn dan al geïnd).</summary>
    private async Task<List<AskCard>> MatchCardsAsync(string lowerText, CancellationToken ct)
    {
        // Ban-status per variantgroep (#44); zonder embedding-vectoren —
        // de kaart-uitklap toont feiten (#43).
        var cards = await CardsNamedIn(db, lowerText)
            .Take(6)
            .WithoutEmbedding()
            .ToListAsync(ct);
        if (cards.Count == 0) return [];

        var banned = await BanLookup.BannedCanonicalIdsAsync(db, ct);
        // Set-legaliteit (#68): de kaartwidget labelt kaarten uit een nog
        // niet verschenen set als "nog niet legaal".
        var setDates = await SetDatesAsync(db, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return [.. cards.Select(c =>
        {
            var legalFrom = c.SetId is null ? null : setDates.GetValueOrDefault(c.SetId);
            return new AskCard(
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains, c.Energy, c.Might,
                c.TextPlain, c.Mechanics, c.ImageUrl, BanLookup.IsBanned(banned, c),
                c.SetLabel ?? c.SetId, legalFrom,
                SetLegality.Key(SetLegality.StatusFor(legalFrom, today)),
                c.ImageWidth, c.ImageHeight, c.ImageAltText);
        })];
    }

    /// <summary>Releasedatum per set (handvol rijen) — voor de legaliteits-
    /// status in kaartfeiten en kaart-widgets (#22/#68).</summary>
    private static Task<Dictionary<string, DateOnly?>> SetDatesAsync(
        RbRulesDbContext ctx, CancellationToken ct) =>
        ctx.CardSets.AsNoTracking().ToDictionaryAsync(s => s.SetId, s => s.PublishedOn, ct);

    /// <summary>LexicalEvidence (#107): had minstens één níet-semantisch
    /// kanaal (kaartnaam, mechaniek-keyword of lexicale tekst-match) een hit?
    /// De semantische buren tellen niet — die zijn kale nearest-neighbour en
    /// dus altijd gevuld; voor het lege-retrieval-signaal is alleen echt
    /// bewijs relevant.</summary>
    private sealed record CardContextResult(
        string Block, IReadOnlyList<string> Mechanics, IReadOnlyList<string> CardNames,
        bool LexicalEvidence);

    /// <summary>Ruimere limiet voor lijst-/opsommingsvragen (#67).</summary>
    private const int ListCardLimit = 40;

    /// <summary>Kaartcontext via drie kanalen: exacte naam-matches, herkende
    /// mechaniek-keywords ("wat is Deflect?" → kaarten mét Deflect) en
    /// semantische buren van de vraag. Zo is er áltijd kaart-bewijs, ook als
    /// de regels-PDF een keyword niet expliciet definieert. Lijstvragen (#67)
    /// krijgen een vierde, lexicaal kanaal: ILIKE op kaarttekst per zoekterm
    /// uit de rewrite (#66), met ruimere limiet en een expliciete
    /// afkap-melding richting de prompt ("eerste N van M"). Zonder
    /// query-vector (embedding-uitval, #100) vervalt alleen het semantische
    /// kanaal — naam, mechaniek en lexicaal blijven werken.</summary>
    private static async Task<CardContextResult> CardContextAsync(
        RbRulesDbContext ctx, string question, string qLower, Vector? qv,
        QuestionType type, QueryRewrite? rewrite, CancellationToken ct)
    {
        var isList = type == QuestionType.Lijst;

        // 1. Exacte naam-matches, in SQL (#43).
        var nameHits = await CardsNamedIn(ctx, qLower)
            .Take(3)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);

        // 2. Mechaniek-keywords in de vraag (én in de rewrite: "deflekt" →
        // "Deflect") → voorbeeldkaarten + telling.
        var mechanicCorpus = rewrite is null
            ? question
            : $"{question}\n{rewrite.NormalizedQuestion}\n{string.Join('\n', rewrite.SearchQueries)}";
        var allMechanics = (await ctx.Cards.AsNoTracking()
                .Where(c => c.Mechanics != null && c.VariantOf == null)
                .Select(c => c.Mechanics!)
                .ToListAsync(ct))
            .SelectMany(m => m)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchedMechanics = allMechanics
            .Where(m => m.Length >= 3 && Regex.IsMatch(
                mechanicCorpus, $@"\b{Regex.Escape(m)}\b", RegexOptions.IgnoreCase))
            .Take(2)
            .ToList();

        var mechanicBlocks = new List<string>();
        var mechanicCardIds = new List<string>();
        foreach (var m in matchedMechanics)
        {
            var count = await ctx.Cards.CountAsync(
                c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(m), ct);
            var examples = await ctx.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(m))
                .OrderBy(c => c.RiftboundId)
                .Take(isList ? ListCardLimit : 4)
                .Select(c => c.RiftboundId)
                .ToListAsync(ct);
            mechanicCardIds.AddRange(examples);
            mechanicBlocks.Add($"Mechaniek '{m}' komt voor op {count} kaarten; voorbeelden hieronder.");
        }

        // 2b. Lexicaal kanaal voor lijstvragen (#67): ILIKE op kaarttekst per
        // zoekterm uit de rewrite. Eén query per term (bewezen vertaalbaar,
        // zie CardEndpoints); rangschikking op aantal geraakte termen gebeurt
        // in-memory. Zonder rewrite is de ruimere semantiek het vangnet.
        var lexicalIds = new List<string>();
        if (isList && rewrite is { LexicalTerms.Length: > 0 })
        {
            var matchCounts = new Dictionary<string, int>();
            foreach (var term in rewrite.LexicalTerms)
            {
                var pattern = "%" + EscapeLike(term) + "%";
                var idsForTerm = await ctx.Cards.AsNoTracking()
                    .Where(c => c.VariantOf == null && c.TextPlain != null &&
                                EF.Functions.ILike(c.TextPlain!, pattern, "\\"))
                    .Select(c => c.RiftboundId)
                    .ToListAsync(ct);
                foreach (var id in idsForTerm)
                    matchCounts[id] = matchCounts.GetValueOrDefault(id) + 1;
            }
            lexicalIds = [.. matchCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)];
        }

        // 3. Semantische buren van de vraag (als vangnet; ruimer bij
        // lijstvragen — zeker als de rewrite geen lexicale termen opleverde).
        // Zonder query-vector (#100) vervalt dit kanaal.
        var semanticIds = new List<string>();
        if (qv is not null)
            semanticIds = await ctx.Cards.AsNoTracking()
                .Where(c => c.Embedding != null && c.VariantOf == null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(isList ? 12 : 4)
                .Select(c => c.RiftboundId)
                .ToListAsync(ct);

        var cap = isList ? ListCardLimit : 8;
        var merged = nameHits.Concat(lexicalIds).Concat(mechanicCardIds)
            .Concat(semanticIds).Distinct().ToList();
        var ids = merged.Take(cap).ToList();
        var lexicalEvidence = nameHits.Count > 0 || matchedMechanics.Count > 0
            || lexicalIds.Count > 0;
        if (ids.Count == 0) return new("", matchedMechanics, [], lexicalEvidence);

        // Afkap-melding (#67): nooit stilzwijgend inkorten — de prompt moet
        // "eerste N van M" kunnen zeggen.
        if (isList && lexicalIds.Count > 0)
        {
            var includedLexical = lexicalIds.Count(ids.Contains);
            if (includedLexical < lexicalIds.Count)
                mechanicBlocks.Add(
                    $"LET OP: de tekst-zoek vond {lexicalIds.Count} kaarten die aan de " +
                    $"zoektermen voldoen; hieronder staan er {includedLexical}. Meld in het " +
                    $"antwoord expliciet dat dit de eerste {includedLexical} van " +
                    $"{lexicalIds.Count} gevonden kaarten zijn.");
        }

        var cards = await FetchPromptCardsAsync(ctx, ids, ct);
        var banned = await BanLookup.BannedCanonicalIdsAsync(ctx, ct);

        // Set-legaliteit (#68): kaarten uit een nog niet verschenen set krijgen
        // een expliciet "NOG NIET LEGAAL"-feit mee als bewijs voor het model.
        var setDates = await SetDatesAsync(ctx, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var cardsById = cards.ToDictionary(c => c.RiftboundId);
        var lines = ids
            .Where(cardsById.ContainsKey)
            .Select(id => cardsById[id])
            .Select(c =>
            {
                var fact = SetLegality.PromptFact(
                    c.SetId is null ? null : setDates.GetValueOrDefault(c.SetId),
                    today, c.SetLabel ?? c.SetId);
                return "- " + CardText.DescribeForPrompt(c, BanLookup.IsBanned(banned, c))
                     + (fact is null ? "" : $" {fact}");
            });

        var header = mechanicBlocks.Count > 0
            ? string.Join("\n", mechanicBlocks) + "\n"
            : "";
        var block = "\n\nKaartgegevens (gezaghebbend voor stats/mechanieken; " +
               "kaartteksten zijn bewijs voor hoe keywords werken):\n" +
               header + string.Join("\n", lines);
        var includedNames = ids
            .Where(cardsById.ContainsKey)
            .Select(id => cardsById[id].Name)
            .ToList();
        return new(block, matchedMechanics, includedNames, lexicalEvidence);
    }

    /// <summary>Kaarten voor de prompt, geprojecteerd zónder embedding —
    /// 1024 floats × tot 40 kaarten hoort niet over de lijn (#67).</summary>
    private static async Task<List<Card>> FetchPromptCardsAsync(
        RbRulesDbContext ctx, List<string> ids, CancellationToken ct)
    {
        var rows = await ctx.Cards.AsNoTracking()
            .Where(c => ids.Contains(c.RiftboundId))
            .Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains,
                c.Energy, c.Might, c.TextPlain, c.Mechanics, c.ImageUrl, c.VariantOf,
            })
            .ToListAsync(ct);
        // Bewust zónder de presentatievelden (#270): de prompt krijgt
        // kaartfeiten, geen alt-tekst — die kan lokaal afgeleid zijn en is
        // dus geen officiële bron.
        return [.. rows.Select(r => new Card
        {
            RiftboundId = r.RiftboundId, Name = r.Name, Type = r.Type,
            Supertype = r.Supertype, Domains = r.Domains, Energy = r.Energy,
            Might = r.Might, TextPlain = r.TextPlain, Mechanics = r.Mechanics,
            ImageUrl = r.ImageUrl, VariantOf = r.VariantOf,
        })];
    }

    /// <summary>LIKE/ILIKE-metatekens in een zoekterm onschadelijk maken
    /// (escape-teken is backslash, zie de ILike-aanroep).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
