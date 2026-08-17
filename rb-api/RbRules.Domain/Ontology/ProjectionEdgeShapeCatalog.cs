namespace RbRules.Domain.Ontology;

/// <summary>De KANT van een edge waarop een bevinding slaat. <see cref="From"/>
/// wordt tegen <see cref="OntologyRelation.Domain"/> gehouden, <see cref="To"/>
/// tegen <see cref="OntologyRelation.Range"/>.</summary>
public enum EdgeEndpoint
{
    From,
    To,
}

/// <summary>Wat er mis is met één kant van één geprojecteerde edge-vorm. Alleen
/// PROBLEMEN krijgen een verdict — een conforme kant levert geen bevinding op.</summary>
public enum ProjectionLabelVerdict
{
    /// <summary>De projectie legt op deze kant GEEN knooplabel op
    /// (<c>MATCH (a {ref: …})</c>), dus de gedeclareerde domain/range is per
    /// constructie niet af te dwingen: wat er ook aan die ref hangt, de edge wordt
    /// geschreven. Geen fout in de gebruikelijke zin, maar wél een gat in de
    /// belofte van het register — en dus iets waarover expliciet besloten moet
    /// zijn.</summary>
    Unenforceable,

    /// <summary>De projectie legt wél labels op, maar géén ervan valt binnen de
    /// gedeclareerde domain/range (subklasse-polymorf, <see cref="OntologySchema.IsA"/>).
    /// Een label dat de ontologie helemaal niet als klasse kent telt hier ook onder:
    /// het register is de ÉNE bron, dus een ongedeclareerd label kan een
    /// gedeclareerde range niet vervullen.</summary>
    Violates,
}

/// <summary>Eén knooplabel-vorm waarin de projectie een edge schrijft:
/// <c>(:Claim)-[:ABOUT]-&gt;(:Card)</c>. Een LEGE labellijst betekent
/// "label-loze match" — de projectie legt daar niets op.
///
/// De labels zijn die welke het STATEMENT garandeert, niet elk label dat de knoop
/// in de graaf toevallig draagt. <c>MATCH (ix:Interaction {ref: …})</c> levert dus
/// <c>Interaction</c>, ook al draagt diezelfde knoop in Neo4j
/// <c>:Interaction:Concept</c> (de live graaf meet daarom bij
/// <c>REQUIRES_CONDITION</c> beide labels als domain). Dat is bewust: de guard
/// toetst wat de query afdwingt, want dát is wat een latere wijziging kan
/// breken.
///
/// Sinds #317 kan een kant DISJUNCTIEF zijn: een WHERE-label-disjunctie
/// (<c>MATCH (a {ref: …}) WHERE (a:Card OR a:Mechanic OR …)</c>) garandeert dat de
/// knoop ÉÉN van de genoemde soorten is — wezenlijk iets anders dan een
/// multi-label patroon (<c>:Interaction:Concept</c>), waar de knoop ÁLLE labels
/// draagt. Het verschil bepaalt de toets (zie <see cref="ProjectionLabelCheck"/>):
/// conjunctief volstaat één passend label, disjunctief moet élke soort binnen de
/// declaratie vallen — anders laat de disjunctie een niet-conforme knoop door.</summary>
public sealed record ProjectionEdgeShape(
    string EdgeName,
    IReadOnlyList<string> FromLabels,
    IReadOnlyList<string> ToLabels)
{
    /// <summary>De from-kant is een label-disjunctie (WHERE <c>a:X OR a:Y</c>)
    /// in plaats van een multi-label garantie.</summary>
    public bool FromDisjunctive { get; init; }

    /// <summary>Spiegelbeeld voor de to-kant.</summary>
    public bool ToDisjunctive { get; init; }

    /// <summary>Labels van één kant, of <c>null</c> voor een onbekende kant.</summary>
    public IReadOnlyList<string> Labels(EdgeEndpoint side) =>
        side == EdgeEndpoint.From ? FromLabels : ToLabels;

    /// <summary>Is de gegeven kant disjunctief gebonden?</summary>
    public bool Disjunctive(EdgeEndpoint side) =>
        side == EdgeEndpoint.From ? FromDisjunctive : ToDisjunctive;

    /// <summary>Cypher-achtige weergave, gebruikt als vergelijkings- én
    /// foutmeldingssleutel: <c>(:Claim)-[:ABOUT]-&gt;(:Card)</c>, en
    /// <c>(:Assertion)-[:DERIVED_FROM]-&gt;()</c> voor een label-loze kant. Labels worden
    /// gesorteerd zodat de volgorde waarin ze in de Cypher staan geen contract
    /// is — een multi-label knoop is een verzameling, geen lijst. Een disjunctieve
    /// kant rendert met <c>|</c> (Cypher's eigen label-expressiesyntaxis):
    /// <c>(:Card|Concept)</c> is een ándere bewering dan <c>(:Card:Concept)</c>
    /// en moet dus ook een andere sleutel opleveren.</summary>
    public string Format() =>
        $"({LabelText(FromLabels, FromDisjunctive)})-[:{EdgeName}]->({LabelText(ToLabels, ToDisjunctive)})";

    public static string LabelText(IReadOnlyList<string> labels) =>
        LabelText(labels, disjunctive: false);

    public static string LabelText(IReadOnlyList<string> labels, bool disjunctive) =>
        labels.Count == 0
            ? ""
            : ":" + string.Join(disjunctive ? "|" : ":", labels.Order(StringComparer.Ordinal));
}

