using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GraphNode(string Id, string Label);
public record GraphMechanicGroup(string Mechanic, IReadOnlyList<GraphNode> Cards);
public record GraphRule(string Code, string? Snippet, string? Via);
public record GraphInteraction(string OtherId, string OtherName, string Kind);
public record GraphFact(string Kind, string Label);

public record GraphNeighbors(
    GraphNode Center,
    IReadOnlyList<string> Domains,
    IReadOnlyList<GraphMechanicGroup> Mechanics,
    IReadOnlyList<GraphRule> Rules,
    IReadOnlyList<GraphInteraction> Interactions,
    IReadOnlyList<GraphFact> Facts,
    string Source);

public record GraphPath(IReadOnlyList<string> Nodes, IReadOnlyList<string> Relations);

/// <summary>Leeskant van de kennisgraaf (#377). Tot nu toe werd Neo4j alleen
/// geschreven en nooit gelezen; deze service maakt de graaf productief voor
/// de verkenner, de bewijsketen tussen twee kaarten en de graph-uitbreiding
/// in /ask.
///
/// Neo4j is niet hard vereist: valt de graaf uit of is hij nog niet gesynct,
/// dan beantwoordt Postgres dezelfde vraag met minder diepte. Het antwoord
/// vertelt via <c>Source</c> welke engine het leverde.</summary>
public class GraphQueryService(RbRulesDbContext db, IDriver driver, ILogger<GraphQueryService> logger)
{
    private const int MaxSharingCards = 6;
    private const int MaxRules = 8;
    private const int MaxInteractions = 12;

    public async Task<GraphNeighbors?> NeighborsAsync(string cardId, CancellationToken ct = default)
    {
        try
        {
            var fromGraph = await NeighborsFromGraphAsync(cardId, ct);
            if (fromGraph is not null) return fromGraph;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Neo4j-verkenner niet beschikbaar, terugval op Postgres");
        }
        return await NeighborsFromRelationalAsync(cardId, ct);
    }

    private async Task<GraphNeighbors?> NeighborsFromGraphAsync(string cardId, CancellationToken ct)
    {
        await using var session = driver.AsyncSession();
        var p = new Dictionary<string, object> { ["id"] = cardId };

        var centerCursor = await session.RunAsync(
            "MATCH (c:Card {id: $id}) RETURN c.name AS name", p);
        var centerRows = await centerCursor.ToListAsync();
        if (centerRows.Count == 0) return null;   // niet in de graaf → fallback
        var center = new GraphNode(cardId, centerRows[0]["name"].As<string>() ?? cardId);

        var domainCursor = await session.RunAsync(
            "MATCH (:Card {id: $id})-[:HAS_DOMAIN]->(d:Domain) RETURN d.name AS name", p);
        var domains = (await domainCursor.ToListAsync())
            .Select(r => r["name"].As<string>())
            .Where(n => n is not null)
            .ToList();

        var mechanicCursor = await session.RunAsync(
            $$"""
            MATCH (c:Card {id: $id})-[:HAS_MECHANIC]->(m:Mechanic)
            OPTIONAL MATCH (m)<-[:HAS_MECHANIC]-(o:Card) WHERE o.id <> $id
            WITH m, collect(DISTINCT {id: o.id, name: o.name})[..{{MaxSharingCards}}] AS others
            RETURN m.name AS mechanic, others
            """, p);
        var mechanics = (await mechanicCursor.ToListAsync())
            .Select(r => new GraphMechanicGroup(
                r["mechanic"].As<string>(),
                r["others"].As<List<object>>()
                    .OfType<Dictionary<string, object>>()
                    .Where(d => d.TryGetValue("id", out var id) && id is not null)
                    .Select(d => new GraphNode(
                        d["id"].As<string>(),
                        d.TryGetValue("name", out var n) ? n.As<string>() : d["id"].As<string>()))
                    .ToList()))
            .ToList();

        // Afgeleide GOVERNED_BY-relatie: welke regels beheersen deze kaart,
        // en via welke mechaniek — dit is het feit dat nergens is ingevoerd.
        var ruleCursor = await session.RunAsync(
            $$"""
            MATCH (:Card {id: $id})-[g:GOVERNED_BY]->(r:RuleSection)
            RETURN r.code AS code, r.snippet AS snippet, g.via AS via
            ORDER BY size(r.code), r.code LIMIT {{MaxRules}}
            """, p);
        var rules = (await ruleCursor.ToListAsync())
            .Select(r => new GraphRule(
                r["code"].As<string>(), r["snippet"].As<string?>(), r["via"].As<string?>()))
            .ToList();

        var interactionCursor = await session.RunAsync(
            $$"""
            MATCH (:Card {id: $id})-[i:INTERACTS_WITH]-(o:Card)
            RETURN o.id AS id, o.name AS name, i.kind AS kind LIMIT {{MaxInteractions}}
            """, p);
        var interactions = (await interactionCursor.ToListAsync())
            .Select(r => new GraphInteraction(
                r["id"].As<string>(), r["name"].As<string>() ?? r["id"].As<string>(),
                r["kind"].As<string?>() ?? "interactie"))
            .ToList();

        var factCursor = await session.RunAsync(
            """
            MATCH (:Card {id: $id})<-[rel:AMENDS|BANS]-(n)
            RETURN type(rel) AS rel, coalesce(n.newText, n.name) AS label LIMIT 6
            """, p);
        var facts = (await factCursor.ToListAsync())
            .Select(r => new GraphFact(
                r["rel"].As<string>() == "BANS" ? "Ban" : "Errata",
                r["label"].As<string>() ?? ""))
            .ToList();

        return new(center, domains!, mechanics, rules, interactions, facts, "neo4j");
    }

