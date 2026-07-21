using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De knooplabel-guard (#289, PR 2) — één laag dieper dan
/// <see cref="ProjectionOntologyGuardTests"/>.
///
/// WAAROM. PR 1 bewaakt edge-NAMEN: schrijft de projectie een edge waarover niemand
/// een beslissing nam? Dat ziet per constructie niet dat een edge de verkeerde
/// KLASSEN verbindt. #296 is het bewijs: <c>OntologySchema</c> declareert
/// <c>SUPERSEDES</c> als <c>NormativeSource → NormativeSource</c>, terwijl
/// <c>GraphSyncService</c> <c>(:Erratum)-[:SUPERSEDES]-&gt;(:Card)</c> schrijft.
/// De naam klopt, de vorm niet — en dat bleef jaren onopgemerkt.
///
/// HOE. Zelfde constructie als PR 1: beide projecties draaien tegen een
/// <see cref="RecordingDriver"/> en de guard leest de UITGEVOERDE Cypher, nu met de
/// alias-binding erbij (<see cref="CypherEdgeScanner.WrittenEdgeShapes"/>), zodat
/// <c>MERGE (cl)-[:ABOUT]-&gt;(t)</c> weet dat <c>cl</c> in datzelfde statement als
/// <c>:Claim</c> gematcht is. Opmaak blijft geen contract: een alias hernoemen, de
/// query herformatteren of een statement naar een helper verplaatsen verandert geen
/// enkele vorm.
///
/// DE VIER CHECKS.
/// <list type="bullet">
/// <item><b>L1</b> — elke geschreven vorm staat in <c>ProjectionEdgeShapeCatalog</c>.</item>
/// <item><b>L2</b> — elke vorm in de catalogus wordt ook echt geschreven (dezelfde
///   twee-richtingen-eis als G1/G2: zonder deze kant is het over een jaar een lijst
///   die niemand onderhoudt).</item>
/// <item><b>L3</b> — elke schending van de gedeclareerde domain/range is gedekt door
///   een erkend, gedocumenteerd defect.</item>
/// <item><b>L4</b> — elk erkend defect hééft nog een schending. Dít is wat de waiver
///   van een mute-knop onderscheidt: repareer #296 en de waiver gaat rood met de
///   opdracht hem op te ruimen, in plaats van stil te blijven staan.</item>
/// </list>
///
/// WAT DEZE GUARD NIET ZIET, eerlijk benoemd. PR 1 heeft G5 (bron ⊆ uitgevoerd) om
/// een statement achter een uitstaande vlag te betrappen. Die toets is hier bewust
/// NIET herhaald op vormen: een heel bronbestand is geen statement, dus de aliassen
/// van álle statements zouden op één hoop komen (<c>c</c> is in
/// <c>GraphSyncService</c> zowel <c>:Card</c> als <c>:Condition</c>), en fragmenten
/// die pas bij het aanroepen worden samengesteld (<c>RunPairsAsync</c> plakt de
/// <c>MATCH (c:Card …)</c>-prefix er zelf voor) zouden als label-loos binnenkomen.
/// Dat levert vals alarm, en een guard die vals alarm slaat wordt uitgezet. Het
/// restrisico dat daarmee blijft staan — een TWEEDE statement dat een al uitgevoerde
/// edge-naam met ándere labels schrijft, achter een tak die de probe niet neemt —
/// staat in ARCHITECTURE §6.3.</summary>
public class ProjectionLabelGuardTests
{
    // ── L1: elke geschreven vorm is geregistreerd ─────────────────────────────

    [Fact]
    public async Task L1_ElkeGeschrevenVorm_StaatInDeCatalogus()
    {
        var declared = ProjectionEdgeShapeCatalog.All
            .Select(s => s.Format()).ToHashSet(StringComparer.Ordinal);

        foreach (var shape in await ObservedShapesAsync())
            Assert.True(declared.Contains(shape.Format()),
                $"de projectie schrijft {shape.Format()}, maar die vorm staat niet in "
                + "ProjectionEdgeShapeCatalog. Een edge tussen andere knooplabels is een andere "
                + "bewering over de graaf — registreer de vorm, en controleer of de ontologie "
                + "haar toestaat (dat is precies de #296-klasse fouten)");
    }

    // ── L2: elke geregistreerde vorm wordt ook echt geschreven ────────────────

    [Fact]
    public async Task L2_ElkeGeregistreerdeVorm_WordtOokEchtGeschreven()
    {
        // Zonder deze richting kan de catalogus stil vollopen met fossielen: een label
        // dat de projectie niet meer schrijft blijft dan als "wat wij bouwen" geboekt
        // staan, en juist dát is het bewijsmateriaal waarop #296 en #304 leunen.
        var observed = (await ObservedShapesAsync()).Select(s => s.Format()).ToHashSet(StringComparer.Ordinal);

        foreach (var shape in ProjectionEdgeShapeCatalog.All)
            Assert.True(observed.Contains(shape.Format()),
                $"ProjectionEdgeShapeCatalog registreert {shape.Format()}, maar geen enkele "
                + "projectie schrijft die vorm nog — verwijder de regel, of herstel het "
                + "statement als er per ongeluk een label sneuvelde");
    }

    // ── L3/L4: schendingen zijn erkend, en erkenningen zijn nog waar ──────────

    [Fact]
    public async Task L3_ElkeSchending_IsEenErkendDefect()
    {
        var defects = ProjectionEdgeShapeCatalog.KnownDefects
            .ToDictionary(d => d.Key, StringComparer.Ordinal);

        foreach (var finding in await FindingsAsync())
            Assert.True(defects.ContainsKey(finding.Key),
                $"de projectie schrijft een edge die de ontologie niet toestaat: {finding.Key}. "
                + "Óf de projectie is fout, óf de declaratie in OntologySchema is dat — beslis "
                + "welke, en leg een bewust uitstel vast als KnownLabelDefect mét issue. "
                + "#270-les: meet eerst op de live graaf wat er werkelijk staat.");
    }

    [Fact]
    public async Task L4_ElkErkendDefect_BestaatNogEcht()
    {
        // De helft die een waiver van een mute-knop onderscheidt. Wordt #296 opgelost —
        // door de range te verruimen, door de projectie aan te passen, of doordat de
        // edge verdwijnt — dan hoort de erkenning mee te verdwijnen. Zonder deze test
        // blijft een gerepareerd defect als open schuld in de documentatie staan, en
        // dekt de waiver bovendien stil een tóekomstige, andere schending af.
        var findings = (await FindingsAsync()).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var defect in ProjectionEdgeShapeCatalog.KnownDefects)
            Assert.True(findings.Contains(defect.Key),
                $"KnownLabelDefect '{defect.Key}' ({defect.Issue}) beschrijft een schending die "
                + "de projectie niet meer begaat. Is het defect opgelost? Verwijder de regel — "
                + "een waiver die zijn eigen onderwerp overleeft, dekt vanaf dat moment iets "
                + "anders af dan waarvoor hij is aangenomen.");
    }

    [Fact]
    public void ErkendDefect_DraagtRedenEnIssue()
    {
        // Zelfde eis als G3 op de naam-catalogus: een uitzondering zonder motivering is
        // niet reviewbaar, en zonder issue-referentie is het een TODO zonder adres.
        foreach (var defect in ProjectionEdgeShapeCatalog.KnownDefects)
        {
            Assert.False(string.IsNullOrWhiteSpace(defect.Reason),
                $"'{defect.Key}' is een erkend defect zonder motivering");
            Assert.Matches(@"^#\d+$", defect.Issue);
        }
    }

    // ── L5 (meta): de vormen zijn rij-onafhankelijk ───────────────────────────

    [Fact]
    public async Task L5_Vormen_ZijnRijOnafhankelijk()
    {
        // Spiegelbeeld van G4a, en om dezelfde reden: L1/L2 draaien tegen een LEGE
        // database. Zou een vorm alleen bij gevulde rijen ontstaan, dan bewaakt de
        // guard hem niet. Labels staan in de query-tekst en niet in de rijen, dus dit
        // hoort per constructie te kloppen — juist daarom is het goedkoop het vast te
        // pinnen in plaats van erop te vertrouwen.
        var leeg = (await ObservedShapesAsync(filled: false)).Select(s => s.Format()).ToHashSet(StringComparer.Ordinal);
        var gevuld = (await ObservedShapesAsync(filled: true)).Select(s => s.Format()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(leeg.Order(StringComparer.Ordinal), gevuld.Order(StringComparer.Ordinal));
    }

    // ── De twee registers wijzen naar elkaar ──────────────────────────────────

    [Fact]
    public void L0_NaamCatalogusEnVormCatalogus_DekkenElkaar()
    {
        // De vorm-catalogus is de verdieping van de naam-catalogus, geen tweede lijst
        // die er los naast leeft. Elke naam heeft minstens één vorm, en elke vorm hoort
        // bij een geclassificeerde naam — anders kan iemand een vorm toevoegen zonder
        // ooit de stand-vraag ("hoort dit in de TBox?") te beantwoorden.
        foreach (var shape in ProjectionEdgeShapeCatalog.All)
            Assert.True(ProjectionEdgeCatalog.ByEdgeName.ContainsKey(shape.EdgeName),
                $"vorm {shape.Format()} hoort bij edge '{shape.EdgeName}', die niet in "
                + "ProjectionEdgeCatalog geclassificeerd staat");

        foreach (var entry in ProjectionEdgeCatalog.All)
            Assert.True(ProjectionEdgeShapeCatalog.For(entry.EdgeName).Any(),
                $"'{entry.EdgeName}' staat wél in ProjectionEdgeCatalog maar heeft geen enkele "
                + "geregistreerde knooplabel-vorm");
    }

    // ── De afdwinging laat vandaag niets vallen (#317) ────────────────────────

    [Fact]
    public async Task RelatesToRijen_InDeFixture_WijzenAlleenNaarDeVijfAfgedwongenSoorten()
    {
        // De WHERE-disjunctie laat een ref naar een knoop búíten de vijf gemeten
        // soorten bewust NIET schrijven — dat ís de afdwinging, geen bug. Deze
        // meting bewaakt het spiegelbeeld: de rijen die de twee RELATES_TO-
        // statements voeden (de #116-relaties en de agent/patient-refs van de
        // qualifier-cache) wijzen in de representatieve fixture allemaal binnen
        // die vijf, dus de rebuild verliest er geen enkele bestaande edge door
        // (de live meting op het issue: twaalf combinaties, alle twaalf binnen de
        // vijf). Komt hier ooit een zesde soort binnen, dan hoort dat een bewuste
        // beslissing te zijn: declaratie verbreden (dat is een versie-bump) én de
        // disjunctie in beide statements mee — niet een ref die stil verdampt.
        await using var db = TestGraphDb.New();
        await ProjectieCorpus.VulAsync(db);

        var refs = db.Relations.AsEnumerable().SelectMany(r => new[] { r.FromRef, r.ToRef })
            .Concat(db.Interactions.AsEnumerable().SelectMany(i => new[] { i.AgentRef, i.PatientRef }))
            .ToList();

        Assert.NotEmpty(refs);
        foreach (var text in refs)
        {
            Assert.True(BrainRef.TryParse(text, out var parsed),
                $"fixture-ref '{text}' parset niet als BrainRef");
            var label = BrainQuery.GraphLabel(parsed.Kind);
            Assert.True(label is "Card" or "Mechanic" or "Concept" or "RuleSection" or "Claim",
                $"fixture-ref '{text}' wijst naar knoopsoort '{label}', buiten de vijf die de "
                + "WHERE-disjunctie toelaat — die edge zou bij de rebuild stil verdwijnen. "
                + "Is dat de bedoeling, verbreed dan declaratie én disjunctie samen (#317).");
        }
    }

    // ── Opname ────────────────────────────────────────────────────────────────

    /// <summary>Élke onderscheiden vorm die de twee projecties schrijven. Duplicaten
    /// vouwen samen: twee statements die dezelfde vorm schrijven (de #116-relaties en
    /// de qualifier-cache schrijven sinds #317 allebei de disjunctieve
    /// <c>(:Card|Claim|Concept|Mechanic|RuleSection)</c>-vorm) zijn één bewering
    /// over de graaf.</summary>
    private static async Task<IReadOnlyList<ProjectionEdgeShape>> ObservedShapesAsync(
        bool filled = false)
    {
        var corpus = await ProjectieCorpus.CorpusAsync(filled);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new List<ProjectionEdgeShape>();
        foreach (var shape in corpus.SelectMany(s => CypherEdgeScanner.WrittenEdgeShapes(s.Cypher)))
            if (seen.Add(shape.Format())) shapes.Add(shape);
        return shapes;
    }

    /// <summary>De bevindingen over wat er ECHT geschreven wordt (niet over wat de
    /// catalogus beweert) — anders zou een fout in de catalogus zichzelf goedkeuren.
    /// L1/L2 houden die twee gelijk.</summary>
    private static async Task<IReadOnlyList<ProjectionLabelFinding>> FindingsAsync()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<ProjectionLabelFinding>();
        foreach (var finding in (await ObservedShapesAsync()).SelectMany(ProjectionLabelCheck.Findings))
            if (seen.Add(finding.Key)) findings.Add(finding);
        return findings;
    }
}

