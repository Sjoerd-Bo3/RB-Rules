using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphSyncResult(
    int Cards, int Domains, int Tags, int Mechanics,
    int Sections = 0, int Concepts = 0, int Errata = 0, int Bans = 0, int Governed = 0);

/// <summary>Neo4j-sync met batched UNWIND (audit-fix: de PoP deed ~4 queries
/// per kaart). Tag ≠ Mechanic: facties/tribes worden (:Tag), geminede
/// spelmechanieken (:Mechanic). Parameters als dictionaries — de driver
/// serialiseert geen anonymous types in collecties.
///
/// Sinds #377 is dit een échte kennisgraaf: naast kaartfacetten gaan ook
/// regelsecties (met hun hiërarchie), primer-concepten, errata en bans mee,
/// en leidt Neo4j GOVERNED_BY af uit HAS_MECHANIC + DEFINES. De structuur
/// volgt GraphOntology — relaties die daar niet in staan komen er niet in.</summary>
public class GraphSyncService(RbRulesDbContext db, IDriver driver)
{
    /// <summary>Per mechaniek de meest algemene secties die hem beschrijven.</summary>
    private const int DefiningSectionsPerMechanic = 3;

    public async Task<GraphSyncResult> SyncAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Invoke("kaarten en facetten verzamelen");

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
        var canonicalIds = cards.Select(c => c.RiftboundId).ToHashSet();

        // Variant → canonieke printing, zodat errata en bans die aan een
        // alt-art hangen op de juiste knoop landen.
        var canonicalOf = await db.Cards.AsNoTracking()
            .Select(c => new { c.RiftboundId, c.VariantOf })
            .ToDictionaryAsync(c => c.RiftboundId, c => c.VariantOf ?? c.RiftboundId, ct);

