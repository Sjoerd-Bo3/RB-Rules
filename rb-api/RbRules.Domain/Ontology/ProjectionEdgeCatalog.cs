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
/// OPEN defect-issue (#296: <c>SUPERSEDES</c> declareerde een range die de
/// projectie schond, <c>RELATES_TO</c> matchte label-loos — beide inmiddels
/// opgelost, #296 resp. #317) als schone beslissing geboekt stonden. Een catalogus
/// met als motto "geen edge zonder beslissing" mag een bekend defect niet stil
/// wegpoetsen omdat de relatie toevallig wél geregistreerd is: <c>InSchema</c>
/// zegt "de NAAM staat in het register", niet "er is niets aan de hand".</summary>
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
/// projectie-schuld een schema-versiebump forceren. En deze catalogus toetst
/// uitsluitend NAMEN: of een geprojecteerde edge ook domain/range-conform is
/// (tot #296 voldeed <c>(:Erratum)-[:SUPERSEDES]->(:Card)</c> bijvoorbeeld níet
/// aan de toen gedeclareerde range <c>NormativeSource</c>) is een aparte vraag,
/// met sinds #289 PR 2 een eigen register en eigen guard: zie
/// <see cref="ProjectionEdgeShapeCatalog"/>. Die twee horen bij elkaar — élke
/// naam hier heeft daar minstens één knooplabel-vorm, en omgekeerd (check L0).</summary>
public static class ProjectionEdgeCatalog
{
    private static readonly ProjectionEdge[] EdgeList =
    [
        // ── InSchema: geregistreerde domeinrelaties ──────────────────────────
        new("HAS_DOMAIN", ProjectionEdgeStance.InSchema,
            "Kaart → kleur/domein. Sinds #274 uit het register afgeleid (FacetMergeClause)."),
        new("HAS_MECHANIC", ProjectionEdgeStance.InSchema,
            "Kaart → mechaniek. Sinds #274 uit het register afgeleid (FacetMergeClause)."),
        new("GOVERNED_BY", ProjectionEdgeStance.InSchema,
            "Gereïficeerde Interaction → de normatieve RuleSection die haar verankert."),
        new("SUPERSEDES", ProjectionEdgeStance.InSchema,
            "Erratum → de kaart waarvan het de gedrukte tekst vervangt. Droeg tot #296 een "
            + "declaratie (range NormativeSource) die onwaar was over de graaf; sindsdien "
            + "volgt het register de meting (Erratum → Card) en dwingt de projectie de "
            + "range met knooplabels af — de bijbehorende waiver is opgeruimd."),
        new("RELATES_TO", ProjectionEdgeStance.InSchema,
            "Gedenormaliseerde retrieval-projectie met kind als property (#116/#226). Droeg "
            + "tot #317 een open defect (label-loze ref-match, domain/range onafdwingbaar); "
            + "sindsdien declareert het register de vijf GEMETEN eindpunt-soorten "
            + "(Card/Mechanic/Concept/RuleSection/Claim) en dwingen beide statements ze af "
            + "met één WHERE-label-disjunctie per kant — de twee waivers zijn opgeruimd."),

        // ── Sinds #304 gedeclareerd: de zeven voormalige schuld-edges ────────
        // De domain/range-beslissingen (mét de metingen waarop ze rusten) staan
        // bij de declaraties in OntologySchema; hier alleen de stand.
        new("ABOUT", ProjectionEdgeStance.InSchema,
            "Claim/Ruling → het onderwerp waarover zij iets beweren (#304): domain "
            + "[Claim, Ruling], range Card/Mechanic/RuleSection/Concept — het gemeten "
            + "2×4-vierkant van vormen."),
        new("PART_OF", ProjectionEdgeStance.InSchema,
            "RuleSection → dichtstbijzijnde bestaande ouder-sectie binnen dezelfde bron "
            + "(#304). Mereologie over de normatieve tak: transitief, acyclisch, 0..1 "
            + "directe ouder."),
        new("EXPLAINS", ProjectionEdgeStance.InSchema,
            "Concept (primer-doc) → de RuleSection(s) waarop het gebaseerd is (#304). De "
            + "kennispiramide-brug tussen afgeleide uitleg en officiële tekst."),
        new("FROM_SET", ProjectionEdgeStance.InSchema,
            "Kaart → de set waarin zij verscheen (#304). De dode INTRODUCED_IN-declaratie "
            + "is hernoemd naar de naam die de projectie al jaren schrijft — zelfde "
            + "beslissing als HAS_MECHANIC in #274: de naam volgt de projectie."),
        new("HAS_TAG", ProjectionEdgeStance.InSchema,
            "Kaart → factie/tribe (#304). De klasse-beslissing is genomen: Tag is een "
            + "eigen EntityType direct onder Thing (geen Concept — een tag draagt geen "
            + "regels, en Tag ⊑ Concept zou een dode HAS_TAG ∘ GOVERNED_BY-reasonerketen "
            + "genereren)."),
        new("HAS_ROLE", ProjectionEdgeStance.InSchema,
            "Interaction → rol-filler met de rol als edge-parameter (#304). Range is de "
            + "METING (Card/Mechanic, nooit Keyword) en wordt sinds #304 door twee "
            + "label-gebonden statements afgedwongen; ValidateReifiedInteraction leest "
            + "de range nu uit het register i.p.v. een eigen (onjuiste) lijst."),
        new("REQUIRES_CONDITION", ProjectionEdgeStance.InSchema,
            "Interaction → gereïficeerde Condition (#304). Domain is wat het statement "
            + "afdwingt (:Interaction). Niet verwarren met REQUIRES, de gekwalificeerde "
            + "(reïficatie-plichtige) relatie — een ander ding."),

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