/// <summary>De pure beslissing achter de guard, los van de projectie: wanneer
/// voldoet een knooplabel-vorm aan een gedeclareerde relatie? Gedragstests, want een
/// guard die alleen als geheel getest is vertelt bij een rode regel niet wélke regel
/// hij eigenlijk handhaaft.</summary>
public class ProjectionLabelCheckTests
{
    private static IReadOnlyList<ProjectionLabelFinding> Check(
        string edge, string[] from, string[] to) =>
        ProjectionLabelCheck.Findings(new ProjectionEdgeShape(edge, from, to));

    [Fact]
    public void ConformeVorm_LevertGeenBevinding() =>
        // HAS_MECHANIC: Card → Mechanic, exact zoals gedeclareerd.
        Assert.Empty(Check("HAS_MECHANIC", ["Card"], ["Mechanic"]));

    [Fact]
    public void SubklasseVoldoetAanDeSuperklasse() =>
        // Domain is [Card]; een Unit IS een Card (OntologySchema.IsA). Zou deze toets
        // op naam-gelijkheid werken i.p.v. op de hiërarchie, dan gaf de dag waarop de
        // projectie :Unit gaat schrijven vals alarm.
        Assert.Empty(Check("HAS_MECHANIC", ["Unit"], ["Mechanic"]));

