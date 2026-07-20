namespace RbRules.Domain.Ontology;

/// <summary>De VIER standen die een geprojecteerde edge kan hebben t.o.v.
/// <see cref="OntologySchema"/>. Elke stand draagt een harde, toetsbare invariant
/// over het register (zie <see cref="ProjectionEdgeCatalog"/>) — een stand is dus
/// geen etiket maar een bewering die rood kan gaan.</summary>
public enum ProjectionEdgeStance
{
    /// <summary>Geregistreerde domeinrelatie: de naam MOET via
    /// <see cref="OntologySchema.RelationByEdgeName"/> resolven.</summary>
    InSchema,

    /// <summary>Hoort inhoudelijk in de TBox, staat er nog niet in. De naam mag
    /// (nog) NIET resolven; reden én issue-referentie zijn verplicht, zodat de
    /// schuld een adres heeft in plaats van een lijstregel.</summary>
    DomeinNogNietGedeclareerd,

    /// <summary>Herkomst-/bewijstrail (PROV-O en aanverwant). Bewust buiten de
    /// TBox: dit zegt waar een feit vandaan komt, niet wat het spel is. De naam
    /// mag NIET resolven.</summary>
    Provenance,

    /// <summary>Eigen boekhouding van de kennisbank (entiteitsresolutie,
    /// schema-versionering, mining-administratie). Bewust buiten de TBox — het
    /// gaat over ONZE structuren, niet over Riftbound. De naam mag NIET
    /// resolven.</summary>
    Infrastructuur,
}

/// <summary>Eén geprojecteerde edge met zijn stand t.o.v. de ontologie.
/// <see cref="Reason"/> is altijd verplicht (een classificatie zonder motivering is
/// een lijst die niemand kan reviewen). <see cref="Issue"/> is VERPLICHT bij
/// <see cref="ProjectionEdgeStance.DomeinNogNietGedeclareerd"/> — erkende schuld
/// hoort naar een openstaand spoor te wijzen — maar TOEGESTAAN bij elke stand.
///
/// Dat laatste is een correctie uit de #289-review: de eerste versie verbood een
/// issue-referentie buiten die ene stand, waardoor juist de twee edges met een
/// OPEN defect-issue (#296: <c>SUPERSEDES</c> declareert een range die de
/// projectie schendt, <c>RELATES_TO</c> matcht label-loos) als schone beslissing
/// geboekt stonden. Een catalogus met als motto "geen edge zonder beslissing" mag
/// een bekend defect niet stil wegpoetsen omdat de relatie toevallig wél
/// geregistreerd is: <c>InSchema</c> zegt "de NAAM staat in het register", niet
/// "er is niets aan de hand".</summary>
public sealed record ProjectionEdge(
    string EdgeName,
    ProjectionEdgeStance Stance,
    string Reason,
    string? Issue = null)
{
    /// <summary>Alleen <see cref="ProjectionEdgeStance.InSchema"/> mag in
    /// <see cref="OntologySchema"/> voorkomen; de andere drie standen beweren
    /// expliciet dat de naam daar NIET staat.</summary>
    public bool MustResolveInSchema => Stance == ProjectionEdgeStance.InSchema;
}

/// <summary>Het register van élke edge die de twee volledige graph-projecties
/// (<c>GraphSyncService</c> en <c>BreinProjectionService</c>) naar Neo4j schrijven,
/// met per edge de beslissing uit #289: hoort hij in de ontologie, of valt hij daar
/// bewust buiten?
///
/// WAAROM DIT GEEN "LIJST DIE NIEMAND ONDERHOUDT" IS. De guard
/// (<c>ProjectionOntologyGuardTests</c>) draait beide projecties tegen een
/// opnemende Neo4j-driver en vergelijkt de ECHT uitgevoerde Cypher met deze
/// catalogus in twee richtingen: een geschreven edge die hier niet staat is rood
/// (G1), én een entry hier die niemand meer schrijft is óók rood (G2). De catalogus
/// kan dus niet vóór- of achterlopen op de projectie. Een derde check (G3) toetst
/// dat de stand niet liegt over <see cref="OntologySchema"/>, en een vierde (G4)
/// dat het opgenomen Cypher-corpus rij-onafhankelijk is — de aanname waarop G1
/// rust (elke projectie-stap vuurt onvoorwaardelijk, óók met lege <c>$rows</c>).
///
/// SCOPE. Alleen de twee volledige rebuild-projecties staan hier. <c>INTERACTS_WITH</c>
/// (uit <c>InteractionService</c>) heeft geen entry nodig: die edge-naam wordt
/// rechtstreeks uit <see cref="OntologySchema"/> geïnterpoleerd en kan per
/// constructie niet uiteenlopen — een sterkere garantie dan een catalogus-regel.
///
/// WAT DIT BEWUST NIET DOET. De catalogus is géén onderdeel van
/// <c>OntologySnapshot.Capture</c>: twee artefacten met twee levenscycli. De
/// ontologie-vingerafdruk beschrijft het SCHEMA; deze catalogus beschrijft wat de
/// PROJECTIE doet. Ze horen apart te kunnen bewegen — anders zou het opruimen van
/// projectie-schuld een schema-versiebump forceren. En de catalogus toetst
/// uitsluitend NAMEN: of een geprojecteerde edge ook domain/range-conform is
/// (<c>(:Erratum)-[:SUPERSEDES]->(:Card)</c> voldoet bijvoorbeeld níet aan de
/// gedeclareerde range <c>NormativeSource</c>) is een aparte vraag met een eigen
/// guard — zie #289 PR 2.</summary>
public static class ProjectionEdgeCatalog
{
    /// <summary>Het spoor waar de erkende domeinschuld wordt opgelost. Bewust NIET
    /// #289 zelf (#289-review, F8): dat issue sluit met deze guard, en dan wijst elke
    /// schuld-entry naar een gesloten issue terwijl de schuld nog leeft — een adres
    /// dat stil verloopt is geen adres.</summary>
    private const string SchuldSpoor = "#304";

