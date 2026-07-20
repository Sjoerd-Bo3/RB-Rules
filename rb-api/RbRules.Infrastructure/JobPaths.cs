namespace RbRules.Infrastructure;

/// <summary>Eén stap in een pad (#190): verwijst naar een bestaande
/// <see cref="JobCatalog"/>-job door naam (gevalideerd — zie
/// JobPathsTests). <paramref name="Drain"/> laat <see cref="PathRunner"/>
/// diezelfde job herhalen tot <see cref="JobOutcome.Drained"/> true is (geen
/// cap meer geraakt) — <paramref name="MaxRepeats"/> is de harde vangrail en
/// de no-progress-guard in PathRunner de zachte, zodat een permanent capte
/// of stilstaande job een pad nooit eindeloos laat doorlopen. Drain hoort
/// alleen op per-run gecapte jobs: een ongecapte job doet zijn hele backlog
/// in één run en zou bij Drain alleen zijn failures herkauwen.
///
/// <paramref name="Uncapped"/> (#258) draait de stap via
/// <see cref="JobDefinition.RunUncapped"/> — de per-run cap gaat eraf en de
/// pad-deadline gaat erin. Alleen zinvol op jobs die zo'n variant hébben
/// (JobPathOrderTests dwingt dat af) en nooit samen met Drain: een ongecapte run
/// verwerkt in één keer alles wat binnen de deadline past, dus een drain-lus
/// zou er alleen overheen lopen tót de deadline al verstreken is.</summary>
public sealed record PathStep(
    string JobName, bool Drain = false, int MaxRepeats = 10, bool Uncapped = false);

/// <summary>Een pad: een naam (Kind in run_log/JobLedger, en de padnaam die
/// als "job" verschijnt op /api/admin/status) + geordende stappen.
///
/// <paramref name="ContinueOnError"/> (#258) maakt het pad best-effort per
/// stap in plaats van stop-bij-fout: een gefaalde stap wordt als "error"
/// gelogd en de keten loopt door. Dat is de semantiek die de "alles"-keten en
/// de nachtrun altijd al hadden (CONVENTIONS: "een haperende externe dienst
/// stopt nooit de hele run") en die ze bij het opgaan in het pad-mechanisme
/// moesten houden — een uurenlange nachtrun mag niet stranden op één 5xx van
/// rb-ai. De handmatige paden houden bewust stop-bij-fout: daar staat een
/// beheerder naar te kijken en is doorlopen op een kapotte basis zinloos.
/// Afbreken via beheer (#253) stopt élk pad, ook een best-effort pad — dat is
/// geen fout maar een beslissing.</summary>
public sealed record PathDefinition(
    string Name, IReadOnlyList<PathStep> Steps, bool ContinueOnError = false);

/// <summary>Catalogus van beheerpaden (#190): geordende JobCatalog-jobs die
/// vanzelf doorstromen — één klik (of scheduler, zie ScanScheduler) runt de
/// hele keten via <see cref="PathRunner"/>, met dezelfde JobRunner-
/// éénjob-gate, dezelfde live-Progress en dezelfde run_log-zichtbaarheid als
/// een losse job. Nieuwe paden zijn puur declaratief: een lijst
/// <see cref="PathStep"/>'s die elk naar een bestaande JobCatalog-naam
/// wijzen — geen wijziging aan JobRunner, de losse jobs of de
/// mining-services zelf nodig.
///
/// Sinds #258 is het pad het ENIGE ketenmechanisme: de oude, met de hand
/// geschreven ketens in JobCatalog ("all" via RunAllAsync, "nachtrun" via
/// RunNightlyAsync) zijn weg. Beide zijn nu dunne aliassen die een
/// PathDefinition door <see cref="PathRunner"/> draaien — daarmee erven ze
/// gratis wat ze misten: een run_log-regel per stap, drain-semantiek op de
/// gecapte miners en één plek waar de volgorde staat.
///
/// De wipe (<c>regenerateknowledge</c>) en de brein-resets zitten bewust in
/// GEEN pad: dat blijven expliciete, destructieve Gevarenzone-acties
/// (#187/#190/#263).</summary>
public static class JobPaths
{
    public static PathDefinition? Find(string name) => All.GetValueOrDefault(name);