    [Fact]
    public void MultiLabel_VoldoetAlsEenLabelBinnenDeRangeValt()
    {
        // Een knoop DRAAGT al zijn labels tegelijk (:Interaction:Concept), dus één
        // passend label is genoeg. "Alle labels moeten passen" zou de reïficatie-tak
        // meteen rood zetten zonder dat er iets mis is.
        Assert.Empty(Check("GOVERNED_BY", ["Interaction", "Concept"], ["RuleSection"]));
        Assert.Empty(Check("GOVERNED_BY", ["Concept", "Interaction"], ["RuleSection"]));
    }

    [Fact]
    public void BuitenDeRange_IsEenSchending()
    {
        // De #304-meting als literal: een Keyword-filler op HAS_ROLE zou precies de
        // bewering zijn die docs/miner/validator jarenlang deden en die de live
        // graaf weerlegt (492 × Card, 274 × Mechanic, nul × Keyword). Zet iemand de
        // gedeclareerde range terug op Card/Keyword, dan verschuift dit oordeel —
        // en gaat de Interaction→Mechanic-conformiteitstest hieronder rood.
        var findings = Check("HAS_ROLE", ["Interaction"], ["Keyword"]);

        var finding = Assert.Single(findings);
        Assert.Equal(EdgeEndpoint.To, finding.Side);
        Assert.Equal(ProjectionLabelVerdict.Violates, finding.Verdict);
    }