/// <summary>Eén bevinding over één kant van één vorm. <see cref="Key"/> is de
/// sleutel waarop een <see cref="KnownLabelDefect"/> matcht.</summary>
public sealed record ProjectionLabelFinding(
    string EdgeName,
    EdgeEndpoint Side,
    ProjectionLabelVerdict Verdict,
    IReadOnlyList<string> Labels,
    bool Disjunctive = false)
{
    public string Key => $"{EdgeName}|{Side}|{Verdict}|{ProjectionEdgeShape.LabelText(Labels, Disjunctive)}";
}

/// <summary>Een BEKEND, gedocumenteerd verschil tussen wat de projectie schrijft en
/// wat <see cref="OntologySchema"/> declareert.
///
/// Dit is nadrukkelijk GEEN mute-knop. Een waiver is zelf een bewering die rood kan
/// gaan, in twee richtingen (zelfde constructie als G1/G2 in PR 1): élke bevinding
/// moet door precies één waiver gedekt zijn (anders is er nieuwe drift), én élke
/// waiver moet een bevinding hébben die nog bestaat. Wordt het defect gerepareerd —
/// of verandert het van vorm — dan gaat de waiver rood met de opdracht hem te
/// verwijderen. Zo kan een erkend defect niet stil blijven staan nadat het weg is,
/// en kan het net zomin stil verdwijnen uit het zicht terwijl het er nog is.
///
/// <see cref="Issue"/> is verplicht: een defect zonder adres is een TODO die
/// niemand terugvindt (#289-review, F8).</summary>
public sealed record KnownLabelDefect(
    string EdgeName,
    EdgeEndpoint Side,
    ProjectionLabelVerdict Verdict,
    IReadOnlyList<string> Labels,
    string Reason,
    string Issue,
    bool Disjunctive = false)
{
    public string Key => $"{EdgeName}|{Side}|{Verdict}|{ProjectionEdgeShape.LabelText(Labels, Disjunctive)}";
}

/// <summary>De pure toets: voldoen de knooplabels die de projectie schrijft aan de
/// domain/range die <see cref="OntologySchema"/> declareert?
///
/// Alleen edges die in het register RESOLVEN worden getoetst — bij de standen
/// <c>DomeinNogNietGedeclareerd</c>/<c>Provenance</c>/<c>Infrastructuur</c> is er
/// per definitie geen declaratie om tegen te houden. Zodra zo'n edge wél
/// gedeclareerd wordt, gaat deze toets er vanzelf overheen lopen: dan is de vorm
/// ineens een bewering die kan kloppen of niet — precies wat er in #304 met de
/// zeven voormalige schuld-edges gebeurde.</summary>
public static class ProjectionLabelCheck
{
    /// <summary>Alle problemen met deze vorm (leeg = conform, of niets te
    /// toetsen).</summary>
    public static IReadOnlyList<ProjectionLabelFinding> Findings(ProjectionEdgeShape shape)
    {
        var relation = OntologySchema.RelationByEdgeName(shape.EdgeName);
        if (relation is null) return [];

        var findings = new List<ProjectionLabelFinding>();
        Add(findings, shape, EdgeEndpoint.From, relation, relation.Domain);
        Add(findings, shape, EdgeEndpoint.To, relation, relation.Range);
        return findings;
    }

