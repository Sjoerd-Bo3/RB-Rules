using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De projectie↔ontologie-guard (#289, PR 1) — de richting die #274 open
/// liet. <see cref="OntologyProjectionAlignmentTests"/> bewaakt "de reasoner gebruikt
/// geen edge buiten het register"; deze tests bewaken het omgekeerde: "de projectie
/// schrijft niets waarover niemand een beslissing nam". Precies langs die weg groeiden
/// HAS_MECHANIC en HAS_KEYWORD jarenlang uit elkaar.
///
/// HOE. Beide volledige rebuild-projecties (<see cref="GraphSyncService"/> en
/// <see cref="BreinProjectionService"/>) draaien tegen een <see cref="RecordingDriver"/>;
/// uit de OPGENOMEN Cypher haalt <see cref="CypherEdgeScanner"/> elke geschreven
/// edge-naam, die tegen <see cref="ProjectionEdgeCatalog"/> wordt gehouden. De guard
/// leest dus UITGEVOERDE Cypher, geen broncode: een alias hernoemen, de query anders
/// formatteren of een statement naar een helper verplaatsen laat hem koud. Alleen een
/// andere edge-NAAM of een verdwenen schrijf-clausule maakt hem rood.
///
/// WAT G4 WEL EN NIET BEWIJST (#289-review, F1). G1/G2 zijn alleen volledig als het
/// opgenomen corpus gelijk is aan wat in productie draait. G4 dekt daarvan twee
/// bedreigingen af — rij-afhankelijkheid (G4a) en een statement dat stopt met vuren
/// (G4b) — en pint met G4c de afhankelijkheids-oppervlakte vast waarlangs
/// configuratie binnen zou komen. Wat een runtime-probe per constructie NIET kan
/// zien, is een statement dat onder de testconfiguratie NOOIT vuurt: een tak die je
/// niet neemt, neem je niet waar. Die restrisico staat expliciet in ARCHITECTURE §6.3
/// en is de reden dat G4 hieronder niet meer als sluitend bewijs voor G1's
/// volledigheid wordt opgevoerd — de eerdere PR-body deed dat wél, en dat was een
/// over-claim.</summary>
public class ProjectionOntologyGuardTests
{
    // ── G1: elke geschreven edge staat in de catalogus ────────────────────────

    [Fact]
    public async Task G1_ElkeGeschrevenEdge_IsGeclassificeerd()
    {
        // Een nieuwe edge toevoegen aan de projectie zonder er een beslissing over te
        // nemen (hoort hij in de TBox, of valt hij daar bewust buiten?) is rood.
        var written = await WrittenEdgesAsync(filled: false);

        foreach (var edge in written)
            Assert.True(ProjectionEdgeCatalog.ByEdgeName.ContainsKey(edge),
                $"de projectie schrijft edge '{edge}' die niet in ProjectionEdgeCatalog staat — "
                + "classificeer hem (InSchema / DomeinNogNietGedeclareerd / Provenance / "
                + "Infrastructuur) vóór hij de graph in gaat");
    }

    // ── G2: elke catalogus-entry wordt ook echt geschreven ────────────────────

    [Fact]
    public async Task G2_ElkeCatalogusEntry_WordtOokEchtGeschreven()
    {
        // Dit is het antwoord op "een lijst die niemand onderhoudt": een entry kan niet
        // blijven staan nadat de projectie hem liet vallen (of hernoemde). Zonder deze
        // richting zou de catalogus stil vollopen met fossielen en zou een hernoeming
        // alleen G1 raken — nu betrapt hij beide kanten van dezelfde wijziging.
        var written = await WrittenEdgesAsync(filled: false);

        foreach (var entry in ProjectionEdgeCatalog.All)
            Assert.True(written.Contains(entry.EdgeName),
                $"ProjectionEdgeCatalog classificeert '{entry.EdgeName}', maar geen enkele "
                + "projectie schrijft die edge nog — verwijder de entry, of herstel de "
                + "schrijf-clausule als hij per ongeluk sneuvelde");
    }

    // ── G3: de stand liegt niet over het register ─────────────────────────────

    public static TheoryData<string> CatalogusNamen()
    {
        var data = new TheoryData<string>();
        foreach (var e in ProjectionEdgeCatalog.All) data.Add(e.EdgeName);
        return data;
    }

    [Theory]
    [MemberData(nameof(CatalogusNamen))]
    public void G3_StandLiegtNietOverHetRegister(string edgeName)
    {
        var entry = ProjectionEdgeCatalog.ByEdgeName[edgeName];
        var registered = OntologySchema.RelationByEdgeName(entry.EdgeName);

        if (entry.MustResolveInSchema)
        {
            Assert.True(registered is not null,
                $"'{entry.EdgeName}' staat als InSchema in de catalogus maar resolvet niet "
                + "in OntologySchema — óf registreer de relatie, óf kies een andere stand");
            Assert.Equal(entry.EdgeName, registered!.EdgeName);
        }
        else
        {
            // De drie buiten-standen BEWEREN dat de naam niet in het register staat.
            // Wordt hij later wél gedeclareerd, dan moet de stand mee — anders zou een
            // relatie tegelijk "bewust buiten de TBox" én gedeclareerd zijn.
            Assert.True(registered is null,
                $"'{entry.EdgeName}' staat als {entry.Stance} in de catalogus, maar is "
                + "inmiddels een geregistreerde relatie — zet de stand op InSchema");
        }
    }

    [Theory]
    [MemberData(nameof(CatalogusNamen))]
    public void G3_ErkendeSchuld_DraagtRedenEnIssue(string edgeName)
    {
        // Een classificatie zonder motivering is niet reviewbaar; erkende domeinschuld
        // zonder issue-referentie is een TODO zonder adres.
        var entry = ProjectionEdgeCatalog.ByEdgeName[edgeName];

        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        if (entry.Stance == ProjectionEdgeStance.DomeinNogNietGedeclareerd)
            Assert.False(string.IsNullOrWhiteSpace(entry.Issue),
                $"'{entry.EdgeName}' is erkende domeinschuld en hoort naar een issue te wijzen");

        // Een issue-referentie is bij ELKE stand toegestaan (#289-review, F7): een
        // geregistreerde relatie kan een open defect hebben (#296) en dat mag de
        // catalogus niet wegpoetsen. Wel moet het een echte referentie zijn.
        if (entry.Issue is not null)
            Assert.Matches(@"^#\d+$", entry.Issue);
    }

    // ── G4 (meta): het corpus is representatief ───────────────────────────────

    [Fact]
    public async Task G4a_Corpus_IsRijOnafhankelijk()
    {
        // Vergelijking als VERZAMELING statement-teksten (niet als volgorde): dat een
        // projectie zijn stappen herschikt is geen drift, dat een stap alleen nog bij
        // gevulde rijen vuurt wél.
        var leeg = (await CorpusAsync(filled: false)).Select(s => s.Cypher).ToList();
        var gevuld = (await CorpusAsync(filled: true)).Select(s => s.Cypher).ToList();

        var alleenGevuld = gevuld.Except(leeg).ToList();
        var alleenLeeg = leeg.Except(gevuld).ToList();

        Assert.True(alleenGevuld.Count == 0,
            "een statement vuurt alleen bij gevulde rijen — dan mist de probe tegen een lege "
            + "database dekking en kan G1 stil onder-rapporteren:\n"
            + string.Join("\n---\n", alleenGevuld));
        Assert.True(alleenLeeg.Count == 0,
            "een statement vuurt alleen bij lege rijen:\n" + string.Join("\n---\n", alleenLeeg));
    }

    // Vastgepind aantal statements per projectie. Deze getallen mogen ALLEEN
    // veranderen als er bewust een statement bij komt of af gaat — dan is het een
    // zichtbare, reviewbare regel in de diff. Zonder deze pin blijft een statement dat
    // achter een conditie verdwijnt (env-vlag, ManagedSettings-toggle, #254) stil weg
    // uit het corpus: G4a ziet dat niet, want de conditie is in BEIDE DB-standen
    // hetzelfde (#289-review, F1).
    // 41 → 42 in #304: het label-loze HAS_ROLE-statement is gesplitst in twee
    // label-gebonden statements (Card- en Mechanic-fillers), zodat de projectie de
    // gedeclareerde range zelf afdwingt.
    private const int GraphSyncStatements = 42;
    private const int BreinProjectieStatements = 12;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G4b_AantalStatements_LigtVastPerProjectie(bool filled)
    {
        await using var db = TestGraphDb.New();
        if (filled) await ProjectieCorpus.VulAsync(db);

        var graphSync = new RecordingDriver();
        await new GraphSyncService(db, graphSync).SyncAsync();
        Assert.True(GraphSyncStatements == graphSync.Statements.Count,
            $"GraphSyncService voerde {graphSync.Statements.Count} statements uit i.p.v. "
            + $"{GraphSyncStatements}. Ging er één AF, kijk dan of hij achter een conditie is "
            + "beland (env-vlag, ManagedSettings) — dan verliest de guard stil dekking. Kwam er "
            + "één BIJ, pas dan bewust dit getal aan.");

        var brein = new RecordingDriver();
        await new BreinProjectionService(db, brein).ProjectAsync();
        Assert.True(BreinProjectieStatements == brein.Statements.Count,
            $"BreinProjectionService voerde {brein.Statements.Count} statements uit i.p.v. "
            + $"{BreinProjectieStatements}.");
    }

    [Fact]
    public void G4c_ProjectiesHebbenGeenConfiguratieAfhankelijkheid()
    {
        // De laatste steun onder G1: zolang beide projecties alléén (DbContext, IDriver)
        // krijgen, kan hun corpus niets anders zijn dan data-afhankelijk — en dat dekt
        // G4a af. CLAUDE.md schrijft voor dat een schakelaar via ManagedSettings loopt
        // (#254), dus de waarschijnlijkste manier waarop deze aanname breekt is een
        // extra constructor-parameter. Precies dán hoort deze test rood te gaan, met de
        // opdracht G4 uit te breiden in plaats van de aanname stil te laten verlopen.
        foreach (var type in (Type[])[typeof(GraphSyncService), typeof(BreinProjectionService)])
        {
            var ctor = Assert.Single(type.GetConstructors());
            Assert.Equal(
                ["RbRulesDbContext", "IDriver"],
                ctor.GetParameters().Select(p => p.ParameterType.Name));
        }
    }

    [Fact]
    public async Task G5_ElkeCypherInDeBron_IsOokUitgevoerd()
    {
        // Het gat dat G4 per constructie NIET kan dichten (#289-review, F1): een
        // statement dat onder de testconfiguratie nooit vuurt — achter een env-vlag of
        // een ManagedSettings-toggle (#254) — staat niet in het corpus en is dus voor
        // G1 onzichtbaar. Bewezen met een gloednieuwe, ongeclassificeerde VERIFIED_BY
        // achter zo'n vlag: alles bleef groen. De statement-teller (G4b) vangt dat óók
        // niet, want een tak die niet vuurt verlaagt de telling niet.
        //
        // Deze toets kijkt daarom één keer wél naar de bron, en dan naar precies één
        // ding: elke edge-naam die LETTERLIJK in de twee projectie-bestanden staat moet
        // ook echt uitgevoerd zijn. Dat is geen broncode-scanner die opmaak bewaakt —
        // aliassen, witruimte en helper-verhuizingen raken hem niet (bewezen met N1/N2/N3).
        var uitgevoerd = await WrittenEdgesAsync(filled: false);

        foreach (var (bestand, bron) in ProjectieBronnen())
            foreach (var edge in CypherEdgeScanner.WrittenEdgesInSource(bron).Distinct())
                Assert.True(uitgevoerd.Contains(edge),
                    $"{bestand} bevat letterlijk een MERGE/CREATE van '{edge}', maar dat "
                    + "statement is tijdens de projectie NIET uitgevoerd. Staat het achter een "
                    + "conditie (env-vlag, ManagedSettings-toggle)? Dan is de edge onzichtbaar "
                    + "voor G1 en bewaakt de guard hem niet.");
    }

    /// <summary>De broncode van de twee geprobede projecties. Opzoeken vanaf de
    /// test-assembly omhoog: faalt dat, dan faalt de TEST — een guard die stil
    /// overslaat omdat hij zijn eigen invoer niet vindt, is geen guard.</summary>
    private static IEnumerable<(string Bestand, string Bron)> ProjectieBronnen()
    {
        foreach (var naam in (string[])["GraphSyncService.cs", "BreinProjectionService.cs"])
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? gevonden = null;
            while (dir is not null && gevonden is null)
            {
                var kandidaat = Path.Combine(dir.FullName, "RbRules.Infrastructure", naam);
                if (File.Exists(kandidaat)) gevonden = kandidaat;
                dir = dir.Parent;
            }
            Assert.True(gevonden is not null, $"broncode van {naam} niet gevonden vanaf {AppContext.BaseDirectory}");
            yield return (naam, File.ReadAllText(gevonden!));
        }
    }

    [Fact]
    public async Task Fixture_IsEchtGevuld()
    {
        // De bewaker-van-de-bewaker. G4a vergelijkt een lege met een GEVULDE stand; is
        // die tweede stand in werkelijkheid half leeg, dan wordt G4a stil krachteloos
        // zonder dat iets rood gaat (#289-review, F6). Vroeger asserteerde deze test
        // alleen resultaat-tellers — die zeggen niets over de EDGE-lijsten
        // (supportedByPairs, explainsPairs, about*, RoleEdges, …). Nu wordt per
        // uitgevoerd statement getoetst: schrijft het een edge, dan moet het ook rijen
        // hebben gekregen. Dat is de eigenschap waar G4a op leunt, direct gemeten.
        var statements = await CorpusAsync(filled: true);

        var zonderRijen = statements
            .Where(s => CypherEdgeScanner.WrittenEdges(s.Cypher).Count > 0)
            .Where(s => s.RowList is null || s.RowList.Count == 0)
            .ToList();

        Assert.True(zonderRijen.Count == 0,
            "de 'gevulde' fixture geeft deze edge-schrijvende statements géén rijen, dus G4a "
            + "toetst er niets meer mee — vul VulAsync aan:\n"
            + string.Join("\n---\n", zonderRijen.Select(s => s.Cypher)));

        // En de teller-kant: elke rij-verzameling die de twee projecties opleveren is
        // niet-leeg, zodat ook de knoop-statements echt werk doen.
        await using var db = TestGraphDb.New();
        await ProjectieCorpus.VulAsync(db);
        var driver = new RecordingDriver();
        var sync = await new GraphSyncService(db, driver).SyncAsync();
        var brein = await new BreinProjectionService(db, driver).ProjectAsync();

        Assert.All(new[]
        {
            sync.Cards, sync.Domains, sync.Tags, sync.Mechanics, sync.Sections,
            sync.Concepts, sync.Claims, sync.Sources, sync.Errata, sync.Changes,
            sync.Relations, sync.Rulings, sync.MiningRuns, sync.Assertions,
            sync.Interactions, sync.Conditions,
        }, n => Assert.True(n > 0));

        Assert.True(brein.GraphAvailable);
        Assert.All(new[]
        {
            brein.CanonicalEntities, brein.MergedInto, brein.Predicates,
            brein.HasPredicate, brein.OntologyVersions, brein.Precedes,
        }, n => Assert.True(n > 0));
    }

    // ── Corpus-opname ─────────────────────────────────────────────────────────

    // Gedeeld met ProjectionLabelGuardTests (#289 PR 2): één opname, één fixture.
    // Twee kopieën zouden uiteenlopen, en dan zou Fixture_IsEchtGevuld hierboven —
    // de bewaker-van-de-bewaker — nog maar de helft bewaken.
    private static Task<IReadOnlyList<RecordedStatement>> CorpusAsync(bool filled) =>
        ProjectieCorpus.CorpusAsync(filled);

    private static async Task<HashSet<string>> WrittenEdgesAsync(bool filled) =>
        [.. (await CorpusAsync(filled)).SelectMany(s => CypherEdgeScanner.WrittenEdges(s.Cypher))];
}