    [Fact]
    public void Supersedes_ErratumNaarCard_IsSindsNr296Conform()
    {
        // Tot #296 was dit DE bekende schending (range NormativeSource); sindsdien
        // declareert het register de gemeten vorm. Mutatie-pin: zet de range terug
        // op NormativeSource en hier verschijnt weer een Violates-bevinding — en
        // L3 gaat rood omdat de bijbehorende waiver is opgeruimd.
        Assert.Empty(Check("SUPERSEDES", ["Erratum"], ["Card"]));
    }

    [Fact]
    public void HasRole_GemetenFillers_ZijnConform()
    {
        // De twee label-gebonden HAS_ROLE-statements (#304).
        Assert.Empty(Check("HAS_ROLE", ["Interaction"], ["Card"]));
        Assert.Empty(Check("HAS_ROLE", ["Interaction"], ["Mechanic"]));
    }

    [Fact]
    public void HasTag_CardNaarTag_IsSindsNr304Conform() =>
        Assert.Empty(Check("HAS_TAG", ["Card"], ["Tag"]));

    [Fact]
    public void OnbekendLabel_VoldoetNooit()
    {
        // "Sticker" is geen EntityType. Het register is de ÉNE bron, dus een label
        // dat daar niet in staat kan een gedeclareerde range niet vervullen — anders
        // zou elke typefout in een label stil als "vast wel een subklasse" doorglippen.
        // (Tot #304 stond hier "Tag"; dat is inmiddels een echte klasse en dekt dit
        // pad dus niet meer.)
        var finding = Assert.Single(Check("HAS_DOMAIN", ["Card"], ["Sticker"]));
        Assert.Equal(ProjectionLabelVerdict.Violates, finding.Verdict);
    }

