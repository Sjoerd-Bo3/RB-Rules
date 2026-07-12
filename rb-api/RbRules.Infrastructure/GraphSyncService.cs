using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphSyncResult(
    int Cards, int Domains, int Tags, int Mechanics,
    int Sections, int Concepts, int Claims, int Sources, int Errata, int Changes);

/// <summary>Neo4j-sync met batched UNWIND (audit-fix: de PoP deed ~4 queries
/// per kaart; dit zijn er een handvol totaal). Tag ≠ Mechanic: facties/tribes
/// worden (:Tag), geminede spelmechanieken (:Mechanic). Parameters als
/// dictionaries — de driver serialiseert geen anonymous types in collecties.
/// Sinds #104 projecteert dezelfde transactionele rebuild ook de kennislagen
/// (docs/BRAIN.md §2.2): RuleSection (+PART_OF), Concept (+EXPLAINS), Claim
/// (+ABOUT/SUPPORTED_BY, alleen accepted/unreviewed), Source, Erratum
/// (+SUPERSEDES) en Change (+AFFECTS); elke knoop draagt een ref-property
/// volgens de BrainRef-conventie (§2.1).</summary>
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
            .Select(s => new { s.Id, s.Name, s.TrustTier, s.Rank })
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

        var changes = await db.Changes.AsNoTracking()
            .Select(c => new { c.Id, c.ChangeType, c.Severity, c.DetectedAt, c.Summary, c.Meaning, c.Diff })
            .ToListAsync(ct);

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

        var affectsSection = new List<object>();
        var affectsCard = new List<object>();
        foreach (var c in changes)
        {
            var text = string.Join('\n', new[] { c.Summary, c.Meaning, c.Diff }
                .Where(t => !string.IsNullOrWhiteSpace(t)));
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
              OR n:Source OR n:Erratum OR n:Change
            DETACH DELETE n
            """);

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
            changeRows.Count);
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
