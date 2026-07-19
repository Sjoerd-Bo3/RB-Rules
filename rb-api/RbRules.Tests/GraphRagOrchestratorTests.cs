using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Fase 4 (#228): de RetrievalOrchestrator end-to-end met in-memory
/// poort-fakes (de echte Neo4j/pgvector/GDS-adapters zijn een integratie-follow-up),
/// plus het AnswerTrace-auditspoor (§6/#236) en zijn Postgres-roundtrip.</summary>
public class GraphRagOrchestratorTests
{
    private static Gazetteer Gaz() => Gazetteer.Build(
    [
        new(BrainRef.Mechanic("Empowered"), "Empowered", []),
        new(BrainRef.Mechanic("Might"), "Might", []),
        new(BrainRef.Concept("showdown"), "Showdown", []),
        new(BrainRef.Mechanic("Exhaust"), "Exhaust", []),
    ]);

    // ── Fakes ──

    private sealed class FakeGazetteer(Gazetteer g) : IGazetteerSource
    {
        public Task<Gazetteer> BuildAsync(CancellationToken ct = default) => Task.FromResult(g);
    }

    private sealed class FakeSimilarity(Func<BrainRef, double>? f = null) : INodeContextSimilarity
    {
        public Task<Func<BrainRef, double>> ForQuestionAsync(string q, CancellationToken ct = default) =>
            Task.FromResult(f ?? (_ => 0.0));
    }

    private sealed class FakeAdjacency(Func<BrainRef, BrainRef, bool>? f = null) : INodeAdjacency
    {
        public Task<Func<BrainRef, BrainRef, bool>> ForCandidatesAsync(
            IReadOnlyList<BrainRef> c, CancellationToken ct = default) =>
            Task.FromResult(f ?? ((_, _) => true)); // ankers verbonden → co-mention wint
    }

    private sealed class FakeRetriever(
        RetrievalResult? local = null, RetrievalResult? global = null,
        RetrievalResult? path = null, RetrievalResult? drift = null) : IGraphRetriever
    {
        public bool LocalCalled, GlobalCalled, PathCalled, DriftCalled;
        public Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { LocalCalled = true; return Task.FromResult(local ?? RetrievalResult.Empty); }
        public Task<RetrievalResult> GlobalAsync(string q, ModeSelection m, CancellationToken ct = default)
        { GlobalCalled = true; return Task.FromResult(global ?? RetrievalResult.Empty); }
        public Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { PathCalled = true; return Task.FromResult(path ?? RetrievalResult.Empty); }
        public Task<RetrievalResult> DriftAsync(string q, IReadOnlyList<BrainRef> a, ModeSelection m, CancellationToken ct = default)
        { DriftCalled = true; return Task.FromResult(drift ?? RetrievalResult.Empty); }
    }

    private static RetrievalResult OfficialChunk(BrainRef r, string text) =>
        new([], [], [new RetrievedChunk(r, KnowledgeTier.Official, text, 0.9, TrustVector.OfficialDefault)], [], []);

    private static RetrievalResult CommunityChunk(BrainRef r, string text)
    {
        var trust = TrustVector.For(KnowledgeTier.Community, Verification.LexicallySupported,
            [new("a", 0.7, 1), new("b", 0.6, 1), new("c", 0.4, 1)], ageDays: 0);
        return new([], [], [new RetrievedChunk(r, KnowledgeTier.Community, text, 0.9, trust, 3, 3)], [], []);
    }

    private static GraphPath SamplePath() =>
        new(new GraphNode(BrainRef.Mechanic("Empowered"), KnowledgeTier.Official, "Empowered"),
        [
            new PathHop(new GraphNode(BrainRef.Concept("showdown"), KnowledgeTier.Official, "Showdown"),
                new GraphEdge(BrainRef.Mechanic("Empowered"), BrainRef.Concept("showdown"), "REQUIRES", 0.9), 0.9, 0.9),
        ]);

    // ── 1) Entity-dichte interactie → DRIFT + path, graph-kanaal leidt ──

    [Fact]
    public async Task Interactie_KiestDriftPlusPath_EnBouwtPadCitaties()
    {
        var pathResult = new RetrievalResult([], [], [], [SamplePath()], []);
        var retriever = new FakeRetriever(
            drift: OfficialChunk(BrainRef.Section("core", "7.3"), "showdown timing regel"),
            path: pathResult);
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling);

        Assert.Equal(RetrievalMode.Drift, outcome.Mode.Primary);
        Assert.True(outcome.Mode.UsePath);
        Assert.True(outcome.GraphChannelLeads);          // β → graph-kanaal
        Assert.True(retriever.DriftCalled && retriever.PathCalled);
        Assert.NotEmpty(outcome.PathCitations);           // pad → citatie
        Assert.Contains(outcome.PathCitations, c => c.WidgetMarker == "[[interaction:showdown]]" || c.Ref == BrainRef.Concept("showdown"));
        Assert.NotEmpty(outcome.Trace.Supports);          // AnswerTrace verantwoordt
    }

    // ── 2) Trust-gating: officiële dekking → officieel primair ──

    [Fact]
    public async Task Gating_OfficieleDekking_OfficieelPrimair()
    {
        var retriever = new FakeRetriever(local: OfficialChunk(BrainRef.Section("core", "1.1"), "officiele definitie"));
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync("Wat betekent Exhaust?", QuestionType.Definitie);

        Assert.Equal(RetrievalMode.Local, outcome.Mode.Primary);
        Assert.True(retriever.LocalCalled);
        Assert.Equal(PrimaryChannel.Official, outcome.Gate.Primary);
    }

    // ── 3) Trust-gating: geen officieel, sterke community → primair mét badge ──

    [Fact]
    public async Task Gating_GeenOfficieel_SterkeCommunity_PrimairMetBadge()
    {
        var retriever = new FakeRetriever(drift: CommunityChunk(BrainRef.Claim(7), "community lezing van deze interactie"));
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync("Werkt Empowered samen met Might?", QuestionType.Ruling);

        Assert.Equal(PrimaryChannel.CommunityBadged, outcome.Gate.Primary);
        Assert.True(outcome.Gate.BadgeCommunity);
    }

    // ── 4) Begrotings-fallback: GDS koud → Path-kanaal uit ──

    [Fact]
    public async Task Budget_GdsKoud_StriptPathKanaal()
    {
        var retriever = new FakeRetriever();
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync(
            "Waarom verliest Empowered de showdown van Might?", QuestionType.Ruling,
            new GraphRagContext(GdsWarm: false));

        Assert.Equal(RetrievalFallback.GdsCold, outcome.FallbackReason);
        Assert.False(outcome.Mode.UsePath);
        Assert.False(retriever.PathCalled);               // Path is nooit gedraaid
        Assert.NotEqual(RetrievalMode.Path, outcome.Mode.Primary); // Path→Drift gedegradeerd
    }

    // ── 5) Begrotings-fallback: latency-budget overschreden → Local-only ──

    [Fact]
    public async Task Budget_LatencyOverschreden_ValtTerugOpLocalOnly()
    {
        var retriever = new FakeRetriever();
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling,
            new GraphRagContext(LatencyBudget: new LatencyBudget(1000)),
            elapsedMsOverride: () => 5000); // meteen over budget

        Assert.Equal(RetrievalFallback.LatencyExceeded, outcome.FallbackReason);
        Assert.Equal(RetrievalMode.Local, outcome.Mode.Primary);
        Assert.False(retriever.DriftCalled && retriever.PathCalled);
    }

    // ── 6) NoPath-signaal wanneer een pad verwacht maar niet gevonden is ──

    [Fact]
    public async Task GeenPad_LevertNoPathSignaal()
    {
        var retriever = new FakeRetriever(); // alle modi leeg
        var orch = new RetrievalOrchestrator(new FakeGazetteer(Gaz()), new FakeSimilarity(), new FakeAdjacency(), retriever);

        var outcome = await orch.RetrieveAsync(
            "Werkt Empowered samen met Might in een showdown?", QuestionType.Ruling);

        Assert.NotNull(outcome.NoPath);
        Assert.True(outcome.Anchors.Count >= 2);
    }

    // ── AnswerTrace: opbouw + Postgres-roundtrip (cascade) ──

    [Fact]
    public void AnswerTrace_Build_VerantwoordtMetTrustWaardeToen()
    {
        var official = new BundleItem(BrainRef.Section("core", "1.1"), KnowledgeTier.Official, "regel", 0.9,
            TrustVector.OfficialDefault, WidgetMarker: "[[rule:1.1]]");
        var bundle = ContextBundler.Bundle([official], tokenBudget: 1000);
        var gate = TrustGate.Decide(ContextBundler.ToTrustCandidates([official]));
        var mode = ModeSelector.Select(QuestionType.Definitie, "Wat betekent Exhaust?", 1);

        var trace = AnswerTraceBuilder.Build("Wat betekent Exhaust?", QuestionType.Definitie, mode,
            beta: 0.6, gate, bundle, epoch: new TraceEpoch(GraphEpoch: "epoch-1", LlmModel: "claude-opus-4-8"),
            id: Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddSeconds(1), new byte[10]));

        var support = Assert.Single(trace.Supports);
        Assert.Equal("section:core/1.1", support.SubjectRef);
        Assert.Equal(1.0, support.TrustWeightAtQuery, 6);
        Assert.Equal("epoch-1", trace.GraphEpoch);
        Assert.Equal("[[rule:1.1]]", support.WidgetMarker);
    }

    [Fact]
    public async Task AnswerTrace_Roundtrip_CascadeBewaartSupports()
    {
        var official = new BundleItem(BrainRef.Section("core", "1.1"), KnowledgeTier.Official, "regel", 0.9,
            TrustVector.OfficialDefault);
        var bundle = ContextBundler.Bundle([official], tokenBudget: 1000);
        var gate = TrustGate.Decide(ContextBundler.ToTrustCandidates([official]));
        var mode = ModeSelector.Select(QuestionType.Ruling, "Werkt A met B?", 2);
        var trace = AnswerTraceBuilder.Build("Werkt A met B?", QuestionType.Ruling, mode, 0.7, gate, bundle);

        await using var db = NewDb();
        db.AnswerTraces.Add(trace);
        await db.SaveChangesAsync();

        await using var db2 = NewDb2(db);
        var reloaded = await db2.AnswerTraces.Include(t => t.Supports).SingleAsync(t => t.Id == trace.Id);
        Assert.Single(reloaded.Supports);
        Assert.Equal(PrimaryChannel.Official.ToString(), reloaded.PrimaryChannel);
    }

    private static readonly string SharedDbName = "answer-trace-" + Guid.NewGuid();
    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>().UseInMemoryDatabase(SharedDbName).Options);
    private static RbRulesDbContext NewDb2(RbRulesDbContext _) => NewDb();

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — de AnswerTrace zelf draagt geen vectors).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<Pgvector.Vector, string>(
                            v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }
}
