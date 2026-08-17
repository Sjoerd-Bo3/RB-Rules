using System.Diagnostics;

namespace RbRules.Domain.GraphRag;

/// <summary>Omgevings-context voor één retrieval-run: het token-budget van de
/// bundel, of de GDS-named-graph warm staat (beslissing #232), het latency-budget
/// en de epoch-stempels voor de trace. Bewust een los record zodat de orchestrator
/// puur-composeerbaar blijft en tests hem exact kunnen sturen.</summary>
public sealed record GraphRagContext(
    int TokenBudget = 6000,
    bool GdsWarm = true,
    LatencyBudget? LatencyBudget = null,
    TraceEpoch? Epoch = null);

/// <summary>De volledige uitkomst van de GraphRAG-retrieval: alle tussenstappen
/// expliciet (inzicht #236 — niets verdwijnt in onzichtbare state). AskService
/// bouwt hieruit de prompt-context, de citaties/widgets en schrijft de
/// <see cref="Trace"/> weg.</summary>
public sealed record GraphRagOutcome(
    IReadOnlyList<LinkDecision> Links,
    IReadOnlyList<BrainRef> Anchors,
    double Beta,
    bool GraphChannelLeads,
    ModeSelection Mode,
    RetrievalResult Retrieval,
    ContextBundle Bundle,
    IReadOnlyList<PathCitation> PathCitations,
    TrustGateDecision Gate,
    NoPathSignal? NoPath,
    string? FallbackReason,
    AnswerTrace Trace);

