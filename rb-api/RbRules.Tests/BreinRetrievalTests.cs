using Microsoft.Extensions.Logging.Abstractions;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase ask-retrieval (#228): de wiring van de fase-4 RetrievalOrchestrator
/// achter de DEFAULT-UIT feature-flag, de context-verrijking en de nette terugval.
/// PURE, testbare logica — de echte Neo4j/pgvector-adapters zijn een
/// integratie-follow-up; hier draaien in-memory poort-fakes.</summary>
public class BreinRetrievalTests
{
    // ── Fakes (spiegel van GraphRagOrchestratorTests) ──

    private static Gazetteer Gaz() => Gazetteer.Build(
    [
        new(BrainRef.Mechanic("Empowered"), "Empowered", []),
        new(BrainRef.Mechanic("Might"), "Might", []),
        new(BrainRef.Concept("showdown"), "Showdown", []),
        new(BrainRef.Mechanic("Exhaust"), "Exhaust", []),
    ]);

    private sealed class FakeGazetteer(Gazetteer g) : IGazetteerSource
    {
        public bool Called;
        public Task<Gazetteer> BuildAsync(CancellationToken ct = default)
        { Called = true; return Task.FromResult(g); }
    }

    private sealed class ThrowingGazetteer : IGazetteerSource
    {
        public Task<Gazetteer> BuildAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("gazetteer weg");
    }

    /// <summary>Honoreert annulering (zoals de echte Neo4j/EF-adapters): gooit een
    /// OperationCanceledException zodra het token geannuleerd is.</summary>
    private sealed class CancelAwareGazetteer(Gazetteer g) : IGazetteerSource
    {
        public Task<Gazetteer> BuildAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(g);
        }
    }

    private sealed class FakeSimilarity : INodeContextSimilarity
    {
        public Task<Func<BrainRef, double>> ForQuestionAsync(string q, CancellationToken ct = default) =>
            Task.FromResult<Func<BrainRef, double>>(_ => 0.0);
    }

    private sealed class FakeAdjacency : INodeAdjacency
    {
        public Task<Func<BrainRef, BrainRef, bool>> ForCandidatesAsync(
            IReadOnlyList<BrainRef> c, CancellationToken ct = default) =>
            Task.FromResult<Func<BrainRef, BrainRef, bool>>((_, _) => true);
    }

    private sealed class FakeRetriever(RetrievalResult? drift = null, RetrievalResult? local = null)
        : IGraphRetriever
    {
        public bool AnyCalled;
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { AnyCalled = true; return Task.FromResult(local ?? RetrievalResult.Empty); }
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default)
        { AnyCalled = true; return Task.FromResult(RetrievalResult.Empty); }
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { AnyCalled = true; return Task.FromResult(RetrievalResult.Empty); }
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { AnyCalled = true; return Task.FromResult(drift ?? RetrievalResult.Empty); }
    }

    private sealed class ThrowingRetriever : IGraphRetriever
    {
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
            => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default)
            => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
            => throw new InvalidOperationException("Neo4j weg");
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
            => throw new InvalidOperationException("Neo4j weg");
    }

    private static RetrievalResult OfficialChunk(BrainRef r, string text) =>
        new([], [], [new RetrievedChunk(r, KnowledgeTier.Official, text, 0.9, TrustVector.OfficialDefault)], [], []);

    /// <summary>Retriever die een NIET-leeg trust-gewogen pad teruggeeft op de
    /// Path-modus — zodat de pad-onderbouwing + PathCitation-formattering (§4 "het pad
    /// ÍS de uitleg") daadwerkelijk door een test loopt.</summary>
    private sealed class PathRetriever(RetrievalResult pathResult) : IGraphRetriever
    {
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(RetrievalResult.Empty);
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default) => Task.FromResult(RetrievalResult.Empty);
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(pathResult);
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default) => Task.FromResult(RetrievalResult.Empty);
    }

    private static RetrievalOrchestrator Orch(IGraphRetriever retriever, IGazetteerSource? gaz = null) =>
        new(gaz ?? new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

    private static BreinRetrievalService Service(
        IGraphRetriever retriever, BreinRetrievalSettings settings, IGazetteerSource? gaz = null) =>
        new(Orch(retriever, gaz), TestSettings.Fixed(settings), NullLogger<BreinRetrievalService>.Instance);

    // ── 1) Flag-parsing: default UIT, alleen expliciet aan-woord telt ──

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("onzin", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("on", true)]
    [InlineData("Enabled", true)]
    [InlineData("yes", true)]
    public void Gate_Parse_DefaultUit_AlleenExplicietAan(string? raw, bool expected) =>
        Assert.Equal(expected, BreinRetrievalGate.Parse(raw));

    [Fact]
    public void Settings_Disabled_IsDeVeiligeDefault()
    {
        Assert.False(BreinRetrievalSettings.Disabled.Enabled);
    }

    // ── 2) Flag UIT ⇒ geen brein-call, geen verrijking ──

    [Fact]
    public async Task Disabled_EnrichAsync_NullEnRaaktDeOrchestratorNooitAan()
    {
        var gaz = new FakeGazetteer(Gaz());
        var retriever = new FakeRetriever(drift: OfficialChunk(BrainRef.Section("core", "7.3"), "regel"));
        var svc = new BreinRetrievalService(
            new RetrievalOrchestrator(gaz, new FakeSimilarity(), new FakeAdjacency(), retriever),
            TestSettings.Fixed(BreinRetrievalSettings.Disabled), NullLogger<BreinRetrievalService>.Instance);

        var result = await svc.EnrichAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling);

        Assert.Null(result);
        // De flag-uit-poort mag geen enkele poort raken (geen extra latency/IO).
        Assert.False(gaz.Called);
        Assert.False(retriever.AnyCalled);
    }

    // ── 3) Flag AAN ⇒ context verrijkt + AnswerTrace opgebouwd ──

    [Fact]
    public async Task Enabled_EnrichAsync_VerrijktContextEnBouwtAnswerTrace()
    {
        var retriever = new FakeRetriever(
            drift: OfficialChunk(BrainRef.Section("core", "7.3"), "showdown timing regel"));
        var svc = Service(retriever, new BreinRetrievalSettings(Enabled: true));

        var result = await svc.EnrichAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling);

        Assert.NotNull(result);
        // Verrijkt blok met machine-leesbaar trust-label en gating-beslissing.
        Assert.Contains("BREIN-CONTEXT", result!.PromptBlock);
        Assert.Contains("[OFFICIEEL]", result.PromptBlock);
        Assert.Contains("showdown timing regel", result.PromptBlock);
        Assert.Contains("[gating: Official]", result.PromptBlock);
        // AnswerTrace verantwoordt met de dragende feiten + epoch-stempels.
        Assert.NotEmpty(result.Outcome.Trace.Supports);
        Assert.Equal("bge-m3", result.Outcome.Trace.EmbeddingRev);
        Assert.Equal("ask-graphrag-v1", result.Outcome.Trace.PromptVersion);
    }

    // ── 4) Retrieval-fout ⇒ nette terugval (null), nooit een exception ──

    [Fact]
    public async Task Enabled_RetrieverGooit_ValtTerugNaarNull_GeenException()
    {
        var svc = Service(new ThrowingRetriever(), new BreinRetrievalSettings(Enabled: true));

        var result = await svc.EnrichAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling);

        Assert.Null(result); // AskService valt hierop terug op de bestaande flow
    }

    [Fact]
    public async Task Enabled_GazetteerGooit_ValtTerugNaarNull_GeenException()
    {
        var svc = Service(
            new FakeRetriever(), new BreinRetrievalSettings(Enabled: true), new ThrowingGazetteer());

        var result = await svc.EnrichAsync("Wat betekent Exhaust?", QuestionType.Definitie);

        Assert.Null(result);
    }

    // ── 5) Client-abort bubbelt WÉL door (niet maskeren) ──

    [Fact]
    public async Task Enabled_ClientAbort_Propageert()
    {
        // De echte adapters gooien OCE op een geannuleerd token; EnrichAsync maskeert
        // die bewust niet (de vrager haakte zelf af).
        var svc = new BreinRetrievalService(
            new RetrievalOrchestrator(
                new CancelAwareGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(),
                new FakeRetriever()),
            TestSettings.Fixed(new BreinRetrievalSettings(Enabled: true)), NullLogger<BreinRetrievalService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.EnrichAsync("Wat betekent Exhaust?", QuestionType.Definitie, cts.Token));
    }

    // ── 6) Formatter: leeg outcome ⇒ leeg blok (prompt blijft de bestaande) ──

    [Fact]
    public async Task Formatter_GeenDragendFeit_LeegBlok()
    {
        var svc = Service(new FakeRetriever(), new BreinRetrievalSettings(Enabled: true));
        // "Wat betekent Exhaust?" → Local, retriever leeg → geen chunks/paden.
        var result = await svc.EnrichAsync("Wat betekent Exhaust?", QuestionType.Definitie);

        Assert.NotNull(result);
        Assert.Equal("", result!.PromptBlock); // leeg blok ⇒ prompt byte-identiek
    }

    // ── 7) Formatter: latency-terugval expliciet zichtbaar in het blok ──

    [Fact]
    public void Formatter_LatencyTerugval_ZichtbaarInBlok()
    {
        // Deterministische LATE latency-terugval (na de dure fase): onder budget bij
        // de vóór-poort (100ms), erover bij de late check (5000ms). De DRIFT-chunk
        // overleeft, de paden worden geknepen, en de terugval wordt in het blok gemeld.
        var orch = Orch(new FakeRetriever(
            drift: OfficialChunk(BrainRef.Section("core", "7.3"), "regel")));
        var calls = 0;
        var outcome = orch.RetrieveAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling,
            new GraphRagContext(LatencyBudget: new LatencyBudget(1000)),
            elapsedMsOverride: () => ++calls == 1 ? 100 : 5000).GetAwaiter().GetResult();

        var block = BreinContextFormatter.Format(outcome);

        Assert.Contains("BREIN-CONTEXT", block);
        Assert.Contains("retrieval-terugval: " + RetrievalFallback.LatencyExceeded, block);
    }

    // ── 8) Formatter: NIET-leeg pad ⇒ pad-onderbouwing + PathCitations in het blok ──

    [Fact]
    public async Task Formatter_NietLeegPad_RendertOnderbouwingEnPadCitaties()
    {
        // Een causale vraag met 2 ankers → Path-modus; de retriever levert één
        // trust-gewogen pad (Card —GOVERNED_BY→ §-sectie). Dit is de ENIGE test die
        // de pad-onderbouwing-lus én de PathCitation-formattering met widget-markers
        // (§4 "het pad ÍS de uitleg") echt uitvoert — een regressie daarin (verkeerde
        // citation-nummering, null-deref op WidgetMarker, kapotte Explain) faalt hier.
        var start = new GraphNode(BrainRef.Card("Poro Herald"), KnowledgeTier.Official, "Poro Herald");
        var section = new GraphNode(
            BrainRef.Section("core", "7.3"), KnowledgeTier.Official, "§7.3 Showdown timing");
        var edge = new GraphEdge(start.Ref, section.Ref, "GOVERNED_BY", 0.9);
        var path = new GraphPath(start, [new PathHop(section, edge, 0.9, 0.9)]);
        var pathResult = new RetrievalResult([start, section], [edge], [], [path], []);

        var orch = Orch(new PathRetriever(pathResult));
        var outcome = await orch.RetrieveAsync(
            "Waarom verliest Empowered van Might?", QuestionType.Ruling);

        // De orchestrator moet het pad daadwerkelijk als citaties hebben opgenomen
        // (anders test de formatter een lege lijst en bewijst dit niets).
        Assert.NotEmpty(outcome.Retrieval.Paths);
        Assert.NotEmpty(outcome.PathCitations);

        var block = BreinContextFormatter.Format(outcome);

        // De leesbare pad-onderbouwing (Explain): start-label + edge-type + doel-label.
        Assert.Contains("pad-onderbouwing: Poro Herald —GOVERNED_BY→ §7.3 Showdown timing", block);
        // De pad-knoop-citaties met hun widget-markers (Card → [[card:label]],
        // Section → [[rule:code]]) en het trust-label.
        Assert.Contains("[[card:Poro Herald]]", block);
        Assert.Contains("[[rule:7.3]]", block);
        Assert.Contains("[cit:1]", block);
        Assert.Contains("[cit:2]", block);
        Assert.Contains("[OFFICIEEL]", block);
    }
}