    /// <summary>Terugval zonder graaf: dezelfde vraag uit Postgres. Mist de
    /// afgeleide regelrelaties — die bestaan alleen in de graaf.</summary>
    private async Task<GraphNeighbors?> NeighborsFromRelationalAsync(string cardId, CancellationToken ct)
    {
        var center = await db.Cards.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RiftboundId == cardId, ct);
        if (center is null) return null;

        var mechanics = new List<GraphMechanicGroup>();
        foreach (var mechanic in (center.Mechanics ?? []).Take(6))
        {
            var sharing = await db.Cards.AsNoTracking()
                .Where(c => c.RiftboundId != cardId && c.VariantOf == null &&
                            c.Mechanics != null && c.Mechanics.Contains(mechanic))
                .OrderBy(c => c.Name)
                .Take(MaxSharingCards)
                .Select(c => new GraphNode(c.RiftboundId, c.Name))
                .ToListAsync(ct);
            mechanics.Add(new(mechanic, sharing));
        }

        var rows = await db.CardInteractions.AsNoTracking()
            .Where(x => x.CardAId == cardId || x.CardBId == cardId)
            .Take(MaxInteractions)
            .ToListAsync(ct);
        var otherIds = rows.Select(x => x.CardAId == cardId ? x.CardBId : x.CardAId).ToList();
        var names = await db.Cards.AsNoTracking()
            .Where(c => otherIds.Contains(c.RiftboundId))
            .ToDictionaryAsync(c => c.RiftboundId, c => c.Name, ct);
        var interactions = rows.Select(x =>
        {
            var otherId = x.CardAId == cardId ? x.CardBId : x.CardAId;
            return new GraphInteraction(otherId, names.GetValueOrDefault(otherId, otherId), x.Kind);
        }).ToList();

        var canonical = CardText.CanonicalId(center);
        var banned = await BanLookup.BannedCanonicalIdsAsync(db, ct);
        var facts = new List<GraphFact>();
        if (banned.Contains(canonical)) facts.Add(new("Ban", $"{center.Name} staat op de banlijst"));
        var erratum = await db.Errata.AsNoTracking()
            .Where(e => e.CardRiftboundId == cardId)
            .OrderByDescending(e => e.DetectedAt)
            .Select(e => e.NewText)
            .FirstOrDefaultAsync(ct);
        if (erratum is not null) facts.Add(new("Errata", erratum));