        var cardRows = cards.Select(c => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["name"] = c.Name,
            ["type"] = c.Type,
            ["rarity"] = c.Rarity,
            ["energy"] = c.Energy,
            ["might"] = c.Might,
            ["set"] = c.SetId,
            ["setLabel"] = c.SetLabel,
        }).ToList();

        var domainPairs = Pairs(cards, c => c.Domains);
        var tagPairs = Pairs(cards, c => c.Tags);
        var mechanicPairs = Pairs(cards, c => c.Mechanics ?? []);

        await using var session = driver.AsyncSession();

        // Eerder gesyncte variant-knopen opruimen (#57) — de graph is vóór
        // de variantgroepering gevuld.
        await session.RunAsync(
            "MATCH (c:Card) WHERE NOT c.id IN $ids DETACH DELETE c",
            new Dictionary<string, object>
            {
                ["ids"] = canonicalIds.Select(id => (object)id).ToList(),
            });

        await session.RunAsync(
            """
            UNWIND $rows AS row
            MERGE (c:Card {id: row.id})
              SET c.name = row.name, c.type = row.type, c.rarity = row.rarity,
                  c.energy = row.energy, c.might = row.might
            WITH c, row WHERE row.set IS NOT NULL
            MERGE (s:Set {id: row.set}) ON CREATE SET s.label = row.setLabel
            MERGE (c)-[:FROM_SET]->(s)
            """,
            new Dictionary<string, object> { ["rows"] = cardRows });

        await RunPairsAsync(session,
            "MERGE (d:Domain {name: p.value}) MERGE (c)-[:HAS_DOMAIN]->(d)", domainPairs);
        await RunPairsAsync(session,
            "MERGE (t:Tag {name: p.value}) MERGE (c)-[:HAS_TAG]->(t)", tagPairs);
        await RunPairsAsync(session,
            "MERGE (m:Mechanic {name: p.value}) MERGE (c)-[:HAS_MECHANIC]->(m)", mechanicPairs);

        var sections = await SyncRuleSectionsAsync(session, progress, ct);
        var defining = await SyncDefinesAsync(session, cards, progress, ct);
        var governed = await InferGovernedByAsync(session, progress);
        var concepts = await SyncConceptsAsync(session, progress, ct);
        var (errata, bans) = await SyncErrataAndBansAsync(session, canonicalOf, canonicalIds, progress, ct);

        return new(
            cardRows.Count,
            CountDistinct(domainPairs),
            CountDistinct(tagPairs),
            CountDistinct(mechanicPairs),
            Sections: sections,
            Concepts: concepts,
            Errata: errata,
            Bans: bans,
            Governed: governed);
    }

    /// <summary>Regelsecties als knopen, met hun hiërarchie (601.2.d hangt
    /// onder 601.2 onder 601) — die zat tot nu toe alleen impliciet in de
    /// §-code-string.</summary>
    private async Task<int> SyncRuleSectionsAsync(
        IAsyncSession session, Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("regelsecties naar de graaf");

        var chunks = await db.RuleChunks.AsNoTracking()
            .Where(c => c.SectionCode != null && c.SectionCode != "")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { Code = c.SectionCode!, c.SourceId, c.Page, c.Text })
            .ToListAsync(ct);
        if (chunks.Count == 0) return 0;

        var sections = chunks
            .GroupBy(c => c.Code)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    Code = g.Key,
                    first.SourceId,
                    first.Page,
                    Snippet = first.Text[..Math.Min(first.Text.Length, 200)],
                };
            })
            .ToList();

        var rows = sections.Select(s => (object)new Dictionary<string, object?>
        {
            ["code"] = s.Code,
            ["source"] = s.SourceId,
            ["page"] = s.Page,
            ["snippet"] = s.Snippet,
        }).ToList();

        await session.RunAsync(
            """
            UNWIND $rows AS row
            MERGE (r:RuleSection {code: row.code})
              SET r.source = row.source, r.page = row.page, r.snippet = row.snippet
            """,
            new Dictionary<string, object> { ["rows"] = rows });

        // Directe ouder-kind-relaties; alleen als de ouder ook bestaat.
        var codes = sections.Select(s => s.Code).ToHashSet();
        var parentRows = sections
            .Select(s => new { s.Code, Parent = RuleSectionParser.ParentCodes(s.Code).LastOrDefault() })
            .Where(x => x.Parent is not null && codes.Contains(x.Parent))
            .Select(x => (object)new Dictionary<string, object?>
            {
                ["parent"] = x.Parent,
                ["child"] = x.Code,
            })
            .ToList();

        if (parentRows.Count > 0)
        {
            await session.RunAsync(
                """
                UNWIND $rows AS row
                MATCH (p:RuleSection {code: row.parent}), (c:RuleSection {code: row.child})
                MERGE (p)-[:PARENT_OF]->(c)
                """,
                new Dictionary<string, object> { ["rows"] = parentRows });
        }

        return sections.Count;
    }

    /// <summary>DEFINES: de meest algemene secties die een mechaniek noemen.
    /// Korte §-codes staan hoger in de boom en zijn dus definiërender dan een
    /// diepe subregel die het keyword terloops gebruikt.</summary>
    private async Task<int> SyncDefinesAsync(
        IAsyncSession session, List<Card> cards, Action<string>? progress, CancellationToken ct)
    {
        var mechanics = cards
            .SelectMany(c => c.Mechanics ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => m.Length >= 3)
            .ToList();
        if (mechanics.Count == 0) return 0;

        progress?.Invoke($"mechanieken koppelen aan definiërende secties ({mechanics.Count})");

        var rows = new List<object>();
        foreach (var mechanic in mechanics)
        {
            var codes = await db.RuleChunks.AsNoTracking()
                .Where(c => c.SectionCode != null && EF.Functions.ILike(c.Text, $"%{mechanic}%"))
                .Select(c => c.SectionCode!)
                .Distinct()
                .ToListAsync(ct);

            foreach (var code in codes
                .OrderBy(c => c.Length)
                .ThenBy(c => c, StringComparer.Ordinal)
                .Take(DefiningSectionsPerMechanic))
            {
                rows.Add(new Dictionary<string, object?> { ["code"] = code, ["mechanic"] = mechanic });
            }
        }
        if (rows.Count == 0) return 0;

        await session.RunAsync(
            """
            UNWIND $rows AS row
            MATCH (r:RuleSection {code: row.code}), (m:Mechanic {name: row.mechanic})
            MERGE (r)-[:DEFINES]->(m)
            """,
            new Dictionary<string, object> { ["rows"] = rows });
        return rows.Count;
    }

    /// <summary>Inferentie in de graaf zelf: draagt een kaart een mechaniek
    /// en definieert een sectie die mechaniek, dan valt de kaart onder die
    /// sectie. Dit is precies wat een kennisgraaf toevoegt boven losse
    /// tabellen — het feit stond nergens, maar volgt uit twee andere.</summary>
    private static async Task<int> InferGovernedByAsync(IAsyncSession session, Action<string>? progress)
    {
        progress?.Invoke("afgeleide relaties berekenen (GOVERNED_BY)");
        var cursor = await session.RunAsync(
            """
            MATCH (c:Card)-[:HAS_MECHANIC]->(m:Mechanic)<-[:DEFINES]-(r:RuleSection)
            MERGE (c)-[g:GOVERNED_BY]->(r)
              SET g.inferred = true, g.via = m.name
            RETURN count(g) AS n
            """);
        var record = await cursor.SingleAsync();
        return record["n"].As<int>();
    }

    /// <summary>Primer-concepten en de secties die ze uitleggen.</summary>
    private async Task<int> SyncConceptsAsync(
        IAsyncSession session, Action<string>? progress, CancellationToken ct)
    {
        var docs = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer" && k.Status == "approved")
            .Select(k => new { k.Topic, k.Title, k.SectionRefs })
            .ToListAsync(ct);
        if (docs.Count == 0) return 0;

        progress?.Invoke($"primer-concepten naar de graaf ({docs.Count})");

        var conceptRows = docs.Select(d => (object)new Dictionary<string, object?>
        {
            ["id"] = d.Topic,
            ["title"] = d.Title,
        }).ToList();

        await session.RunAsync(
            """
            UNWIND $rows AS row
            MERGE (c:Concept {id: row.id}) SET c.title = row.title
            """,
            new Dictionary<string, object> { ["rows"] = conceptRows });

        var explainRows = docs
            .SelectMany(d => (d.SectionRefs ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Select(code => (object)new Dictionary<string, object?>
                {
                    ["concept"] = d.Topic,
                    ["code"] = code,
                }))
            .ToList();

        if (explainRows.Count > 0)
        {
            await session.RunAsync(
                """
                UNWIND $rows AS row
                MATCH (c:Concept {id: row.concept}), (r:RuleSection {code: row.code})
                MERGE (c)-[:EXPLAINS]->(r)
                """,
                new Dictionary<string, object> { ["rows"] = explainRows });
        }

        return docs.Count;
    }

    /// <summary>Errata en bans als knopen, gekoppeld aan de canonieke kaart —
    /// zo is een ban op één printing zichtbaar vanaf de hele variantgroep.</summary>
    private async Task<(int Errata, int Bans)> SyncErrataAndBansAsync(
        IAsyncSession session,
        Dictionary<string, string> canonicalOf,
        HashSet<string> canonicalIds,
        Action<string>? progress,
        CancellationToken ct)
    {
        progress?.Invoke("errata en bans naar de graaf");

        string? Canonical(string? cardId) =>
            cardId is not null && canonicalOf.TryGetValue(cardId, out var canonical)
            && canonicalIds.Contains(canonical) ? canonical : null;

        var errata = (await db.Errata.AsNoTracking()
                .Where(e => e.CardRiftboundId != null)
                .Select(e => new { e.Id, e.CardName, e.NewText, e.CardRiftboundId })
                .ToListAsync(ct))
            .Select(e => new { e.Id, e.CardName, e.NewText, Card = Canonical(e.CardRiftboundId) })
            .Where(e => e.Card is not null)
            .ToList();

        if (errata.Count > 0)
        {
            await session.RunAsync(
                """
                UNWIND $rows AS row
                MERGE (e:Erratum {id: row.id})
                  SET e.cardName = row.cardName, e.newText = row.newText
                WITH e, row
                MATCH (c:Card {id: row.card})
                MERGE (e)-[:AMENDS]->(c)
                """,
                new Dictionary<string, object>
                {
                    ["rows"] = errata.Select(e => (object)new Dictionary<string, object?>
                    {
                        ["id"] = $"erratum:{e.Id}",
                        ["cardName"] = e.CardName,
                        ["newText"] = e.NewText[..Math.Min(e.NewText.Length, 300)],
                        ["card"] = e.Card,
                    }).ToList(),
                });
        }

        var bans = (await db.BanEntries.AsNoTracking()
                .Where(b => b.CardRiftboundId != null)
                .Select(b => new { b.Id, b.Name, b.Kind, b.CardRiftboundId })
                .ToListAsync(ct))
            .Select(b => new { b.Id, b.Name, b.Kind, Card = Canonical(b.CardRiftboundId) })
            .Where(b => b.Card is not null)
            .ToList();

        if (bans.Count > 0)
        {
            await session.RunAsync(
                """
                UNWIND $rows AS row
                MERGE (b:BanEntry {id: row.id})
                  SET b.name = row.name, b.kind = row.kind
                WITH b, row
                MATCH (c:Card {id: row.card})
                MERGE (b)-[:BANS]->(c)
                """,
                new Dictionary<string, object>
                {
                    ["rows"] = bans.Select(b => (object)new Dictionary<string, object?>
                    {
                        ["id"] = $"ban:{b.Id}",
                        ["name"] = b.Name,
                        ["kind"] = b.Kind,
                        ["card"] = b.Card,
                    }).ToList(),
                });
        }

        return (errata.Count, bans.Count);
    }

    private static async Task RunPairsAsync(
        IAsyncSession session, string mergeClause, List<object> pairs)
    {
        await session.RunAsync(
            $"UNWIND $pairs AS p MATCH (c:Card {{id: p.id}}) {mergeClause}",
            new Dictionary<string, object> { ["pairs"] = pairs });
    }

    private static List<object> Pairs(IEnumerable<Card> cards, Func<Card, string[]> selector) =>
        [.. cards.SelectMany(c => selector(c).Select(v => (object)new Dictionary<string, object?>
        {
            ["id"] = c.RiftboundId,
            ["value"] = v,
        }))];

    private static int CountDistinct(List<object> pairs) =>
        pairs.Cast<Dictionary<string, object?>>()
            .Select(d => (string?)d["value"])
            .Distinct()
            .Count();
}