    // ── De vier bouwstenen ────────────────────────────────────────────────
    // LET OP: statische initializers draaien in TEKSTVOLGORDE. De
    // stap-arrays hieronder moeten dus vóór AllPaths/AllUpdate/Nightly staan,
    // anders zijn ze daar nog null.
    // Elk pad is een fase van de kennisketen; de samengestelde ketens
    // ("alles bijwerken", de nachtrun) plakken ze aan elkaar in plaats van
    // hun eigen volgorde te herhalen. Eén bron voor de volgorde.

    /// <summary>Ingest-pad: nieuwe/gewijzigde BRONNEN volledig verwerken.
    /// <c>rules-index</c> en <c>bans</c> stonden tot #258 alleen in de
    /// "alles"-keten en ontbraken hier — precies het gat dat "alles"
    /// inhoudelijk nog nodig maakte. Let op: de keten gebruikt de
    /// INCREMENTELE regelindexering (<c>rules-index</c>, force:false), niet de
    /// losse <c>rules</c>-knop die bewust álles herbouwt (force:true, na een
    /// parser-verbetering). Anders her-chunkt en her-embedt elke nachtrun de
    /// volledige regelindex voor niets.</summary>
    private static readonly PathStep[] IngestSteps =
    [
        new("scan"),
        // Nieuwe/gewijzigde documenten indexeren + de banlijst/errata opnieuw
        // structureren — beide leunen op wat "scan" zojuist binnenhaalde.
        new("rules-index"),
        new("bans"),
        // Review-fix #190: classify is ONgecapt — één run verwerkt de hele
        // backlog, dus draineren zou alleen de zojuist gefaalde items futiel
        // herkauwen. Geen Drain; failures komen bij de volgende pad-run of
        // scheduler-tick vanzelf terug.
        new("classify"),
        // Changeconsolidatie (#206): ná classify (ChangeType/Summary moeten
        // ingevuld zijn om kandidaat-paren te kunnen beoordelen). Ook ongecapt:
        // het aantal ongekoppelde changes binnen het venster is klein.
        new("consolidatechanges"),
    ];

    /// <summary>Kaart-pad: nieuwe/gewijzigde KAARTEN door de pijplijn.
    /// <c>mine</c> (mechaniek-mining) is kaart-afgeleid en hoort hier — tot
    /// #258 stond hij in het ingest-pad, waar hij niets met bronnen te maken
    /// had. <c>graph</c> sluit af: de projectie wil de verse mechanieken zien.</summary>
    private static readonly PathStep[] CardSteps =
    [
        new("cards"),
        new("embed"),
        new("mine", Drain: true),
        new("graph"),
    ];

    /// <summary>Kennis-pad: de LLM-afgeleide kennislaag bijwerken zonder de
    /// bron-scan opnieuw te draaien. Elke mining-stap draint (de Phase
    /// 2-regeneratieles: claims/clarify hadden meerdere runs nodig om hun cap
    /// niet meer te raken). "relationtriage" (#199 v1) staat ná "relations"
    /// (er moet iets te triageren zijn). "primer" stond tot #258 alleen in het
    /// aparte pad "full" — dat pad wás het kennis-pad plus primer en is nu
    /// overbodig, dus primer staat hier op zijn logische plek: ná de mining
    /// (hij vat samen wat er ligt) en vóór de graph-afsluiter.</summary>
    private static readonly PathStep[] KnowledgeSteps =
    [
        new("claims", Drain: true),
        new("clarify", Drain: true),
        new("relations", Drain: true),
        new("relationtriage", Drain: true),
        new("primer"),
        new("graph"),
    ];

    /// <summary>Brein-pad (#258, nieuw): de gereïficeerde brein-laag.
    /// <c>breinentiteiten</c> staat vooraan hoewel het issue hem niet noemt —
    /// zonder die (deterministische, goedkope) stap is de canonieke
    /// entiteitenlaag leeg en vindt <c>breinmine-predicaten</c> NUL subjects
    /// (#250). De nachtrun deed hem daarom al; dat gedrag blijft.
    ///
    /// HARDE VOLGORDE-EIS: dit pad hoort ná een <c>graph</c>-stap te draaien
    /// (die zit als afsluiter in het kaart- én kennis-pad). <c>graph</c> doet
    /// een DETACH DELETE over ZIJN labelset en <c>breinprojectie</c> schrijft
    /// een strikt disjuncte labelset — omgekeerd zou de graph-rebuild de verse
    /// brein-projectie niet raken, maar de basis-graaf waar de brein-knopen
    /// aan hangen zou dan pas ná de projectie ontstaan. PathOrderTests legt
    /// dit vast voor de samengestelde ketens.</summary>
    private static readonly PathStep[] BrainSteps =
    [
        new("breinentiteiten"),
        new("breinmine-interacties", Drain: true),
        new("breinmine-predicaten", Drain: true),
        new("breinprojectie"),
        new("reason"),
    ];