        return new(
            new GraphNode(center.RiftboundId, center.Name),
            center.Domains, mechanics, [], interactions, facts, "postgres");
    }

    /// <summary>Bewijsketen: hoe hangen twee kaarten samen? Levert het kortste
    /// pad met de relatienamen, zodat de UI "A —HAS_MECHANIC→ Deflect
    /// ←HAS_MECHANIC— B" kan tonen in plaats van alleen een percentage.</summary>
    public async Task<IReadOnlyList<GraphPath>> PathsAsync(
        string fromCardId, string toCardId, int maxLength = 4, CancellationToken ct = default)
    {
        try
        {
            await using var session = driver.AsyncSession();
            var cursor = await session.RunAsync(
                $$"""
                MATCH (a:Card {id: $from}), (b:Card {id: $to})
                MATCH p = shortestPath((a)-[*1..{{Math.Clamp(maxLength, 1, 5)}}]-(b))
                RETURN [n IN nodes(p) | coalesce(n.name, n.code, n.title, n.id)] AS nodes,
                       [r IN relationships(p) | type(r)] AS rels
                LIMIT 3
                """,
                new Dictionary<string, object> { ["from"] = fromCardId, ["to"] = toCardId });

            return (await cursor.ToListAsync())
                .Select(r => new GraphPath(
                    r["nodes"].As<List<object>>().Select(n => n.As<string>()).ToList(),
                    r["rels"].As<List<object>>().Select(n => n.As<string>()).ToList()))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Graph-pad niet beschikbaar");
            return [];
        }
    }

    /// <summary>GraphRAG-uitbreiding voor /ask: vector levert kandidaat-secties
    /// en genoemde kaarten, de graaf voegt de bovenliggende regels en de
    /// afgeleide kaart↔regel-verbanden toe. Best-effort — zonder graaf werkt
    /// /ask gewoon door op de vector-retrieval.</summary>
    public async Task<(IReadOnlyList<string> ExtraSections, IReadOnlyList<string> Notes)>
        ExpandForAskAsync(
            IReadOnlyCollection<string> sectionCodes,
            IReadOnlyCollection<string> cardIds,
            CancellationToken ct = default)
    {
        if (sectionCodes.Count == 0 && cardIds.Count == 0) return ([], []);
        try
        {
            await using var session = driver.AsyncSession();
            var extra = new List<string>();
            var notes = new List<string>();

            if (sectionCodes.Count > 0)
            {
                var cursor = await session.RunAsync(
                    """
                    UNWIND $codes AS code
                    MATCH (p:RuleSection)-[:PARENT_OF]->(:RuleSection {code: code})
                    RETURN DISTINCT p.code AS code LIMIT 8
                    """,
                    new Dictionary<string, object>
                    {
                        ["codes"] = sectionCodes.Select(c => (object)c).ToList(),
                    });
                extra.AddRange((await cursor.ToListAsync()).Select(r => r["code"].As<string>()));
            }

            if (cardIds.Count > 0)
            {
                var cursor = await session.RunAsync(
                    """
                    UNWIND $ids AS id
                    MATCH (c:Card {id: id})-[g:GOVERNED_BY]->(r:RuleSection)
                    RETURN DISTINCT c.name AS card, r.code AS code, g.via AS via LIMIT 10
                    """,
                    new Dictionary<string, object>
                    {
                        ["ids"] = cardIds.Select(c => (object)c).ToList(),
                    });
                foreach (var row in await cursor.ToListAsync())
                {
                    var code = row["code"].As<string>();
                    extra.Add(code);
                    notes.Add($"{row["card"].As<string>()} valt onder §{code} via mechaniek {row["via"].As<string?>() ?? "?"}");
                }
            }

            return (extra.Distinct().Except(sectionCodes).Take(8).ToList(), notes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Graph-uitbreiding overgeslagen");
            return ([], []);
        }
    }
}
