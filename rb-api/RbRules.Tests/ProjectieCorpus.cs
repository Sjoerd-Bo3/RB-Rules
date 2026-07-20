using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De gedeelde opname van álle Cypher die de twee volledige
/// rebuild-projecties (<see cref="GraphSyncService"/>, <see cref="BreinProjectionService"/>)
/// uitvoeren, plus de representatieve databank-stand waartegen ze draaien.
///
/// Sinds #289 PR 2 gedeeld tussen de twee guards die op dit corpus rusten:
/// <see cref="ProjectionOntologyGuardTests"/> (edge-NAMEN) en
/// <see cref="ProjectionLabelGuardTests"/> (de KNOOPLABELS aan weerszijden). Eén
/// fixture, want twee kopieën zouden onvermijdelijk uiteenlopen — en dan zou
/// <c>ProjectionOntologyGuardTests.Fixture_IsEchtGevuld</c>, de bewaker-van-de-
/// bewaker, nog maar de helft bewaken.</summary>
internal static class ProjectieCorpus
{
    /// <summary>Voert beide projecties uit tegen een opnemende driver en geeft élk
    /// uitgevoerd statement terug.</summary>
    public static async Task<IReadOnlyList<RecordedStatement>> CorpusAsync(bool filled)
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

    /// <summary>Een representatieve stand: elke rij-verzameling die de twee projecties
    /// lezen is niet-leeg, inclusief de edge-lijsten (PART_OF, EXPLAINS, alle vier
    /// ABOUT-varianten voor zowel Claim als Ruling, SUPPORTED_BY, SUPERSEDES, beide
    /// AFFECTS-varianten, HAS_ROLE, REQUIRES_CONDITION, GOVERNED_BY, MERGED_INTO,
    /// HAS_PREDICATE, PRECEDES). Zie
    /// <see cref="ProjectionOntologyGuardTests.Fixture_IsEchtGevuld"/>.</summary>
    public static async Task VulAsync(RbRulesDbContext db)
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