    private static readonly ProjectionEdge[] EdgeList =
    [
        // ── InSchema: geregistreerde domeinrelaties ──────────────────────────
        new("HAS_DOMAIN", ProjectionEdgeStance.InSchema,
            "Kaart → kleur/domein. Sinds #274 uit het register afgeleid (FacetMergeClause)."),
        new("HAS_MECHANIC", ProjectionEdgeStance.InSchema,
            "Kaart → mechaniek. Sinds #274 uit het register afgeleid (FacetMergeClause)."),
        new("GOVERNED_BY", ProjectionEdgeStance.InSchema,
            "Gereïficeerde Interaction → de normatieve RuleSection die haar verankert."),
        // LET OP: deze twee dragen een OPEN defect (#296). De naam staat in het
        // register — daarom InSchema — maar de declaratie eromheen klopt niet met
        // wat de projectie schrijft. Een naam-guard vangt dat per constructie niet.
        new("SUPERSEDES", ProjectionEdgeStance.InSchema,
            "Erratum → de kaart waarvan het de gedrukte tekst vervangt. DEFECT (#296): het "
            + "register declareert range NormativeSource, maar Card is dat niet — de "
            + "gedeclareerde range is onwaar over de graaf die we bouwen. Nog te beslissen of "
            + "de declaratie verruimd moet worden of de projectie fout is; #270-les: meet "
            + "eerst op de live graaf.", "#296"),
        new("RELATES_TO", ProjectionEdgeStance.InSchema,
            "Gedenormaliseerde retrieval-projectie met kind als property (#116/#226). DEFECT "
            + "(#296): de projectie matcht label-loos op ref (MATCH (a {ref: …})), dus de "
            + "gedeclareerde domain/range is per constructie NIET afdwingbaar — wat er ook aan "
            + "die ref hangt, de edge wordt geschreven.", "#296"),

        // ── Domein, nog niet gedeclareerd (erkende schuld, #289 stap 1) ──────
        new("ABOUT", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Claim/Ruling → het onderwerp waarover zij iets beweren. Een inhoudelijke "
            + "kennisrelatie: zonder haar is niet uitdrukbaar waar een community-lezing "
            + "of officiële ruling over gaat. Vraagt om domain [Claim, Ruling] en een "
            + "range die Card/Mechanic/RuleSection/Concept omvat.", SchuldSpoor),
        new("PART_OF", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "RuleSection → dichtstbijzijnde bestaande ouder-sectie binnen dezelfde bron. "
            + "Mereologie over de normatieve tak; transitief en acyclisch, dus een echte "
            + "TBox-relatie met logische eigenschappen die nu nergens vastliggen.", SchuldSpoor),
        new("EXPLAINS", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Concept (primer-doc) → de RuleSection(s) waarop het gebaseerd is. De "
            + "kennispiramide-brug tussen afgeleide uitleg en officiële tekst — precies "
            + "het soort relatie waarvoor de ontologie bestaat.", SchuldSpoor),
        new("FROM_SET", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Kaart → de set waarin zij verscheen. Het register kent hiervoor al "
            + "INTRODUCED_IN (Card/Keyword/Mechanic → Set, functioneel), maar de "
            + "projectie schrijft FROM_SET. Spiegelbeeld van de HAS_MECHANIC-tweespalt "
            + "uit #274: één relatie, twee namen — alleen loopt de breuk hier tussen een "
            + "DODE declaratie en een levende projectie.", SchuldSpoor),
        new("HAS_TAG", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Kaart → factie/tribe. Er is geen EntityType.Tag, dus dit vraagt eerst een "
            + "klasse-beslissing (Tag als eigen Concept-subklasse?) vóór de relatie "
            + "gedeclareerd kan worden.", SchuldSpoor),
        new("HAS_ROLE", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Interaction → rol-filler, met de rol als edge-property. Het scherpste geval "
            + "uit #289: OntologyValidationService.ValidateReifiedInteraction VALIDEERT "
            + "deze rolstructuur al, terwijl de relatie zelf geen geregistreerd "
            + "RelationType is — de poort beroept zich op iets wat het schema niet kent.",
            SchuldSpoor),
        new("REQUIRES_CONDITION", ProjectionEdgeStance.DomeinNogNietGedeclareerd,
            "Interaction → gereïficeerde Condition. Beide EINDPUNTEN zijn wél "
            + "gedeclareerde klassen (Interaction, Condition); alleen de kant ertussen "
            + "ontbreekt. Let op de valstrik: REQUIRES bestaat wel als relatie, maar dat "
            + "is de gekwalificeerde (reïficatie-plichtige) relatie — een ander ding.",
            SchuldSpoor),

        // ── Provenance: bewust buiten de TBox ────────────────────────────────
        new("WAS_GENERATED_BY", ProjectionEdgeStance.Provenance,
            "PROV-O letterlijk: Assertion → de MiningRun-activity die haar voortbracht "
            + "(#233). Herkomst van een bewering, geen uitspraak over het spel."),
        new("DERIVED_FROM", ProjectionEdgeStance.Provenance,
            "PROV-O letterlijk: Assertion → de bron waaruit het feit is afgeleid (#233). "
            + "Zelfde argument als WAS_GENERATED_BY."),
        new("SUPPORTED_BY", ProjectionEdgeStance.Provenance,
            "Claim/Ruling → de Source die haar staaft. Geen PROV-O-term, wel dezelfde "
            + "soort uitspraak: bewijsvoering/herkomst. Inhoudelijk zegt de edge niets "
            + "over Riftbound — hij zegt wie het beweerde. De kennispiramide-weging zit "
            + "in Source.TrustTier, niet in deze kant."),
        new("AFFECTS", ProjectionEdgeStance.Provenance,
            "Change → de kaart/sectie die een gedetecteerde bronwijziging raakt. Dit is "
            + "de brontrail van ons monitoringsproces (ARCHITECTURE §6.3: de graph draagt "
            + "de volledige, ongefilterde trail), niet een spelrelatie: Change is dan ook "
            + "geen EntityType en hoort er ook geen te worden."),

        // ── Infrastructuur: onze eigen boekhouding ───────────────────────────
        new("MERGED_INTO", ProjectionEdgeStance.Infrastructuur,
            "CanonicalEntity-tombstone → de overlevende entiteit (#227 fase 1). "
            + "Entiteitsresolutie-administratie met een herstelpad; gaat over onze "
            + "tabellen, niet over het spel."),
        new("HAS_PREDICATE", ProjectionEdgeStance.Infrastructuur,
            "CanonicalEntity → MechanicPredicate (#227 fase 5). De predicaten zelf zijn "
            + "een staging-laag met een eigen reviewpoort; ze worden pas domein zodra ze "
            + "gepromoveerd zijn tot geregistreerde relaties."),
        new("PRECEDES", ProjectionEdgeStance.Infrastructuur,
            "OntologyVersion → de opvolgende versie (#227 fase 6). Een keten over het "
            + "SCHEMA zelf; die in het schema declareren zou de ontologie naar zichzelf "
            + "laten wijzen."),
    ];

    /// <summary>Alle geclassificeerde edges, in declaratievolgorde.</summary>
    public static readonly IReadOnlyList<ProjectionEdge> All = EdgeList;

    /// <summary>Geïndexeerd op canonieke edge-naam (hoofdletterongevoelig, zelfde
    /// lijn als <see cref="OntologySchema.RelationByEdgeName"/>).</summary>
    public static readonly IReadOnlyDictionary<string, ProjectionEdge> ByEdgeName =
        EdgeList.ToDictionary(e => e.EdgeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>De edges met een gegeven stand.</summary>
    public static IEnumerable<ProjectionEdge> WithStance(ProjectionEdgeStance stance) =>
        EdgeList.Where(e => e.Stance == stance);
}