    /// <summary>Dezelfde stappen, maar de genoemde jobs ongecapt (#258): de
    /// nachtrun is inhoudelijk de gecapte keten met de caps eraf, niet een
    /// eigen volgorde. Programmatisch afgeleid i.p.v. overgetypt, zodat een
    /// nieuwe stap in een bouwsteen niet stil uit de nachtrun wegvalt.
    /// Uncapped en Drain sluiten elkaar uit (zie <see cref="PathStep"/>).</summary>
    private static PathStep[] WithUncapped(IReadOnlyList<PathStep> steps, params string[] jobNames) =>
        [.. steps.Select(s => jobNames.Contains(s.JobName)
            ? s with { Drain = false, Uncapped = true }
            : s)];

    /// <summary>De "alles bijwerken"-keten (#258): ingest + kaart, gecapt.
    /// Dit is wat de oude <c>RunAllAsync</c> deed — minus <c>interactions</c>
    /// (de legacy paar-lexicale miner, zie JobCatalog) en plus het gat dat het
    /// ingest-pad had. Bewust GEEN kennis- of brein-stappen: "alles bijwerken"
    /// is de goedkope, dagelijkse bijwerkactie ná een deploy, geen LLM-marathon.
    /// Best-effort per stap, zoals RunAllAsync altijd al was.
    ///
    /// Niet in <see cref="AllPaths"/>: net als <see cref="Nightly"/> is dit de
    /// keten áchter een bestaande jobnaam ("all"), niet een los pad — anders
    /// botst de padnaam met de job die rb-web, de docs en het run_log-grootboek
    /// al kennen.</summary>
    public static PathDefinition AllUpdate { get; } =
        new("all", [.. IngestSteps, .. CardSteps], ContinueOnError: true);

    /// <summary>De nachtrun-keten (#245, als pad sinds #258): dezelfde
    /// bouwstenen als "alles bijwerken", gevolgd door het brein-pad, met de
    /// dure miners ONGECAPT (<see cref="PathStep.Uncapped"/> → de pad-deadline
    /// begrenst ze in plaats van een per-run cap).
    ///
    /// Bewust ZONDER het kennis-pad: de nachtrun draaide dat nooit (claims,
    /// clarify en relations hebben hun eigen scheduler-cadans, primer draait
    /// alleen op verzoek omdat elke run drafts ter review oplevert). Dat
    /// erbij trekken zou én het LLM-budget van de brein-mining opeten én
    /// elke nacht een review-stapel produceren — een aparte beslissing, geen
    /// bijvangst van deze opschoning.
    ///
    /// Niet in <see cref="AllPaths"/>: de nachtrun is geen los startbaar pad
    /// maar de keten áchter de job "nachtrun", die er zijn venster-deadline
    /// aan meegeeft (JobCatalog). Zo blijft de padnaam vrij van botsing met
    /// de jobnaam die de scheduler en het grootboek al kennen.</summary>
    public static PathDefinition Nightly { get; } = new("nachtrun",
    [
        .. IngestSteps,
        // Ongecapt i.p.v. gedraineerd: de mining-services stoppen zelf netjes
        // op de deadline en hun watermark bewaart de voortgang voor de
        // volgende nacht.
        .. WithUncapped(CardSteps, "mine"),
        .. WithUncapped(BrainSteps, "breinmine-interacties", "breinmine-predicaten"),
    ], ContinueOnError: true);

    /// <summary>De los startbare paden (/api/admin/paths). De samengestelde
    /// ketens <see cref="AllUpdate"/> en <see cref="Nightly"/> staan hier
    /// bewust NIET tussen — zie hun eigen commentaar.</summary>
    public static IReadOnlyList<PathDefinition> AllPaths { get; } =
    [
        new PathDefinition("ingest", IngestSteps),
        new PathDefinition("card", CardSteps),
        new PathDefinition("knowledge", KnowledgeSteps),
        new PathDefinition("brein", BrainSteps),
    ];

    private static readonly Dictionary<string, PathDefinition> All =
        AllPaths.ToDictionary(p => p.Name);
}