    private static void Add(
        List<ProjectionLabelFinding> findings,
        ProjectionEdgeShape shape,
        EdgeEndpoint side,
        OntologyRelation relation,
        IReadOnlyList<EntityType> declared)
    {
        var labels = shape.Labels(side);
        var disjunctive = shape.Disjunctive(side);

        // EERST de sterkste uitspraak. Een gekwalificeerde relatie (COUNTERS,
        // MODIFIES, GRANTS, REQUIRES) is VERBODEN als kale edge — ze hoort via een
        // Interaction gereïficeerd te worden — en dan doet het er niet toe wélke
        // labels eraan hangen, of dat het er geen zijn. Zou de label-loos-tak
        // hiervóór staan, dan kwam zo'n verboden edge met een label-loze kant terug
        // als het mildere "niet te garanderen", en dat is precies de verdoezeling die
        // deze guard moet voorkomen. Vuurt vandaag nergens (geen van de vier wordt
        // geprojecteerd), maar de dag dat er één opduikt hoort dat luidruchtig te
        // zijn — en de volgorde bepaalt hóe luid.
        if (relation.MustReify || declared.Count == 0)
        {
            findings.Add(new(shape.EdgeName, side, ProjectionLabelVerdict.Violates, labels, disjunctive));
            return;
        }

        // Label-loze match: de projectie legt hier niets op. Niet fout, wel
        // ongegarandeerd — en dus een expliciete beslissing waard.
        if (labels.Count == 0)
        {
            findings.Add(new(shape.EdgeName, side, ProjectionLabelVerdict.Unenforceable, labels, disjunctive));
            return;
        }

        // Conjunctief (een knoop DRÁÁGT al zijn labels): één label binnen de
        // gedeclareerde klassen volstaat (subklasse-polymorf: een Unit voldoet aan
        // een Object-domein). Disjunctief (WHERE a:X OR a:Y, #317): de knoop is
        // slechts ÉÉN van de soorten, dus élke soort moet binnen de declaratie
        // vallen — anders laat het statement een knoop door die de declaratie
        // schendt, en is de declaratie dus smaller dan wat er afgedwongen wordt.
        // Een label dat de ontologie niet kent voldoet nooit.
        var satisfied = disjunctive
            ? labels.All(label => Satisfies(label, declared))
            : labels.Any(label => Satisfies(label, declared));

        if (!satisfied)
            findings.Add(new(shape.EdgeName, side, ProjectionLabelVerdict.Violates, labels, disjunctive));
    }

    private static bool Satisfies(string label, IReadOnlyList<EntityType> declared) =>
        OntologySchema.ParseEntityType(label) is { } type &&
        declared.Any(d => OntologySchema.IsA(type, d));
}

