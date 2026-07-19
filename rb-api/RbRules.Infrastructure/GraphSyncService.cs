using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphSyncResult(
    int Cards, int Domains, int Tags, int Mechanics,
    int Sections, int Concepts, int Claims, int Sources, int Errata, int Changes,
    int Relations, int Rulings, int MiningRuns = 0, int Assertions = 0);

/// <summary>Neo4j-sync met batched UNWIND (audit-fix: de PoP deed ~4 queries
/// per kaart; dit zijn er een handvol totaal). Tag ≠ Mechanic: facties/tribes
/// worden (:Tag), geminede spelmechanieken (:Mechanic). Parameters als
/// dictionaries — de driver serialiseert geen anonymous types in collecties.
/// Sinds #104 projecteert dezelfde transactionele rebuild ook de kennislagen
/// (docs/BRAIN.md §2.2): RuleSection (+PART_OF), Concept (+EXPLAINS), Claim
/// (+ABOUT/SUPPORTED_BY, alleen accepted/unreviewed), Source, Erratum
/// (+SUPERSEDES) en Change (+AFFECTS); elke knoop draagt een ref-property
/// volgens de BrainRef-conventie (§2.1). Sinds #116 ook de dynamische
/// LLM-relaties als RELATES_TO {kind, trust, explanation, status} — via de
/// reviewpoort (RelationProjection), nooit rechtstreeks. Sinds #191 ook
/// geverifieerde rulings als Ruling (+ABOUT/SUPPORTED_BY, alleen
/// status=verified) — hetzelfde Claim-patroon, via RulingTopicMapper.</summary>
public class GraphSyncService(RbRulesDbContext db, IDriver driver)
{
    public async Task<GraphSyncResult> SyncAsync(CancellationToken ct = default)
    {
        // Alleen canonieke printings (#57): alt-arts zijn dezelfde kaart in
        // het spel en horen niet als losse knopen in de graph. Projectie
        // zonder embedding-vectoren (#43).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, Type = c.Type,
                Rarity = c.Rarity, Domains = c.Domains, Tags = c.Tags,
                Mechanics = c.Mechanics, Energy = c.Energy, Might = c.Might,
                SetId = c.SetId, SetLabel = c.SetLabel,
            })
            .ToListAsync(ct);

        // Naam→canoniek-id-lookup voor de mappers: álle printings, zodat een
        // claim of erratum dat aan een alt-art hangt op de canonieke knoop
        // uitkomt.
        var allCards = await db.Cards.AsNoTracking()
            .Select(c => new { c.RiftboundId, c.Name, c.VariantOf })
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking()
            .OrderByDescending(s => s.Rank)
            .Select(s => new { s.Id, s.Name, s.TrustTier, s.Rank, s.Url })
            .ToListAsync(ct);

        // Sectie-knopen: één per (bron, §-code); de chunk-splitsing (zelfde
        // code in meerdere chunks) is een opslagdetail en vouwt hier samen.
        // Voorkeursvolgorde op bron-rank: bij een code die in meerdere
        // bronnen bestaat wijzen mappers naar de hoogst gerankte bron.
        var rankBySource = sources.ToDictionary(s => s.Id, s => s.Rank);
        var sections = (await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && r.SectionCode != "")
                .Select(r => new { r.SourceId, Code = r.SectionCode! })
                .Distinct()
                .ToListAsync(ct))
            .OrderByDescending(s => rankBySource.GetValueOrDefault(s.SourceId))
            .ThenBy(s => s.SourceId).ThenBy(s => s.Code)
            .ToList();

        // Defensief uniek op topic: de ref is de unieke sleutel in de graph.
        var concepts = (await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer")
                .Select(k => new { k.Topic, k.Title, k.Status, k.SectionRefs })
                .ToListAsync(ct))
            .GroupBy(k => k.Topic).Select(g => g.First())
            .ToList();

        // Scope-keuze (docs/BRAIN.md §2.2): alleen accepted/unreviewed claims
        // — rejected/superseded blijven in Postgres, zodat geen tool per
        // ongeluk weerlegde kennis als buurknoop presenteert.
        var claims = await db.Claims.AsNoTracking()
            .Where(c => c.Status == "accepted" || c.Status == "unreviewed")
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement,
                c.Corroboration, c.TrustScore, c.Status, c.OfficialStatus,
            })
            .ToListAsync(ct);

        var claimSources = await db.ClaimSources.AsNoTracking()
            .Where(s => s.Claim!.Status == "accepted" || s.Claim!.Status == "unreviewed")
            .Select(s => new { s.ClaimId, s.SourceId })
            .Distinct()
            .ToListAsync(ct);

        var errata = await db.Errata.AsNoTracking()
            .Select(e => new { e.Id, e.CardName, e.CardRiftboundId })
            .ToListAsync(ct);

        // Bewust ALLE changes, ook geconsolideerde secundairen (#206): de
        // graph is de volledige, ongefilterde brontrail — consolidatie is
        // feed-presentatie, geen kennisrelatie (ARCHITECTURE §6.3).
        var changes = await db.Changes.AsNoTracking()
            .Select(c => new { c.Id, c.ChangeType, c.Severity, c.DetectedAt, c.Summary, c.Meaning, c.Diff })
            .ToListAsync(ct);

        // Rulings (#191): alleen verified — unverified/rejected zijn geen
        // kennis en blijven Postgres-only, zelfde kennislagen-discipline als
        // de accepted/unreviewed-scope hierboven voor Claim.
        var rulings = await db.Corrections.AsNoTracking()
            .Where(c => c.Status == "verified")
            .Select(c => new
            {
                c.Id, c.Scope, c.Ref, c.Text, c.Question, c.Provenance,
                c.SourceRef, c.VerifiedAt,
            })
            .ToListAsync(ct);

        // Provenance-ruggengraat (fase 0a, #233): PROV-O-Activity + Assertion.
        // Postgres is de bron; deze projectie is idempotent herbouwbaar. De
        // Assertion draagt in Postgres altijd WAS_GENERATED_BY + DERIVED_FROM
        // (schrijfpoort), dus de edges hieronder resolveren per constructie.
        var miningRuns = await db.MiningRuns.AsNoTracking()
            .Select(r => new { r.Id, r.Kind, r.LlmModel, r.PromptVersion, r.StartedAt, r.CompletedAt })
            .ToListAsync(ct);
        var assertions = await db.Assertions.AsNoTracking()
            .Select(a => new
            {
                a.Id, a.Subject, a.FactKind, a.MiningRunId, a.DerivedFromRef,
                a.Model, a.EmbeddingModel, a.EmbeddingDim, a.Verifier, a.Verdict, a.AssertedAt,
            })
            .ToListAsync(ct);

        // Dynamische relaties (#116): LLM-relaties gaan nooit rechtstreeks de
        // graph in — hier projecteert de rebuild wat de reviewpoort doorlaat.
        // RelationProjection (Domain, getest): rejected nooit; accepted en
        // unreviewed (status als edge-property) alleen met een geaccepteerd
        // kind (seed + geaccepteerde RelationKinds).
        var acceptedKindSet = RelationProjection.AcceptedKindSet(
            await db.RelationKinds.AsNoTracking()
                .Where(k => k.Status == "accepted")
                .Select(k => k.Kind)
                .ToListAsync(ct));
        var relations = (await db.Relations.AsNoTracking()
                .Select(r => new { r.FromRef, r.ToRef, r.Kind, r.Explanation, r.Trust, r.Status })
                .ToListAsync(ct))
            .Where(r => RelationProjection.ShouldProject(r.Status, r.Kind, acceptedKindSet))
            .ToList();

        // Pure mappers (Domain): topic→ref voor claims, naam/§-match voor
        // change-AFFECTS. Niet te matchen doelen ⇒ knoop zonder edge.
        var topicMapper = ClaimTopicMapper.Create(
            allCards.Select(c => (c.RiftboundId, c.Name, c.VariantOf)),
            cards.SelectMany(c => c.Mechanics ?? []),
            sections.Select(s => (s.SourceId, s.Code)),
            concepts.Select(k => (k.Topic, k.Title)));
        var affectsMapper = ChangeAffectsMapper.Create(
            cards.Select(c => (c.RiftboundId, c.Name)),
            sections.Select(s => (s.SourceId, s.Code)));

        var cardRows = cards.Select(c => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["ref"] = BrainRef.Card(c.RiftboundId).Format(),
            ["name"] = c.Name,
            ["type"] = c.Type,
            ["rarity"] = c.Rarity,
            ["energy"] = c.Energy,
            ["might"] = c.Might,
            ["set"] = c.SetId,
            ["setRef"] = c.SetId is null ? null : BrainRef.Set(c.SetId).Format(),
            ["setLabel"] = c.SetLabel,
        }).ToList();

        var domainPairs = Pairs(cards, c => c.Domains, BrainRef.Domain);
        var tagPairs = Pairs(cards, c => c.Tags, BrainRef.Tag);
        var mechanicPairs = Pairs(cards, c => c.Mechanics ?? [], BrainRef.Mechanic);

        var sectionRows = sections.Select(s => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Section(s.SourceId, s.Code).Format(),
            ["code"] = s.Code,
            ["sourceId"] = s.SourceId,
        }).ToList();

        // PART_OF: kind → dichtstbijzijnde bestáánde ouder binnen dezelfde
        // bron ("466.2.c" hangt aan "466.2", of aan "466" als het
        // tussenniveau geen eigen tekst heeft).
        var partOfPairs = new List<object>();
        foreach (var group in sections.GroupBy(s => s.SourceId))
        {
            var codes = group.Select(s => s.Code).ToHashSet();
            foreach (var s in group)
            {
                var parent = RuleSectionParser.ParentCodes(s.Code)
                    .Reverse().FirstOrDefault(codes.Contains);
                if (parent is null) continue;
                partOfPairs.Add(new Dictionary<string, object?>
                {
                    ["child"] = BrainRef.Section(s.SourceId, s.Code).Format(),
                    ["parent"] = BrainRef.Section(s.SourceId, parent).Format(),
                });
            }
        }

        var conceptRows = concepts.Select(k => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Concept(k.Topic).Format(),
            ["topic"] = k.Topic,
            ["title"] = k.Title,
            ["status"] = k.Status,
        }).ToList();

        // EXPLAINS: KnowledgeDoc.SectionRefs (komma-gescheiden §-codes) →
        // sectie-knopen. Codes die (nog) geen chunk hebben vallen stil weg.
        var explainsPairs = new List<object>();
        var explainsSeen = new HashSet<(string, string)>();
        foreach (var k in concepts)
        {
            var codes = (k.SectionRefs ?? "").Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var code in codes)
            {
                if (topicMapper.ResolveSection(code) is not { } target) continue;
                if (!explainsSeen.Add((k.Topic, target.Key))) continue;
                explainsPairs.Add(new Dictionary<string, object?>
                {
                    ["concept"] = BrainRef.Concept(k.Topic).Format(),
                    ["section"] = target.Format(),
                });
            }
        }

        var sourceRows = sources.Select(s => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Source(s.Id).Format(),
            ["id"] = s.Id,
            ["name"] = s.Name,
            ["trustTier"] = (int)s.TrustTier,
        }).ToList();

        var claimRows = claims.Select(c => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Claim(c.Id).Format(),
            ["statement"] = c.Statement,
            ["corroboration"] = c.Corroboration,
            ["trustScore"] = c.TrustScore,
            ["status"] = c.Status,
            ["officialStatus"] = c.OfficialStatus,
        }).ToList();

        // ABOUT per doelsoort (kaart op id, mechaniek op name, sectie/concept
        // op ref — allemaal constraint-gedekte sleutels). Geen match = claim
        // zonder ABOUT-edge, per ontwerp.
        var aboutCard = new List<object>();
        var aboutMechanic = new List<object>();
        var aboutSection = new List<object>();
        var aboutConcept = new List<object>();
        foreach (var c in claims)
        {
            if (topicMapper.Resolve(c.TopicType, c.TopicRef) is not { } target) continue;
            var pair = (object)new Dictionary<string, object?>
            {
                ["claim"] = BrainRef.Claim(c.Id).Format(),
                ["target"] = target.Kind is BrainRefKind.Card or BrainRefKind.Mechanic
                    ? target.Key : target.Format(),
            };
            (target.Kind switch
            {
                BrainRefKind.Card => aboutCard,
                BrainRefKind.Mechanic => aboutMechanic,
                BrainRefKind.Section => aboutSection,
                _ => aboutConcept,
            }).Add(pair);
        }

        var supportedByPairs = claimSources.Select(s => (object)new Dictionary<string, object?>
        {
            ["claim"] = BrainRef.Claim(s.ClaimId).Format(),
            ["source"] = BrainRef.Source(s.SourceId).Format(),
        }).ToList();

        var rulingRows = rulings.Select(r => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Ruling(r.Id).Format(),
            ["text"] = r.Text,
            ["question"] = r.Question,
            ["kind"] = RulingKind.FromProvenance(r.Provenance),
            ["verifiedAt"] = r.VerifiedAt?.UtcDateTime.ToString("o"),
        }).ToList();

        // ABOUT: zelfde resolutie als Claim, via RulingTopicMapper (Scope →
        // gedeeld topic-vocabulaire → ClaimTopicMapper.Resolve). Scope
        // "answer" (chat-ruling zonder anker) en de review-notitie-
        // promotiescopes "claim"/"relation" resolven nooit ⇒ knoop zonder
        // ABOUT-edge, per ontwerp (zelfde gedrag als een niet-matchende claim).
        var rulingAboutCard = new List<object>();
        var rulingAboutMechanic = new List<object>();
        var rulingAboutSection = new List<object>();
        var rulingAboutConcept = new List<object>();
        foreach (var r in rulings)
        {
            if (RulingTopicMapper.Resolve(topicMapper, r.Scope, r.Ref) is not { } target) continue;
            var pair = (object)new Dictionary<string, object?>
            {
                ["ruling"] = BrainRef.Ruling(r.Id).Format(),
                ["target"] = target.Kind is BrainRefKind.Card or BrainRefKind.Mechanic
                    ? target.Key : target.Format(),
            };
            (target.Kind switch
            {
                BrainRefKind.Card => rulingAboutCard,
                BrainRefKind.Mechanic => rulingAboutMechanic,
                BrainRefKind.Section => rulingAboutSection,
                _ => rulingAboutConcept,
            }).Add(pair);
        }

        // SUPPORTED_BY: SourceRef (#166, de "waar besloten"-URL/citatie) →
        // Source, via dezelfde genormaliseerde URL-kandidaten als het
        // bron-dossier (#171, SourceScout.UrlCandidates). sources staat al op
        // Rank aflopend (zie hierboven) — TryAdd laat bij een gedeelde URL
        // (#175) dus de hoogst gerankte bron winnen. Geen match (vrije
        // citatie zonder URL, of onbekende bron) ⇒ knoop zonder edge.
        var sourceIdByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in sources)
            foreach (var candidate in SourceScout.UrlCandidates(s.Url))
                sourceIdByUrl.TryAdd(candidate, s.Id);
        var rulingSupportedByPairs = new List<object>();
        foreach (var r in rulings)
        {
            if (r.SourceRef is null || !sourceIdByUrl.TryGetValue(r.SourceRef, out var sourceId))
                continue;
            rulingSupportedByPairs.Add(new Dictionary<string, object?>
            {
                ["ruling"] = BrainRef.Ruling(r.Id).Format(),
                ["source"] = BrainRef.Source(sourceId).Format(),
            });
        }

        var erratumRows = errata.Select(e => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Erratum(e.Id).Format(),
            ["cardName"] = e.CardName,
        }).ToList();

        // SUPERSEDES: het opgeslagen id kan een variant-printing zijn
        // (naam-match bij de extractie) — resolven naar canoniek; lukt dat
        // niet, dan alsnog via de kaartnaam. Geen match = knoop zonder edge.
        var canonicalIds = cards.Select(c => c.RiftboundId).ToHashSet();
        var variantOfById = allCards.Where(c => c.VariantOf != null)
            .ToDictionary(c => c.RiftboundId, c => c.VariantOf!);
        var supersedesPairs = new List<object>();
        foreach (var e in errata)
        {
            var cardId = e.CardRiftboundId is { } id
                ? (canonicalIds.Contains(id) ? id : variantOfById.GetValueOrDefault(id))
                : null;
            cardId ??= topicMapper.Resolve("card", e.CardName) is { } byName ? byName.Key : null;
            if (cardId is null) continue;
            supersedesPairs.Add(new Dictionary<string, object?>
            {
                ["erratum"] = BrainRef.Erratum(e.Id).Format(),
                ["cardId"] = cardId,
            });
        }

        var changeRows = changes.Select(c => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Change(c.Id).Format(),
            ["changeType"] = c.ChangeType,
            ["severity"] = c.Severity,
            ["detectedAt"] = c.DetectedAt.UtcDateTime.ToString("o"),
        }).ToList();

        var relationRows = relations.Select(r => (object)new Dictionary<string, object?>
        {
            ["from"] = r.FromRef,
            ["to"] = r.ToRef,
            ["kind"] = r.Kind,
            ["trust"] = r.Trust,
            ["explanation"] = r.Explanation,
            ["status"] = r.Status,
        }).ToList();

        var miningRunRows = miningRuns.Select(r => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.MiningRun(r.Id).Format(),
            ["id"] = r.Id,
            ["kind"] = r.Kind,
            ["llmModel"] = r.LlmModel,
            ["promptVersion"] = r.PromptVersion,
            ["startedAt"] = r.StartedAt.UtcDateTime.ToString("o"),
            ["completedAt"] = r.CompletedAt?.UtcDateTime.ToString("o"),
        }).ToList();

        var assertionRows = assertions.Select(a => (object)new Dictionary<string, object?>
        {
            ["ref"] = BrainRef.Assertion(a.Id).Format(),
            ["subject"] = a.Subject,
            ["factKind"] = a.FactKind,
            ["run"] = BrainRef.MiningRun(a.MiningRunId).Format(),
            ["derivedFrom"] = a.DerivedFromRef,
            ["model"] = a.Model,
            ["embeddingModel"] = a.EmbeddingModel,
            ["embeddingDim"] = a.EmbeddingDim,
            ["verifier"] = a.Verifier,
            ["verdict"] = a.Verdict,
            ["assertedAt"] = a.AssertedAt.UtcDateTime.ToString("o"),
        }).ToList();

        var affectsSection = new List<object>();
        var affectsCard = new List<object>();
        foreach (var c in changes)
        {
            // Gedeelde "betrokken"-definitie met de kennis-hertoets (#119).
            var text = ChangeAffectsMapper.AffectsText(c.Summary, c.Meaning, c.Diff);
            foreach (var target in affectsMapper.Resolve(c.ChangeType, text))
            {
                (target.Kind == BrainRefKind.Card ? affectsCard : affectsSection)
                    .Add((object)new Dictionary<string, object?>
                    {
                        ["change"] = BrainRef.Change(c.Id).Format(),
                        ["target"] = target.Kind == BrainRefKind.Card
                            ? target.Key : target.Format(),
                    });
            }
        }

        await using var session = driver.AsyncSession();

        // Eén transactie rond de hele rebuild (conventie): opruimen en de
        // nieuwe stand schrijven slagen of falen samen — een fout halverwege
        // mag geen half leeggeruimde graph achterlaten. Dispose zonder commit
        // = rollback.
        await using var tx = await session.BeginTransactionAsync();

        // Knopen die geen canonieke kaart (meer) zijn opruimen (#57): de graph
        // is vóór de variantgroepering gevuld, en ook een latere canonical-
        // wissel zou het oude id als wees achterlaten.
        await tx.RunAsync(
            "MATCH (c:Card) WHERE NOT c.id IN $ids DETACH DELETE c",
            new Dictionary<string, object>
            {
                ["ids"] = cards.Select(c => (object)c.RiftboundId).ToList(),
            });

        await tx.RunAsync(
            """
            UNWIND $rows AS row
            MERGE (c:Card {id: row.id})
              SET c.ref = row.ref, c.name = row.name, c.type = row.type,
                  c.rarity = row.rarity, c.energy = row.energy, c.might = row.might
            WITH c, row WHERE row.set IS NOT NULL
            MERGE (s:Set {id: row.set}) ON CREATE SET s.label = row.setLabel
            SET s.ref = row.setRef
            MERGE (c)-[:FROM_SET]->(s)
            """,
            new Dictionary<string, object> { ["rows"] = cardRows });

        await RunPairsAsync(tx,
            "MERGE (d:Domain {name: p.value}) SET d.ref = p.ref MERGE (c)-[:HAS_DOMAIN]->(d)", domainPairs);
        await RunPairsAsync(tx,
            "MERGE (t:Tag {name: p.value}) SET t.ref = p.ref MERGE (c)-[:HAS_TAG]->(t)", tagPairs);
        await RunPairsAsync(tx,
            "MERGE (m:Mechanic {name: p.value}) SET m.ref = p.ref MERGE (c)-[:HAS_MECHANIC]->(m)", mechanicPairs);

        // Facet-knopen die na de opruiming nergens meer aan hangen (bijv. een
        // promo-set die alleen variant-printings bevatte) verdwijnen mee.
        // Vóór de kennislaag-stappen: facetten bestaan bij gratie van kaarten,
        // niet van claims.
        await tx.RunAsync(
            """
            MATCH (n) WHERE (n:Set OR n:Domain OR n:Tag OR n:Mechanic)
              AND NOT (n)--() DELETE n
            """);

        // Kennislaag-projectie (#104): volledige herbouw binnen dezelfde
        // transactie — klein genoeg (duizenden knopen) en per definitie
        // idempotent, geen wees-administratie per soort nodig.
        await tx.RunAsync(
            """
            MATCH (n) WHERE n:RuleSection OR n:Concept OR n:Claim
              OR n:Source OR n:Erratum OR n:Change OR n:Ruling
              OR n:MiningRun OR n:Assertion
            DETACH DELETE n
            """);

        // RELATES_TO (#116) volledig herbouwen: de DETACH DELETE hierboven
        // ruimt alleen edges aan kennislaag-knopen op — tussen persistente
        // knopen (Card/Mechanic/…) zouden verwijderde of inmiddels verworpen
        // relaties anders blijven hangen. Deterministisch: de edges hieronder
        // zijn exact wat de reviewpoort nú doorlaat.
        await tx.RunAsync("MATCH ()-[r:RELATES_TO]->() DELETE r");

        await RunRowsAsync(tx,
            "CREATE (r:RuleSection {ref: row.ref, code: row.code, sourceId: row.sourceId})",
            sectionRows);
        await tx.RunAsync(
            """
            UNWIND $pairs AS p
            MATCH (child:RuleSection {ref: p.child})
            MATCH (parent:RuleSection {ref: p.parent})
            MERGE (child)-[:PART_OF]->(parent)
            """,
            new Dictionary<string, object> { ["pairs"] = partOfPairs });

        await RunRowsAsync(tx,
            "CREATE (k:Concept {ref: row.ref, topic: row.topic, title: row.title, status: row.status})",
            conceptRows);
        await tx.RunAsync(
            """
            UNWIND $pairs AS p
            MATCH (k:Concept {ref: p.concept})
            MATCH (r:RuleSection {ref: p.section})
            MERGE (k)-[:EXPLAINS]->(r)
            """,
            new Dictionary<string, object> { ["pairs"] = explainsPairs });

        await RunRowsAsync(tx,
            "CREATE (s:Source {ref: row.ref, id: row.id, name: row.name, trustTier: row.trustTier})",
            sourceRows);

        await RunRowsAsync(tx,
            """
            CREATE (cl:Claim {ref: row.ref, statement: row.statement,
                              corroboration: row.corroboration, trustScore: row.trustScore,
                              status: row.status, officialStatus: row.officialStatus})
            """,
            claimRows);
        await RunEdgesAsync(tx, "MATCH (cl:Claim {ref: p.claim}) MATCH (t:Card {id: p.target}) MERGE (cl)-[:ABOUT]->(t)", aboutCard);
        await RunEdgesAsync(tx, "MATCH (cl:Claim {ref: p.claim}) MATCH (t:Mechanic {name: p.target}) MERGE (cl)-[:ABOUT]->(t)", aboutMechanic);
        await RunEdgesAsync(tx, "MATCH (cl:Claim {ref: p.claim}) MATCH (t:RuleSection {ref: p.target}) MERGE (cl)-[:ABOUT]->(t)", aboutSection);
        await RunEdgesAsync(tx, "MATCH (cl:Claim {ref: p.claim}) MATCH (t:Concept {ref: p.target}) MERGE (cl)-[:ABOUT]->(t)", aboutConcept);
        await RunEdgesAsync(tx, "MATCH (cl:Claim {ref: p.claim}) MATCH (s:Source {ref: p.source}) MERGE (cl)-[:SUPPORTED_BY]->(s)", supportedByPairs);

        await RunRowsAsync(tx,
            """
            CREATE (rl:Ruling {ref: row.ref, text: row.text, question: row.question,
                               kind: row.kind, verifiedAt: row.verifiedAt})
            """,
            rulingRows);
        await RunEdgesAsync(tx, "MATCH (rl:Ruling {ref: p.ruling}) MATCH (t:Card {id: p.target}) MERGE (rl)-[:ABOUT]->(t)", rulingAboutCard);
        await RunEdgesAsync(tx, "MATCH (rl:Ruling {ref: p.ruling}) MATCH (t:Mechanic {name: p.target}) MERGE (rl)-[:ABOUT]->(t)", rulingAboutMechanic);
        await RunEdgesAsync(tx, "MATCH (rl:Ruling {ref: p.ruling}) MATCH (t:RuleSection {ref: p.target}) MERGE (rl)-[:ABOUT]->(t)", rulingAboutSection);
        await RunEdgesAsync(tx, "MATCH (rl:Ruling {ref: p.ruling}) MATCH (t:Concept {ref: p.target}) MERGE (rl)-[:ABOUT]->(t)", rulingAboutConcept);
        await RunEdgesAsync(tx, "MATCH (rl:Ruling {ref: p.ruling}) MATCH (s:Source {ref: p.source}) MERGE (rl)-[:SUPPORTED_BY]->(s)", rulingSupportedByPairs);

        await RunRowsAsync(tx,
            "CREATE (e:Erratum {ref: row.ref, cardName: row.cardName})",
            erratumRows);
        await RunEdgesAsync(tx, "MATCH (e:Erratum {ref: p.erratum}) MATCH (c:Card {id: p.cardId}) MERGE (e)-[:SUPERSEDES]->(c)", supersedesPairs);

        await RunRowsAsync(tx,
            """
            CREATE (ch:Change {ref: row.ref, changeType: row.changeType,
                               severity: row.severity, detectedAt: row.detectedAt})
            """,
            changeRows);
        await RunEdgesAsync(tx, "MATCH (ch:Change {ref: p.change}) MATCH (t:RuleSection {ref: p.target}) MERGE (ch)-[:AFFECTS]->(t)", affectsSection);
        await RunEdgesAsync(tx, "MATCH (ch:Change {ref: p.change}) MATCH (t:Card {id: p.target}) MERGE (ch)-[:AFFECTS]->(t)", affectsCard);

        // Provenance-knopen (fase 0a, #233): eerst de MiningRun-activities, dan
        // de Assertions met hun WAS_GENERATED_BY- en DERIVED_FROM-edges. De
        // DERIVED_FROM-doelen (Source/RuleSection/Card/…) bestaan op dit punt al,
        // dus een label-loze ref-match resolveert (zelfde patroon als RELATES_TO).
        await RunRowsAsync(tx,
            """
            CREATE (r:MiningRun {ref: row.ref, id: row.id, kind: row.kind,
                                 llmModel: row.llmModel, promptVersion: row.promptVersion,
                                 startedAt: row.startedAt, completedAt: row.completedAt})
            """,
            miningRunRows);
        await RunRowsAsync(tx,
            """
            CREATE (a:Assertion {ref: row.ref, subject: row.subject, factKind: row.factKind,
                                 derivedFrom: row.derivedFrom, model: row.model,
                                 embeddingModel: row.embeddingModel, embeddingDim: row.embeddingDim,
                                 verifier: row.verifier, verdict: row.verdict, assertedAt: row.assertedAt})
            """,
            assertionRows);
        await tx.RunAsync(
            """
            UNWIND $rows AS row
            MATCH (a:Assertion {ref: row.ref})
            MATCH (run:MiningRun {ref: row.run})
            MERGE (a)-[:WAS_GENERATED_BY]->(run)
            """,
            new Dictionary<string, object> { ["rows"] = assertionRows });
        await tx.RunAsync(
            """
            UNWIND $rows AS row
            MATCH (a:Assertion {ref: row.ref})
            MATCH (src {ref: row.derivedFrom})
            MERGE (a)-[:DERIVED_FROM]->(src)
            """,
            new Dictionary<string, object> { ["rows"] = assertionRows });

        // RELATES_TO als laatste: beide eindpunten kunnen elke knoopsoort
        // zijn, dus pas nadat álle knopen bestaan. Match op de ref-property
        // (per constructie globaal uniek, §2.1) zonder label — een label-loze
        // property-match is een scan, maar de aantallen zijn klein (§2.2) en
        // de rebuild draait toch al batched. MERGE op kind: twee kinds tussen
        // hetzelfde paar zijn twee edges. Refs zonder knoop (verdwenen
        // mechaniek, verwijderd doc) vallen stil weg — knoop zonder edge is
        // het bestaande ABOUT-gedrag.
        await tx.RunAsync(
            """
            UNWIND $rows AS row
            MATCH (a {ref: row.from})
            MATCH (b {ref: row.to})
            MERGE (a)-[r:RELATES_TO {kind: row.kind}]->(b)
              SET r.trust = row.trust, r.explanation = row.explanation,
                  r.status = row.status
            """,
            new Dictionary<string, object> { ["rows"] = relationRows });

        await tx.CommitAsync();

        return new(
            cardRows.Count,
            CountDistinct(domainPairs),
            CountDistinct(tagPairs),
            CountDistinct(mechanicPairs),
            sectionRows.Count,
            conceptRows.Count,
            claimRows.Count,
            sourceRows.Count,
            erratumRows.Count,
            changeRows.Count,
            relationRows.Count,
            rulingRows.Count,
            miningRunRows.Count,
            assertionRows.Count);
    }

    private static async Task RunPairsAsync(
        IAsyncQueryRunner runner, string mergeClause, List<object> pairs)
    {
        await runner.RunAsync(
            $"UNWIND $pairs AS p MATCH (c:Card {{id: p.id}}) {mergeClause}",
            new Dictionary<string, object> { ["pairs"] = pairs });
    }

    private static async Task RunRowsAsync(
        IAsyncQueryRunner runner, string createClause, List<object> rows)
    {
        await runner.RunAsync(
            $"UNWIND $rows AS row {createClause}",
            new Dictionary<string, object> { ["rows"] = rows });
    }

    private static async Task RunEdgesAsync(
        IAsyncQueryRunner runner, string matchMergeClause, List<object> pairs)
    {
        await runner.RunAsync(
            $"UNWIND $pairs AS p {matchMergeClause}",
            new Dictionary<string, object> { ["pairs"] = pairs });
    }

    private static List<object> Pairs(
        IEnumerable<Card> cards, Func<Card, string[]> selector, Func<string, BrainRef> refFor) =>
        [.. cards.SelectMany(c => selector(c).Select(v => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["value"] = v,
            ["ref"] = refFor(v).Format(),
        }))];

    private static int CountDistinct(List<object> pairs) =>
        pairs.Cast<Dictionary<string, object?>>()
            .Select(d => (string?)d["value"])
            .Distinct()
            .Count();
}
