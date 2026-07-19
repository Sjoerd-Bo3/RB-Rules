using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase 4 (#228): context-bundeling (trust-orde, budget-afkap van onderaf,
/// MMR) en de pad-scoring/pad-citaties (stevigst-onderbouwd pad, widget-markers,
/// NoPath). Puur en getest.</summary>
public class GraphRagBundlingTests
{
    private static BundleItem Item(BrainRef r, KnowledgeTier tier, string text, double rel, TrustVector t) =>
        new(r, tier, text, rel, t);

    // ── ContextBundler: harde afkap van ONDERAF ──

    [Fact]
    public void Bundle_KraptVanOnderaf_CommunityEnMetaVallenEerstWeg()
    {
        var official = Item(BrainRef.Section("core", "1.1"), KnowledgeTier.Official,
            new string('a', 40), 0.9, TrustVector.OfficialDefault);
        var community = Item(BrainRef.Claim(1), KnowledgeTier.Community,
            new string('b', 40), 0.9, new TrustVector(0.45, 0.8, 0.9, 0.9));
        var meta = Item(BrainRef.Tag("meta"), KnowledgeTier.Meta,
            new string('c', 40), 0.9, new TrustVector(0.25, 0.8, 0.9, 0.5));

        var bundle = ContextBundler.Bundle([community, meta, official], tokenBudget: 15);

        Assert.Single(bundle.Items);
        Assert.Equal(KnowledgeTier.Official, bundle.Items[0].Item.Tier);
        Assert.Contains(bundle.Dropped, i => i.Tier == KnowledgeTier.Community);
        Assert.Contains(bundle.Dropped, i => i.Tier == KnowledgeTier.Meta);
    }

    [Fact]
    public void Bundle_OfficieelStaatBovenCommunity_OngeachtRelevantie()
    {
        var community = Item(BrainRef.Claim(1), KnowledgeTier.Community, "community text hier",
            0.99, new TrustVector(0.45, 0.8, 0.9, 0.9));
        var official = Item(BrainRef.Section("core", "2.1"), KnowledgeTier.Official, "officiele regel hier",
            0.10, TrustVector.OfficialDefault);
        var bundle = ContextBundler.Bundle([community, official], tokenBudget: 1000);
        Assert.Equal(KnowledgeTier.Official, bundle.Items[0].Item.Tier);
    }

    [Fact]
    public void Bundle_MmrOnderdruktBijnaDuplicaatBinnenLaag()
    {
        var trust = new TrustVector(0.45, 0.8, 0.9, 0.9);
        var a = Item(BrainRef.Claim(1), KnowledgeTier.Community, "alpha alpha alpha alpha", 0.90, trust);
        var b = Item(BrainRef.Claim(2), KnowledgeTier.Community, "alpha alpha alpha alpha", 0.85, trust);
        var c = Item(BrainRef.Claim(3), KnowledgeTier.Community, "beta gamma delta epsilon", 0.80, trust);

        var bundle = ContextBundler.Bundle([a, b, c], tokenBudget: 1000);

        // A eerst (hoogste score); daarna C (divers) vóór B (duplicaat van A).
        Assert.Equal(BrainRef.Claim(1), bundle.Items[0].Item.Ref);
        Assert.Equal(BrainRef.Claim(3), bundle.Items[1].Item.Ref);
        Assert.Equal(BrainRef.Claim(2), bundle.Items[2].Item.Ref);
    }

    [Fact]
    public void Bundle_KentStabieleCitationNummersEnLabels()
    {
        var official = Item(BrainRef.Section("core", "1.1"), KnowledgeTier.Official, "regel", 0.9,
            TrustVector.OfficialDefault);
        var bundle = ContextBundler.Bundle([official], tokenBudget: 1000, startCitationId: 5);
        Assert.Equal(5, bundle.Items[0].N);
        Assert.Equal("[OFFICIEEL]", bundle.Items[0].TrustLabel);
    }

    [Fact]
    public void Bundle_LeegeInvoer_LegeBundel() =>
        Assert.Empty(ContextBundler.Bundle([], tokenBudget: 100).Items);

    // ── PathScoring: het STEVIGST onderbouwde pad, niet het kortste ──

    [Fact]
    public void PathScoring_StevigLangPad_VerslaatZwakKortPad()
    {
        var start = new GraphNode(BrainRef.Card("a"), KnowledgeTier.Official, "A");
        var edge = new GraphEdge(BrainRef.Card("a"), BrainRef.Section("core", "1"), "GOVERNED_BY", 1.0);

        // Kort pad (1 stap) over een zwakke/onzekere edge.
        var shortWeak = new GraphPath(start,
            [new PathHop(new GraphNode(BrainRef.Section("core", "1"), KnowledgeTier.Official, "§1"),
                edge, TrustWeight: 0.4, Confidence: 0.5)]);

        // Lang pad (3 stappen) over stevige, zekere edges.
        var longStrong = new GraphPath(start,
        [
            new PathHop(new GraphNode(BrainRef.Mechanic("X"), KnowledgeTier.Official, "X"), edge, 0.95, 0.95),
            new PathHop(new GraphNode(BrainRef.Mechanic("Y"), KnowledgeTier.Official, "Y"), edge, 0.95, 0.95),
            new PathHop(new GraphNode(BrainRef.Section("core", "9"), KnowledgeTier.Official, "§9"), edge, 0.95, 0.95),
        ]);

        Assert.True(PathScoring.Weight(longStrong) < PathScoring.Weight(shortWeak));
        var best = PathScoring.SelectKBest([shortWeak, longStrong], 1);
        Assert.Single(best);
        Assert.Equal(BrainRef.Section("core", "9"), best[0].End.Ref);
    }

    // ── Pad → citatie met widget-markers ──

    [Fact]
    public void PathCitations_LeverenWidgetMarkersPerKnoopsoort()
    {
        var start = new GraphNode(BrainRef.Card("Exhaust Card"), KnowledgeTier.Official, "Exhaust Card");
        var edge = new GraphEdge(BrainRef.Card("Exhaust Card"), BrainRef.Section("core", "7.3"), "GOVERNED_BY", 1.0);
        var path = new GraphPath(start,
            [new PathHop(new GraphNode(BrainRef.Section("core", "7.3"), KnowledgeTier.Official, "§7.3"), edge, 0.9, 0.9)]);

        var citations = PathCitations.Build([path], startId: 1);
        Assert.Equal("[[card:Exhaust Card]]", citations[0].WidgetMarker);
        Assert.Equal("[[rule:7.3]]", citations[1].WidgetMarker);
        Assert.Equal(1, citations[0].N);
        Assert.Equal(2, citations[1].N);
    }

    [Fact]
    public void PathCitations_Explain_ToontDePadStructuur()
    {
        var start = new GraphNode(BrainRef.Card("Unit"), KnowledgeTier.Official, "Unit");
        var edge = new GraphEdge(BrainRef.Card("Unit"), BrainRef.Concept("showdown"), "REQUIRES", 1.0);
        var path = new GraphPath(start,
            [new PathHop(new GraphNode(BrainRef.Concept("showdown"), KnowledgeTier.Official, "Showdown"), edge, 0.9, 0.9)]);
        Assert.Equal("Unit —REQUIRES→ Showdown", PathCitations.Explain(path));
    }

    [Fact]
    public void NoPathSignal_NoemtBeideAnkers()
    {
        var signal = NoPathSignal.For([BrainRef.Card("a"), BrainRef.Card("b")]);
        Assert.Contains("card:a", signal.Message);
        Assert.Contains("card:b", signal.Message);
    }
}