/// <summary>Het register van de knooplabel-VORMEN waarin de twee volledige
/// graph-projecties hun edges schrijven — één laag dieper dan
/// <see cref="ProjectionEdgeCatalog"/>, dat alleen NAMEN classificeert (#289 PR 2).
///
/// WAAROM EEN NAAM-GUARD NIET GENOEG IS. PR 1 bewaakt "schrijft de projectie een
/// edge waarover niemand een beslissing nam". Dat ziet per constructie niet dat een
/// edge de VERKEERDE KLASSEN verbindt. #296 is daar het levende bewijs van:
/// <see cref="OntologySchema"/> declareert <c>SUPERSEDES</c> als
/// <c>NormativeSource → NormativeSource</c>, terwijl <c>GraphSyncService</c>
/// <c>(:Erratum)-[:SUPERSEDES]-&gt;(:Card)</c> schrijft — en <c>Card</c> is geen
/// <c>NormativeSource</c>. Dezelfde driftklasse als #274, één laag dieper, en
/// jarenlang onopgemerkt omdat niets de eindpunten las.
///
/// WAT HIER STAAT. Per edge élke (bronlabels, doellabels)-combinatie die de
/// projectie schrijft. <c>ABOUT</c> heeft er acht (Claim en Ruling × vier
/// doelsoorten), <c>AFFECTS</c>, <c>SUPPORTED_BY</c> en <c>HAS_ROLE</c> elk twee,
/// de rest één.
/// Een lege labellijst = label-loze match; die staat er expliciet in, want een
/// stille overgang van "gebonden" naar "label-loos" is precies het soort
/// dekkingsverlies dat deze guard moet betrappen.
///
/// HOE HET EERLIJK BLIJFT. <c>ProjectionLabelGuardTests</c> draait beide projecties
/// tegen een opnemende driver en leest de UITGEVOERDE Cypher, net als PR 1 — maar
/// nu inclusief de alias-binding per statement, zodat
/// <c>MATCH (cl:Claim …) … MERGE (cl)-[:ABOUT]-&gt;(t)</c> weet dat <c>cl</c> een
/// <c>:Claim</c> is. De toets loopt twee richtingen: een geschreven vorm die hier
/// niet staat is rood, én een vorm hier die niemand meer schrijft is óók rood.
///
/// GEMETEN, NIET GEGOKT. De vormen hieronder zijn tegen de live graaf gehouden
/// (productie, na de deploy van <c>b2493b6</c>) en komen daar één-op-één uit:
/// <c>PART_OF</c> RuleSection→RuleSection (2139), <c>HAS_TAG</c> Card→Tag (982),
/// <c>FROM_SET</c> Card→Set (963), <c>EXPLAINS</c> Concept→RuleSection (101),
/// <c>ABOUT</c> Claim/Ruling → Card/Mechanic/RuleSection/Concept (77). Eén meting
/// weerlegde bovendien de documentatie: <c>HAS_ROLE</c> wijst naar <c>Card</c> (492)
/// en <c>Mechanic</c> (274) — NOOIT naar <c>Keyword</c>, wat
/// <c>OntologyValidationService.ValidateReifiedInteraction</c>, de miner én
/// ARCHITECTURE §11 alle drie beweerden (#270-les: bevraag de live bron, ga niet
/// af op wat een mapper mapt). Sinds #304 is die meting de gedeclareerde range,
/// dwingt de projectie haar per filler-soort af, en zijn de drie
/// Keyword-beweringen gecorrigeerd.</summary>
public static class ProjectionEdgeShapeCatalog
{
    private static readonly ProjectionEdgeShape[] ShapeList =
    [
        // ── GraphSyncService: kaart-facetten ─────────────────────────────────
        new("FROM_SET", ["Card"], ["Set"]),
        new("HAS_DOMAIN", ["Card"], ["Domain"]),
        new("HAS_TAG", ["Card"], ["Tag"]),
        new("HAS_MECHANIC", ["Card"], ["Mechanic"]),

        // ── Kennislagen ──────────────────────────────────────────────────────
        new("PART_OF", ["RuleSection"], ["RuleSection"]),
        new("EXPLAINS", ["Concept"], ["RuleSection"]),

        // ABOUT: vier doelsoorten × twee bronsoorten. Claim en Ruling delen de
        // resolutie (RulingTopicMapper → ClaimTopicMapper) maar zijn twee klassen,
        // dus acht vormen. Komt er ooit een negende doelsoort bij, dan hoort dat
        // een zichtbare regel in de diff te zijn.
        new("ABOUT", ["Claim"], ["Card"]),
        new("ABOUT", ["Claim"], ["Mechanic"]),
        new("ABOUT", ["Claim"], ["RuleSection"]),
        new("ABOUT", ["Claim"], ["Concept"]),
        new("ABOUT", ["Ruling"], ["Card"]),
        new("ABOUT", ["Ruling"], ["Mechanic"]),
        new("ABOUT", ["Ruling"], ["RuleSection"]),
        new("ABOUT", ["Ruling"], ["Concept"]),

        new("SUPPORTED_BY", ["Claim"], ["Source"]),
        new("SUPPORTED_BY", ["Ruling"], ["Source"]),

        // Sinds #296 conform: het register declareert Erratum → Card (de meting),
        // dus deze vorm heeft geen waiver meer nodig.
        new("SUPERSEDES", ["Erratum"], ["Card"]),

        new("AFFECTS", ["Change"], ["RuleSection"]),
        new("AFFECTS", ["Change"], ["Card"]),

        // ── Provenance-tak (#233) ────────────────────────────────────────────
        new("WAS_GENERATED_BY", ["Assertion"], ["MiningRun"]),
        // DERIVED_FROM matcht het doel label-loos op ref (Source/RuleSection/Card/…
        // door elkaar). Bewust: de doelen zijn heterogeen. Niet gedeclareerd in het
        // register, dus vandaag toetst de ontologie er niets aan.
        new("DERIVED_FROM", ["Assertion"], []),

        // ── Reïficatie-tak (#226) ────────────────────────────────────────────
        // RELATES_TO (#317): beide kanten disjunctief gebonden op de vijf GEMETEN
        // knoopsoorten — de live graaf droeg twaalf label-combinaties, allemaal
        // binnen Card/Mechanic/Concept/RuleSection/Claim (grootste: Card→Mechanic
        // 201). Twee statements schrijven deze vorm (de #116-relaties en de
        // qualifier-cache) met elk één WHERE-label-disjunctie per kant — bewust
        // GEEN 5×5 aan per-soort-statements: RELATES_TO is de gedenormaliseerde
        // elk-naar-elk-link. Een ref naar een knoop buiten de vijf wordt bewust
        // NIET geschreven; dat ís de afdwinging.
        new("RELATES_TO",
            ["Card", "Mechanic", "Concept", "RuleSection", "Claim"],
            ["Card", "Mechanic", "Concept", "RuleSection", "Claim"])
        { FromDisjunctive = true, ToDisjunctive = true },
        new("REQUIRES_CONDITION", ["Interaction"], ["Condition"]),
        // Sinds #304 per filler-soort label-gebonden (zoals ABOUT per doelsoort):
        // de gedeclareerde range Card/Mechanic wordt door het statement afgedwongen.
        new("HAS_ROLE", ["Interaction"], ["Card"]),
        new("HAS_ROLE", ["Interaction"], ["Mechanic"]),
        new("GOVERNED_BY", ["Interaction"], ["RuleSection"]),

        // ── BreinProjectionService (#227) ────────────────────────────────────
        new("MERGED_INTO", ["CanonicalEntity"], ["CanonicalEntity"]),
        new("HAS_PREDICATE", ["CanonicalEntity"], ["MechanicPredicate"]),
        new("PRECEDES", ["OntologyVersion"], ["OntologyVersion"]),
    ];