/// <summary>De <c>RetrievalOrchestrator</c> (§4): de flat fan-out van de oude /ask
/// vervangen door één gecoördineerde pijplijn — entity-linking → β(q)-router →
/// modus-selectie → begrotings-poort → retrieval-modi → context-bundeling →
/// trust-gating → AnswerTrace. De IO (gazetteer, embedding-cosine, graaf-adjacency,
/// de vier retriever-modi) loopt via poorten waarvan de Neo4j/pgvector/GDS-
/// implementaties een gedocumenteerde integratie-follow-up zijn; ALLE beslislogica
/// is de pure, geteste helper-laag (<see cref="BetaRouter"/>, <see cref="ModeSelector"/>,
/// <see cref="RetrievalGuard"/>, <see cref="ContextBundler"/>, <see cref="TrustGate"/>,
/// <see cref="AnswerTraceBuilder"/>). Poort-uitval (null/leeg) degradeert netjes —
/// nooit een kale 500 (§7).</summary>
public sealed class RetrievalOrchestrator(
    IGazetteerSource gazetteerSource,
    INodeContextSimilarity nodeSimilarity,
    INodeAdjacency adjacency,
    IGraphRetriever retriever)
{
    public async Task<GraphRagOutcome> RetrieveAsync(
        string question, QuestionType type, GraphRagContext? context = null,
        Func<double>? elapsedMsOverride = null, CancellationToken ct = default)
    {
        var ctx = context ?? new GraphRagContext();
        var budget = ctx.LatencyBudget ?? LatencyBudget.Default;
        var sw = Stopwatch.StartNew();
        double Elapsed() => elapsedMsOverride?.Invoke() ?? sw.Elapsed.TotalMilliseconds;

        // 1) Entity-linking (fundament, §4).
        var gazetteer = await gazetteerSource.BuildAsync(ct).ConfigureAwait(false);
        var mentions = MentionDetector.Detect(question, gazetteer);
        var cosine = await nodeSimilarity.ForQuestionAsync(question, ct).ConfigureAwait(false);
        var candidateRefs = mentions.SelectMany(m => m.Candidates.Select(c => c.Ref)).Distinct().ToList();
        var connected = await adjacency.ForCandidatesAsync(candidateRefs, ct).ConfigureAwait(false);
        var links = EntityLinker.Link(mentions, cosine, connected);
        var anchors = EntityLinker.Anchors(links);

        // 2) β(q)-router (OMD-GraphRAG, §4).
        var signals = QuestionSignals.From(
            anchors.Count, RetrievalCues.ContentWordCount(question),
            RetrievalCues.AbstractionCueCount(question));
        var beta = BetaRouter.Beta(signals);

        // 3) Modus-selectie achter de vraag-router (§4-tabel).
        var mode = ModeSelector.Select(type, question, anchors.Count);

        // 4) Begrotings-poort (beslissing #232): GDS-warmte + latency.
        mode = RetrievalGuard.Apply(mode, ctx.GdsWarm, Elapsed(), budget, out var fallbackReason);

        // 5) Retrieval-modi uitvoeren (netjes degraderend bij uitval).
        var retrieval = await RunModeAsync(question, anchors, mode, ct).ConfigureAwait(false);

        // 5b) Late latency-check: overschreden ná de dure fase → markeer terugval en
        //     knijp tot Local-only-materiaal (paden vallen weg uit de bundel).
        if (fallbackReason is null && !RetrievalGuard.WithinBudget(Elapsed(), budget))
        {
            fallbackReason = RetrievalFallback.LatencyExceeded;
            mode = mode.ToLocalOnly(RetrievalFallback.LatencyExceeded);
            retrieval = retrieval with { Paths = [] };
        }

        // 6) Context-bundeling (§4): chunks + community-summaries → trust-geordend,
        //    gebudgetteerd, gelabeld.
        var items = new List<BundleItem>();
        items.AddRange(retrieval.Chunks.Select(c => BundleItem.From(c)));
        items.AddRange(retrieval.Communities.Select(BundleItem.From));
        var bundle = ContextBundler.Bundle(items, ctx.TokenBudget);

        // 7) Pad → citatie (§4): paden worden de structurele citaties, ná de
        //    chunk-citaties genummerd.
        var pathCitations = mode.UsePath || mode.Primary == RetrievalMode.Path
            ? PathCitations.Build(retrieval.Paths, startId: bundle.Items.Count + 1)
            : [];

        // 8) NoPath-signaal (§4): pad verwacht maar niet gevonden → eerlijk geen
        //    interactie, voeding voor KnowledgeGaps.
        NoPathSignal? noPath = null;
        if ((mode.UsePath || mode.Primary == RetrievalMode.Path)
            && retrieval.Paths.Count == 0 && anchors.Count >= 2)
            noPath = NoPathSignal.For(anchors);

        // 9) Trust-gating (beslissing #229): route op officiële dekking.
        var trustCandidates = new List<TrustCandidate>(ContextBundler.ToTrustCandidates(bundle.Items.Select(i => i.Item)));
        trustCandidates.AddRange(pathCitations.Select(p => new TrustCandidate(p.Tier, p.TrustWeight)));
        var gate = TrustGate.Decide(trustCandidates);

        // 10) AnswerTrace (§6/#236).
        var trace = AnswerTraceBuilder.Build(
            question, type, mode, beta, gate, bundle, pathCitations, ctx.Epoch, fallbackReason);

        return new GraphRagOutcome(
            links, anchors, beta, BetaRouter.GraphChannelLeads(beta), mode,
            retrieval, bundle, pathCitations, gate, noPath, fallbackReason, trace);
    }

    /// <summary>Voer de gekozen modus (+ eventuele aanvullende kanalen) uit en
    /// verenig de deelresultaten. Direct = geen graaf (BanLookup buiten de
    /// orchestrator).</summary>
    private async Task<RetrievalResult> RunModeAsync(
        string question, IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct)
    {
        if (mode.Primary == RetrievalMode.Direct)
            return RetrievalResult.Empty;

        var results = new List<RetrievalResult>();
        switch (mode.Primary)
        {
            case RetrievalMode.Local:
                results.Add(await retriever.LocalAsync(anchors, mode, ct).ConfigureAwait(false));
                break;
            case RetrievalMode.Global:
                results.Add(await retriever.GlobalAsync(question, mode, ct).ConfigureAwait(false));
                break;
            case RetrievalMode.Path:
                results.Add(await retriever.PathAsync(anchors, mode, ct).ConfigureAwait(false));
                break;
            case RetrievalMode.Drift:
                results.Add(await retriever.DriftAsync(question, anchors, mode, ct).ConfigureAwait(false));
                break;
        }

        // Aanvullende kanalen (§4-combinaties): DRIFT naast Path, Path naast DRIFT.
        if (mode.UseDrift && mode.Primary != RetrievalMode.Drift)
            results.Add(await retriever.DriftAsync(question, anchors, mode, ct).ConfigureAwait(false));
        if (mode.UsePath && mode.Primary != RetrievalMode.Path)
            results.Add(await retriever.PathAsync(anchors, mode, ct).ConfigureAwait(false));

        return Merge(results);
    }

    /// <summary>Vereen de deelresultaten, ontdubbeld op ref/edge zodat een knoop die
    /// twee kanalen beide vinden niet dubbel in de bundel komt.</summary>
    private static RetrievalResult Merge(IReadOnlyList<RetrievalResult> parts)
    {
        if (parts.Count == 1) return parts[0];
        var nodes = parts.SelectMany(p => p.Nodes).DistinctBy(n => n.Ref.Format()).ToList();
        var edges = parts.SelectMany(p => p.Edges)
            .DistinctBy(e => $"{e.From.Format()}|{e.EdgeType}|{e.To.Format()}").ToList();
        var chunks = parts.SelectMany(p => p.Chunks).DistinctBy(c => c.Ref.Format()).ToList();
        var paths = parts.SelectMany(p => p.Paths).ToList();
        var communities = parts.SelectMany(p => p.Communities).DistinctBy(c => c.CommunityId).ToList();
        return new(nodes, edges, chunks, paths, communities);
    }
}
