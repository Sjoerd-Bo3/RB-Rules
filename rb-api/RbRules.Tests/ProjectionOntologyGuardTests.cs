using System.Text.RegularExpressions;
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
/// uit de OPGENOMEN Cypher wordt elke geschreven edge-naam geëxtraheerd en tegen
/// <see cref="ProjectionEdgeCatalog"/> gehouden. De guard leest dus UITGEVOERDE Cypher,
/// geen broncode: een alias hernoemen, de query anders formatteren of een statement naar
/// een helper verplaatsen laat hem koud. Alleen een andere edge-NAAM of een verdwenen
/// schrijf-clausule maakt hem rood — de vier PR's die vandaag op een broncode-scanner
/// stukliepen zouden hier groen zijn gebleven.
///
/// DE AANNAME EN HAAR BEWAKING. G1/G2 zijn alleen volledig als het opgenomen corpus
/// onafhankelijk is van de data: élk statement in beide projecties vuurt
/// onvoorwaardelijk, óók met lege <c>$rows</c> (RunPairsAsync/RunRowsAsync/RunEdgesAsync
/// en elke inline RunAsync). Een probe tegen een LEGE database levert daarom het complete
/// Cypher-corpus, en de guard kan niet stil onder-rapporteren doordat een fixture
/// toevallig geen claim of ruling bevatte. <see cref="Corpus_IsRijOnafhankelijk"/> (G4)
/// pint die aanname vast: wikkelt iemand ooit een statement in <c>if (rows.Count > 0)</c>,
/// dan gaat de guard rood in plaats van dekking te verliezen.</summary>
public class ProjectionOntologyGuardTests
{
    /// <summary>Elke SCHRIJF-clausule: <c>MERGE|CREATE (…)-[alias:EDGE_NAAM</c>. Vangt
    /// zowel <c>MERGE (c)-[:FROM_SET]->(s)</c> als
    /// <c>MERGE (ix)-[r:HAS_ROLE {role: row.role}]->(f)</c>, en laat opruim-clausules
    /// (die met MATCH beginnen: <c>MATCH ()-[r:RELATES_TO]->() DELETE r</c>) er bewust
    /// buiten — een edge verwijderen is geen bewering over de ontologie.
    ///
    /// Cypher is WITRUIMTE-TOLERANT en de guard hoort dat ook te zijn: <c>-[:X]-&gt;</c>
    /// en <c>- [ :X ] -&gt;</c> zijn dezelfde query. Een strakkere variant (zonder
    /// <c>\s*</c> rond het streepje) leest een herformattering als een verdwenen edge
    /// en gaat dan rood op een wijziging die niets betekent — precies het
    /// vals-alarm-gedrag dat deze guard moet vermijden; betrapt tijdens de
    /// mutatie-verificatie. <c>&lt;?</c> dekt bovendien een inkomende richting.</summary>
    private static readonly Regex WrittenEdge = new(
        @"(?:MERGE|CREATE)\s*\([^)]*\)\s*<?-\s*\[\s*[a-zA-Z_]*\s*:\s*([A-Z_]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        else
            Assert.Null(entry.Issue);
    }

    // ── G4 (meta): het corpus is rij-onafhankelijk ────────────────────────────

    [Fact]
    public async Task G4_Corpus_IsRijOnafhankelijk() => await Corpus_IsRijOnafhankelijk();

    private static async Task Corpus_IsRijOnafhankelijk()
    {
        // De aanname waarop G1 rust, als test. Vergelijking als VERZAMELING statement-
        // teksten (niet als volgorde): dat een projectie zijn stappen herschikt is geen
        // drift, dat een stap alleen nog bij gevulde rijen vuurt wél.
        var leeg = await CorpusAsync(filled: false);
        var gevuld = await CorpusAsync(filled: true);

        var alleenGevuld = gevuld.Except(leeg).ToList();
        var alleenLeeg = leeg.Except(gevuld).ToList();

        Assert.True(alleenGevuld.Count == 0,
            "een statement vuurt alleen bij gevulde rijen — dan mist de probe tegen een lege "
            + "database dekking en kan G1 stil onder-rapporteren:\n"
            + string.Join("\n---\n", alleenGevuld));
        Assert.True(alleenLeeg.Count == 0,
            "een statement vuurt alleen bij lege rijen:\n" + string.Join("\n---\n", alleenLeeg));
    }

    [Fact]
    public async Task Fixture_IsEchtGevuld()
    {
        // Zonder deze check zou G4 triviaal groen kunnen worden doordat de "gevulde"
        // fixture in werkelijkheid niets projecteert.
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

    private static async Task<IReadOnlyList<string>> CorpusAsync(bool filled)
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

        return driver.Queries;
    }

    private static async Task<HashSet<string>> WrittenEdgesAsync(bool filled) =>
        [.. (await CorpusAsync(filled))
            .SelectMany(q => WrittenEdge.Matches(q).Select(m => m.Groups[1].Value))];

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