    /// <summary>Alle geregistreerde vormen, in declaratievolgorde.</summary>
    public static readonly IReadOnlyList<ProjectionEdgeShape> All = ShapeList;

    /// <summary>De vormen van één edge-naam (hoofdletterongevoelig, zelfde lijn als
    /// <see cref="ProjectionEdgeCatalog.ByEdgeName"/>).</summary>
    public static IEnumerable<ProjectionEdgeShape> For(string edgeName) =>
        ShapeList.Where(s => string.Equals(s.EdgeName, edgeName, StringComparison.OrdinalIgnoreCase));

    // LEEG sinds #317, en dat is een mijlpaal: élke geprojecteerde vorm is nu
    // conform de gedeclareerde domain/range. De twee RELATES_TO-waivers zijn
    // opgeruimd toen de WHERE-label-disjunctie de gemeten breedte afdwingbaar
    // maakte (de declaratie is tegelijk op diezelfde meting gebracht); de
    // SUPERSEDES-waiver was al bij #296 opgeruimd. L4 houdt dit eerlijk: een
    // waiver die hier terugkeert zonder levende bevinding gaat rood — een waiver
    // die zijn eigen defect overleeft dekt vanaf dat moment stil iets anders af.
    private static readonly KnownLabelDefect[] DefectList = [];

    /// <summary>De erkende, gedocumenteerde verschillen tussen projectie en register.
    /// Elke regel hier moet een bevinding hébben (anders is het defect weg en hoort de
    /// regel weg), en elke bevinding moet hier staan (anders is het nieuwe drift).</summary>
    public static readonly IReadOnlyList<KnownLabelDefect> KnownDefects = DefectList;
}
