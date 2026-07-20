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
    private const int GraphSyncStatements = 41;
    private const int BreinProjectieStatements = 12;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G4b_AantalStatements_LigtVastPerProjectie(bool filled)
    {
        await using var db = TestGraphDb.New();
        if (filled) await VulAsync(db);

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
        await VulAsync(db);
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

    private static async Task<IReadOnlyList<RecordedStatement>> CorpusAsync(bool filled)
    {
        await using var db = TestGraphDb.New();
        if (filled) await VulAsync(db);

        var driver = new RecordingDriver();
        await new GraphSyncService(db, driver).SyncAsync();

        // BreinProjectionService slikt elke Neo4j-fout als "graph niet beschikbaar"
        // (nette degradatie, #227). Tegen de opnemende driver hoort dat pad NOOIT te
        // lopen — anders zou een half afgebroken projectie stil een korter corpus geven.
        var brein = await new BreinProjectionService(db, driver).ProjectAsync();
        Assert.True(brein.GraphAvailable,
            "de brein-projectie degradeerde tegen de opnemende driver — het corpus is dan "
            + "afgekapt en de guard zou stil dekking verliezen");

        return driver.Statements;
    }

    private static async Task<HashSet<string>> WrittenEdgesAsync(bool filled) =>
        [.. (await CorpusAsync(filled)).SelectMany(s => CypherEdgeScanner.WrittenEdges(s.Cypher))];

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>Een representatieve stand: elke rij-verzameling die de twee projecties
    /// lezen is niet-leeg, inclusief de edge-lijsten (PART_OF, EXPLAINS, alle vier
    /// ABOUT-varianten voor zowel Claim als Ruling, SUPPORTED_BY, SUPERSEDES, beide
    /// AFFECTS-varianten, HAS_ROLE, REQUIRES_CONDITION, GOVERNED_BY, MERGED_INTO,
    /// HAS_PREDICATE, PRECEDES). Zie <see cref="Fixture_IsEchtGevuld"/>.</summary>
    private static async Task VulAsync(RbRulesDbContext db)
    {
        const string sourceId = "core-rules-pdf";
        const string sourceUrl = "https://playriftbound.com/en-us/article/core-rules";

        db.Sources.Add(new Source
        {
            Id = sourceId, Name = "Core Rules", Url = sourceUrl, Type = "official",
            TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "weekly",
        });

        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001", Name = "Shieldbearer", Type = "Unit", Rarity = "Common",
            Domains = ["Fury"], Tags = ["Noxus"], Mechanics = ["Tank"],
            Energy = 2, Might = 3, SetId = "ogn", SetLabel = "Origins",
        });

        db.Documents.Add(new Document
        {
            Id = 1, SourceId = sourceId, Content = "466 Combat. 466.2 Showdown.",
            ContentHash = "hash-1",
        });
        // Ouder + kind binnen dezelfde bron ⇒ PART_OF.
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = sourceId, SectionCode = "466", ChunkIndex = 0,
            Text = "Combat.",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = sourceId, SectionCode = "466.2", ChunkIndex = 1,
            Text = "Showdown.",
        });

        // SectionRefs verwijst naar een bestaande §-code ⇒ EXPLAINS.
        db.KnowledgeDocs.Add(new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat", Body = "How combat works.",
            SectionRefs = "466", Status = "approved",
        });

        // Vier claims: één per ABOUT-doelsoort (card/mechanic/section/concept).
        foreach (var (topicType, topicRef) in new[]
                 {
                     ("card", "Shieldbearer"), ("mechanic", "Tank"),
                     ("section", "466"), ("concept", "combat"),
                 })
            db.Claims.Add(new Claim
            {
                TopicType = topicType, TopicRef = topicRef,
                Statement = $"Community-lezing over {topicRef}.",
                Status = "accepted", Corroboration = 2, TrustScore = 0.6,
            });
        await db.SaveChangesAsync();

        // SUPPORTED_BY (claim → source).
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = db.Claims.First().Id, SourceId = sourceId, Url = sourceUrl,
            QuoteExcerpt = "kort citaat",
        });

        // Vier geverifieerde rulings: opnieuw één per ABOUT-doelsoort. De eerste draagt
        // SourceRef = de bron-URL ⇒ SUPPORTED_BY (ruling → source).
        var scopes = new[]
        {
            ("card", "Shieldbearer"), ("mechanic", "Tank"),
            ("rule_section", "466"), ("concept", "combat"),
        };
        var eerste = true;
        foreach (var (scope, reference) in scopes)
        {
            db.Corrections.Add(new Correction
            {
                Scope = scope, Ref = reference, Text = $"Officiële ruling over {reference}.",
                Question = "Hoe werkt dit?", Provenance = "official",
                SourceRef = eerste ? sourceUrl : null,
                Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            });
            eerste = false;
        }

        // SUPERSEDES (erratum → kaart).
        db.Errata.Add(new Erratum
        {
            CardName = "Shieldbearer", CardRiftboundId = "ogn-001",
            NewText = "Tank 3.", SourceUrl = sourceUrl,
        });

        // Twee changes: errata ⇒ AFFECTS-kaart, core-rule ⇒ AFFECTS-sectie.
        db.Changes.Add(new Change
        {
            SourceId = sourceId, ChangeType = "errata", Severity = "high",
            Summary = "Shieldbearer krijgt Tank 3.",
        });
        db.Changes.Add(new Change
        {
            SourceId = sourceId, ChangeType = "core-rule", Severity = "medium",
            Summary = "Sectie 466.2 herschreven.",
        });

        // Provenance-ruggengraat: MiningRun + Assertion (WAS_GENERATED_BY/DERIVED_FROM).
        db.MiningRuns.Add(new MiningRun
        {
            Id = "run-1", Kind = "relation", LlmModel = "claude-opus-4-8",
            PromptVersion = "reln-v7#a1b2", CompletedAt = DateTimeOffset.UtcNow,
        });
        db.Assertions.Add(new Assertion
        {
            Id = "asrt-1", Subject = "relation:1", FactKind = "relation",
            MiningRunId = "run-1", DerivedFromRef = $"source:{sourceId}",
            Model = "claude-opus-4-8", Verifier = "official-check", Verdict = "confirmed",
        });

        // Dynamische relatie door de reviewpoort ⇒ RELATES_TO.
        db.RelationKinds.Add(new RelationKind { Kind = "counters", Status = "accepted" });
        db.Relations.Add(new Relation
        {
            FromRef = "mechanic:Tank", ToRef = $"section:{sourceId}/466", Kind = "counters",
            Explanation = "Tank verwijst naar de combat-sectie.", Provenance = "concept:combat",
            Trust = 0.7, Status = "accepted",
        });

        // Gereïficeerde interactie met conditie ⇒ HAS_ROLE, REQUIRES_CONDITION,
        // GOVERNED_BY en de RELATES_TO-qualifier-cache.
        db.Interactions.Add(new Interaction
        {
            AgentRef = "card:ogn-001", PatientRef = "mechanic:Tank",
            Kind = InteractionKinds.Counters, GovernedByRef = $"section:{sourceId}/466",
            Status = InteractionStatus.Promoted, StatusReason = "poort-ok",
            CreatedByRunId = "run-1",
            Conditions =
            [
                new InteractionCondition
                {
                    InteractionId = 0, OnKind = InteractionConditionKinds.Window,
                    SubjectRole = InteractionRoles.Agent, Value = "Showdown",
                },
            ],
        });

        // Brein-laag: twee entiteiten (één tombstone ⇒ MERGED_INTO), een predicaat
        // (⇒ HAS_PREDICATE) en twee ontologie-versies (⇒ PRECEDES).
        var canoniek = new CanonicalEntity
        {
            Kind = "mechanic", CanonicalLabel = "Tank",
            Status = CanonicalEntityStatus.Canonical, CreatedByRunId = "run-1",
            Definition = "Absorbs damage.",
        };
        db.CanonicalEntities.Add(canoniek);
        await db.SaveChangesAsync();

        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = "mechanic", CanonicalLabel = "Tanking", AltLabels = ["tank"],
            Status = CanonicalEntityStatus.Merged, MergedIntoId = canoniek.Id,
            CreatedByRunId = "run-1",
        });
        db.MechanicPredicates.Add(new MechanicPredicateAssertion
        {
            SubjectEntityId = canoniek.Id, Predicate = "prevents", ObjectToken = "damage",
            Status = MechanicPredicateStatus.Reviewed, CreatedByRunId = "run-1",
        });
        db.OntologyVersions.Add(new OntologyVersionRecord
        {
            Version = "1.0.0", Fingerprint = "fp-1", BumpKind = "minor",
            Notes = "eerste vastlegging", RunId = "run-1",
        });
        db.OntologyVersions.Add(new OntologyVersionRecord
        {
            Version = "1.1.0", Fingerprint = "fp-2", BumpKind = "minor",
            Notes = "set OGN", RunId = "run-1",
        });

        await db.SaveChangesAsync();
    }
}
