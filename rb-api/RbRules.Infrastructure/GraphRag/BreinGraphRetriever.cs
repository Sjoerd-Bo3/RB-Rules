using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Infrastructure.GraphRag;

/// <summary>De vier retrieval-modi (§4) tegen de live Neo4j + pgvector
/// (INTEGRATIE-FOLLOW-UP, verifieerbaar bij de eerste run met flag aan). Alle modi
/// zijn idempotent, zij-effect-vrij en degraderen bij uitval naar
/// <see cref="RetrievalResult.Empty"/> — nooit een exception naar de orchestrator.
///
/// Reïkt de bestaande patronen: getypeerde subgraaf-expansie via ongerichte
/// 1-hop-buren (zoals <see cref="BrainGraphService.NeighborsAsync"/>), k-shortest via
/// <c>shortestPath</c>, DRIFT-seeds via pgvector-cosine op de canonieke entiteiten en
/// Global uit de reeds-gesynthetiseerde primer-dossiers (§4: community-summaries =
/// hergebruikte primer, geen tweede synthese-laag). De labels die de Cypher in gaan
/// komen uitsluitend uit <see cref="BrainQuery.GraphLabel"/> (vaste enum-mapping) en
/// de vooraf-geclampte pad-lengte — nooit gebruikerstekst.</summary>
public sealed class BreinGraphRetriever(
    IDriver driver,
    IDbContextFactory<RbRulesDbContext> dbFactory,
    EmbeddingService embeddings,
    ILogger<BreinGraphRetriever> logger) : IGraphRetriever
{
    private const int MaxAnchors = 4;
    private const int NeighborsPerAnchor = 12;
    private const int DriftSeeds = 6;

    // Naam- en tekst-coalesce per Cypher-variabele (geen gebruikerstekst — vaste
    // property-lijst, zelfde afspraak als BrainGraphService.NameCoalesce).
    private static string Name(string v) =>
        $"coalesce({v}.name, {v}.title, {v}.code, {v}.cardName, {v}.statement, {v}.text, {v}.label, toString({v}.id))";
    private static string Text(string v) =>
        $"coalesce({v}.text, {v}.definition, {v}.statement, {v}.ruleText, '')";

    public async Task<RetrievalResult> LocalAsync(
        IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default)
    {
        try
        {
            return await ExpandAsync(anchors, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local-retrieval mislukt — leeg (brein-retrieval degradeert)");
            return RetrievalResult.Empty;
        }
    }

    public async Task<RetrievalResult> DriftAsync(
        string question, IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default)
    {
        try
        {
            // Seed (vector) → anchor-fusie: de vector-buren aangevuld met de gelinkte
            // ankers, daarna typed-edge-expansie (§4). Seeds zonder graaf-verankering
            // degraderen vanzelf: staan ze niet in de graaf, dan levert de expansie niets.
            var seeds = await VectorSeedsAsync(question, ct).ConfigureAwait(false);
            var union = anchors.Concat(seeds).DistinctBy(r => r.Format()).ToList();
            return await ExpandAsync(union, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DRIFT-retrieval mislukt — leeg (brein-retrieval degradeert)");
            return RetrievalResult.Empty;
        }
    }

    public async Task<RetrievalResult> PathAsync(
        IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default)
    {
        if (anchors.Count < 2) return RetrievalResult.Empty;
        try
        {
            var maxLen = Math.Clamp(mode.KHops <= 0 ? 2 : mode.KHops, 2, 4);
            var path = await ShortestPathAsync(anchors[0], anchors[^1], maxLen, ct).ConfigureAwait(false);
            return path is null
                ? RetrievalResult.Empty
                : new RetrievalResult([.. path.Nodes], [], [], [path], []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Path-retrieval mislukt — leeg (brein-retrieval degradeert)");
            return RetrievalResult.Empty;
        }
    }

    public async Task<RetrievalResult> GlobalAsync(
        string question, ModeSelection mode, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Community-summaries = hergebruikte, goedgekeurde primer-dossiers (§4).
            var rows = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer" && k.Status == "approved"
                    && EF.Functions.ToTsVector("english", k.Title + " " + k.Body)
                        .Matches(EF.Functions.PlainToTsQuery("english", question)))
                .OrderByDescending(k => EF.Functions.ToTsVector("english", k.Title + " " + k.Body)
                    .Rank(EF.Functions.PlainToTsQuery("english", question)))
                .Take(5)
                .Select(k => new { k.Topic, k.Title, k.Body })
                .ToListAsync(ct).ConfigureAwait(false);

            var communities = rows.Select((r, i) => new CommunitySummary(
                CommunityId: $"primer:{r.Topic}",
                Level: 0,
                Title: r.Title,
                Text: r.Body,
                Ref: BrainRef.Concept(r.Topic),
                Tier: KnowledgeTier.Primer,
                Relevance: 0.8 - i * 0.05)).ToList();
            return new RetrievalResult([], [], [], [], communities);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Global-retrieval mislukt — leeg (brein-retrieval degradeert)");
            return RetrievalResult.Empty;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Getypeerde 1-hop-expansie rond de seed-refs: knopen + edges + de
    /// tekst-dragende knopen als chunks. Dedupt op ref/edge zodat één knoop niet
    /// dubbel in de bundel komt.</summary>
    private async Task<RetrievalResult> ExpandAsync(IReadOnlyList<BrainRef> seeds, CancellationToken ct)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var chunks = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);

        await using var session = driver.AsyncSession();
        foreach (var seed in seeds.Take(MaxAnchors))
        {
            if (BrainQuery.GraphLabel(seed.Kind) is not { } label) continue;

            var cypher = $$"""
                MATCH (n:{{label}} {ref: $ref})
                OPTIONAL MATCH (n)-[r]-(m)
                RETURN n.ref AS nRef, {{Name("n")}} AS nName, {{Text("n")}} AS nText, labels(n) AS nLabels,
                       m.ref AS mRef, {{Name("m")}} AS mName, {{Text("m")}} AS mText, labels(m) AS mLabels,
                       type(r) AS edge, coalesce(r.confidence, 0.7) AS conf,
                       CASE WHEN r IS NULL THEN null
                            WHEN elementId(startNode(r)) = elementId(n) THEN 'uit'
                            ELSE 'in' END AS dir
                LIMIT $take
                """;
            var cursor = await session.RunAsync(cypher, new Dictionary<string, object>
            {
                ["ref"] = seed.Format(),
                ["take"] = (long)NeighborsPerAnchor,
            });

            foreach (var rec in await cursor.ToListAsync(ct))
            {
                AddNode(nodes, chunks, rec["nRef"].As<string?>(), rec["nName"].As<string?>(),
                    rec["nText"].As<string?>(), rec["nLabels"].As<List<string>>());

                if (rec["edge"].As<string?>() is not { } edgeType) continue; // r = null-rij
                var mRef = rec["mRef"].As<string?>();
                AddNode(nodes, chunks, mRef, rec["mName"].As<string?>(),
                    rec["mText"].As<string?>(), rec["mLabels"].As<List<string>>());

                if (!BrainRef.TryParse(rec["nRef"].As<string?>(), out var nParsed)
                    || !BrainRef.TryParse(mRef, out var mParsed)) continue;
                var uit = rec["dir"].As<string?>() != "in";
                var from = uit ? nParsed : mParsed;
                var to = uit ? mParsed : nParsed;
                var conf = rec["conf"].As<double>();
                var key = $"{from.Format()}|{edgeType}|{to.Format()}";
                edges.TryAdd(key, new GraphEdge(from, to, edgeType, Math.Clamp(conf, 0, 1)));
            }
        }

        return new RetrievalResult([.. nodes.Values], [.. edges.Values], [.. chunks.Values], [], []);
    }

    private static void AddNode(
        Dictionary<string, GraphNode> nodes, Dictionary<string, RetrievedChunk> chunks,
        string? refValue, string? name, string? text, IReadOnlyList<string>? labels)
    {
        if (!BrainRef.TryParse(refValue, out var parsed)) return;
        var key = parsed.Format();
        var tier = TierFor(labels);
        var label = string.IsNullOrWhiteSpace(name) ? parsed.Key : name!;
        nodes.TryAdd(key, new GraphNode(parsed, tier, label, string.IsNullOrWhiteSpace(text) ? null : text));

        if (!string.IsNullOrWhiteSpace(text) && !chunks.ContainsKey(key))
            chunks[key] = new RetrievedChunk(
                parsed, tier, text!, Relevance: 0.6, DefaultTrust(tier));
    }

    private async Task<GraphPath?> ShortestPathAsync(
        BrainRef from, BrainRef to, int maxLen, CancellationToken ct)
    {
        await using var session = driver.AsyncSession();
        var cypher = $$"""
            MATCH (a {ref: $from}), (b {ref: $to})
            MATCH p = shortestPath((a)-[*..{{maxLen}}]-(b))
            RETURN [n IN nodes(p) | {ref: n.ref, name: {{Name("n")}}, labels: labels(n),
                                     text: {{Text("n")}}}] AS ns,
                   [r IN relationships(p) | {t: type(r), c: coalesce(r.confidence, 0.7)}] AS rs
            LIMIT 1
            """;
        var cursor = await session.RunAsync(cypher, new Dictionary<string, object>
        {
            ["from"] = from.Format(),
            ["to"] = to.Format(),
        });
        var records = await cursor.ToListAsync(ct);
        if (records.Count == 0) return null;

        var rec = records[0];
        var ns = rec["ns"].As<List<Dictionary<string, object>>>();
        var rs = rec["rs"].As<List<Dictionary<string, object>>>();
        if (ns.Count == 0 || rs.Count != ns.Count - 1) return null;

        GraphNode NodeAt(int i)
        {
            var d = ns[i];
            BrainRef.TryParse(d.GetValueOrDefault("ref")?.ToString(), out var r);
            var labels = (d.GetValueOrDefault("labels") as List<object>)?.Select(o => o.ToString() ?? "").ToList();
            var tier = TierFor(labels);
            var name = d.GetValueOrDefault("name")?.ToString();
            var text = d.GetValueOrDefault("text")?.ToString();
            return new GraphNode(r, tier, string.IsNullOrWhiteSpace(name) ? r.Key : name!,
                string.IsNullOrWhiteSpace(text) ? null : text);
        }

        var start = NodeAt(0);
        var steps = new List<PathHop>(rs.Count);
        for (var i = 0; i < rs.Count; i++)
        {
            var target = NodeAt(i + 1);
            var edgeType = rs[i].GetValueOrDefault("t")?.ToString() ?? "RELATES_TO";
            var conf = rs[i].GetValueOrDefault("c") is { } c && double.TryParse(c.ToString(), out var cv) ? cv : 0.7;
            conf = Math.Clamp(conf, 0, 1);
            var edge = new GraphEdge(NodeAt(i).Ref, target.Ref, edgeType, conf);
            steps.Add(new PathHop(target, edge, target.EffectiveTrust.Weight, conf));
        }
        return new GraphPath(start, steps);
    }

    private async Task<IReadOnlyList<BrainRef>> VectorSeedsAsync(string question, CancellationToken ct)
    {
        try
        {
            var qv = await embeddings.EmbedOneAsync(question, ct).ConfigureAwait(false);
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.CanonicalEntities.AsNoTracking()
                .Where(e => e.Status != CanonicalEntityStatus.Merged && e.Embedding != null)
                .OrderBy(e => e.Embedding!.CosineDistance(qv))
                .Take(DriftSeeds)
                .Select(e => new { e.Kind, e.CanonicalLabel })
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. rows.Select(r => r.Kind switch
            {
                CanonicalEntityKinds.Mechanic => BrainRef.Mechanic(r.CanonicalLabel),
                CanonicalEntityKinds.Concept => BrainRef.Concept(r.CanonicalLabel),
                _ => BrainRef.Tag(r.CanonicalLabel),
            })];
        }
        catch (Exception ex)
        {
            // Embedding-uitval: DRIFT valt terug op alleen de gelinkte ankers.
            logger.LogWarning(ex, "DRIFT-vector-seeds mislukt — alleen ankers als seed");
            return [];
        }
    }

    /// <summary>Neo4j-label(s) → kennispiramide-tier. Prioriteit officieel → ruling →
    /// community → meta (defensieve default). Bewust conservatief: gemijnde/afgeleide
    /// knopen (Interaction/Claim) dragen géén officieel gezag, zodat de trust-gating
    /// (#229) ze nooit een officiële sectie laat verdringen.</summary>
    private static KnowledgeTier TierFor(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0) return KnowledgeTier.Meta;
        if (labels.Any(l => l is "Card" or "Mechanic" or "Concept" or "RuleSection"
            or "Set" or "Domain" or "Tag" or "Source"))
            return KnowledgeTier.Official;
        if (labels.Any(l => l is "Ruling" or "Erratum")) return KnowledgeTier.VerifiedRuling;
        if (labels.Any(l => l is "Claim")) return KnowledgeTier.Community;
        return KnowledgeTier.Meta;
    }

    private static TrustVector DefaultTrust(KnowledgeTier tier) => tier switch
    {
        KnowledgeTier.Official => TrustVector.OfficialDefault,
        KnowledgeTier.VerifiedRuling => TrustVector.For(tier, Verification.HumanApproved),
        _ => TrustVector.For(tier, Verification.Unverified),
    };
}