    [Fact]
    public void BekendLabelBuitenDeRange_IsOokEenSchending()
    {
        // Tag is sinds #304 een geregistreerde klasse, maar geen Domain — een bekend
        // label buiten de gedeclareerde range blijft een schending.
        var finding = Assert.Single(Check("HAS_DOMAIN", ["Card"], ["Tag"]));
        Assert.Equal(ProjectionLabelVerdict.Violates, finding.Verdict);
    }

    [Fact]
    public void LabelLozeKant_IsOnbepaald_GeenSchending()
    {
        // Een label-loze ref-match legt niets op. Dat is geen fout maar een
        // niet-afdwingbare declaratie: rood zou hier onterecht zijn, stil doorlaten
        // net zo goed. (Tot #317 was dit de levende RELATES_TO-vorm, gedekt door
        // twee waivers; sindsdien dwingt de projectie de vijf soorten af met een
        // WHERE-disjunctie en is dit het oordeel dat L3 rood maakt zodra iemand
        // die disjunctie weer sloopt — er is geen waiver meer die het dekt.)
        var findings = Check("RELATES_TO", [], []);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(ProjectionLabelVerdict.Unenforceable, f.Verdict));
        Assert.Equal([EdgeEndpoint.From, EdgeEndpoint.To], findings.Select(f => f.Side));
    }

    // ── Disjunctieve kanten (#317) ────────────────────────────────────────────

    [Fact]
    public void RelatesTo_DeGemetenVijfDisjunctief_IsConform()
    {
        // De #317-vorm als literals: beide kanten disjunctief op de vijf gemeten
        // soorten, exact wat de twee WHERE-disjuncties afdwingen. Mutatie-pin (c):
        // haal één van de vijf (behalve Mechanic — die dekt Concept al via
        // Mechanic ⊑ Concept) uit de RELATES_TO-declaratie in OntologySchema en
        // deze toets gaat rood, samen met L3 op de echte projectie.
        var shape = new ProjectionEdgeShape("RELATES_TO",
            ["Card", "Mechanic", "Concept", "RuleSection", "Claim"],
            ["Card", "Mechanic", "Concept", "RuleSection", "Claim"])
        { FromDisjunctive = true, ToDisjunctive = true };

        Assert.Empty(ProjectionLabelCheck.Findings(shape));
    }

    [Fact]
    public void DisjunctieMetEenSoortBuitenDeDeclaratie_IsEenSchending()
    {
        // Disjunctief moet ÉLKE soort binnen de declaratie vallen: de knoop is er
        // maar één, dus één buiten-declaratie-soort betekent dat het statement een
        // niet-conforme edge kan schrijven. Source staat niet in de
        // RELATES_TO-declaratie → Violates.
        var shape = new ProjectionEdgeShape("RELATES_TO", ["Card", "Source"], ["Card"])
        { FromDisjunctive = true };

        var finding = Assert.Single(ProjectionLabelCheck.Findings(shape));
        Assert.Equal(EdgeEndpoint.From, finding.Side);
        Assert.Equal(ProjectionLabelVerdict.Violates, finding.Verdict);
        Assert.True(finding.Disjunctive);
        // De sleutel draagt de disjunctie (":Card|Source", niet ":Card:Source"):
        // een waiver voor een multi-label vorm mag nooit stil een disjunctieve
        // vorm dekken, of andersom.
        Assert.Equal("RELATES_TO|From|Violates|:Card|Source", finding.Key);
    }

    [Fact]
    public void ZelfdeLabels_ConjunctiefConform_DisjunctiefEenSchending()
    {
        // Het hart van het onderscheid. Conjunctief DRAAGT de knoop alle labels:
        // één passende (Card) volstaat, ook al is Source er geen. Disjunctief is
        // de knoop er maar één van — en dan is Source precies het gat.
        Assert.Empty(ProjectionLabelCheck.Findings(
            new ProjectionEdgeShape("RELATES_TO", ["Card", "Source"], ["Card"])));
        Assert.Single(ProjectionLabelCheck.Findings(
            new ProjectionEdgeShape("RELATES_TO", ["Card", "Source"], ["Card"])
            { FromDisjunctive = true }));
    }

    [Fact]
    public void DisjunctieveSoort_MagOokEenSubklasseZijn() =>
        // Subklasse-polymorfie geldt per disjunct: Unit ⊑ Card, dus een disjunctie
        // die Unit toelaat valt binnen een declaratie die Card noemt.
        Assert.Empty(ProjectionLabelCheck.Findings(
            new ProjectionEdgeShape("RELATES_TO", ["Unit", "Concept"], ["Card"])
            { FromDisjunctive = true }));

    [Fact]
    public void GekwalificeerdeRelatie_IsAltijdEenSchending_OokLabelLoos()
    {
        // COUNTERS/MODIFIES/GRANTS/REQUIRES dragen RequiresReification: ze zijn
        // verboden als KALE edge en horen via een Interaction te lopen. Dan doet het
        // er niet toe welke labels eraan hangen — ook niet dat het er geen zijn.
        // Stond de label-loos-tak vóór deze, dan kwam zo'n verboden edge terug als het
        // mildere "niet te garanderen"; de sterkste uitspraak hoort te winnen.
        Assert.All(Check("COUNTERS", ["Card"], ["Card"]),
            f => Assert.Equal(ProjectionLabelVerdict.Violates, f.Verdict));
        Assert.All(Check("REQUIRES", [], []),
            f => Assert.Equal(ProjectionLabelVerdict.Violates, f.Verdict));
    }

    [Fact]
    public void NietGedeclareerdeEdge_LevertNiets() =>
        // De Provenance-tak (SUPPORTED_BY, AFFECTS, …) staat bewust buiten de TBox.
        // Er is dan niets om tegen te toetsen; de vorm wordt wél geregistreerd,
        // zodat de toets vanzelf gaat lopen zodra iemand zo'n relatie tóch
        // declareert. (Tot #304 stond hier HAS_TAG; die is inmiddels gedeclareerd
        // en dekt dit pad dus niet meer.)
        Assert.Empty(Check("SUPPORTED_BY", ["Claim"], ["Source"]));
}
