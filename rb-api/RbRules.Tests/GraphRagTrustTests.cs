using RbRules.Domain.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase 4 (#228): de trust-vector, corroboratie-noisy-OR met echo-kamer-
/// dedup, recency-verval per tier, en de GEGATE trust-routing (beslissing #229).
/// Puur — dit is de volledige weeg-/gating-logica zonder I/O.</summary>
public class GraphRagTrustTests
{
    // ── Corroboratie: noisy-OR ná onafhankelijkheids-dedup, NOOIT tellen ──

    [Fact]
    public void NoisyOr_DrieOnafhankelijkeBronnen_MatchtSpecVoorbeeld()
    {
        // §6-voorbeeld: 0.7, 0.6, 0.4 → 1 − (0.3·0.4·0.6) ≈ 0.928.
        var corrob = Corroboration.NoisyOr(
        [
            new("judge-blog", 0.7, 1.0),
            new("vod-transcript", 0.6, 1.0),
            new("discord", 0.4, 1.0),
        ]);
        Assert.Equal(0.928, corrob, 3);
    }

    [Fact]
    public void NoisyOr_DedupetOpCluster_EchoKamerTeltNietMee()
    {
        // Drie posts uit dezelfde thread = één stem (de sterkste), geen oplopen.
        var echoed = Corroboration.NoisyOr(
        [
            new("thread-1", 0.4, 1.0),
            new("thread-1", 0.3, 1.0),
            new("thread-1", 0.35, 1.0),
        ]);
        Assert.Equal(0.4, echoed, 3);
        Assert.Equal(1, Corroboration.IndependentCount(
        [
            new("thread-1", 0.4, 1.0),
            new("thread-1", 0.3, 1.0),
        ]));
    }

    [Fact]
    public void NoisyOr_GeenBronnen_IsNeutraal() =>
        Assert.Equal(Corroboration.None, Corroboration.NoisyOr([]));

    // ── Recency: λ per tier — officieel vervalt niet, meta het snelst ──

    [Fact]
    public void Recency_OfficieelVervaltNooit() =>
        Assert.Equal(1.0, Recency.Decay(KnowledgeTier.Official, 10_000));

    [Fact]
    public void Recency_VervalsnelheidLooptOpLangsDeTiers()
    {
        const double age = 30;
        var official = Recency.Decay(KnowledgeTier.Official, age);
        var ruling = Recency.Decay(KnowledgeTier.VerifiedRuling, age);
        var primer = Recency.Decay(KnowledgeTier.Primer, age);
        var community = Recency.Decay(KnowledgeTier.Community, age);
        var meta = Recency.Decay(KnowledgeTier.Meta, age);
        Assert.True(official > ruling);
        Assert.True(ruling > primer);
        Assert.True(primer > community);
        Assert.True(community > meta);
    }

    [Fact]
    public void Recency_NegatieveLeeftijd_KlemtOp1() =>
        Assert.Equal(1.0, Recency.Decay(KnowledgeTier.Meta, -5));

    // ── TrustVector: product, en een community-claim wint nooit van officieel ──

    [Fact]
    public void TrustVector_Weight_IsHetProductVanDeAssen()
    {
        var v = new TrustVector(0.45, 0.8, 0.9, 0.9);
        Assert.Equal(0.45 * 0.8 * 0.9 * 0.9, v.Weight, 6);
    }

    [Fact]
    public void TrustVector_CommunityWintNooitVanOfficieel()
    {
        var community = TrustVector.For(KnowledgeTier.Community, Verification.LexicallySupported,
            [new("a", 0.7, 1), new("b", 0.6, 1), new("c", 0.4, 1)], ageDays: 30);
        Assert.True(community.Weight < TrustVector.OfficialDefault.Weight);
        Assert.True(community.Weight < 0.4); // de §6-"toonbaar met badge"-orde
    }

    // ── TrustGate (beslissing #229): route op officiële dekking ──

    [Fact]
    public void Gate_OfficieleDekking_OfficieelPrimair_CommunityGebadged()
    {
        var decision = TrustGate.Decide(
        [
            new(KnowledgeTier.Official, 1.0),
            new(KnowledgeTier.Community, 0.33),
        ]);
        Assert.Equal(PrimaryChannel.Official, decision.Primary);
        Assert.True(decision.BadgeCommunity);
    }

    [Fact]
    public void Gate_GeenOfficieel_SterkeCommunity_PrimairMetBadge()
    {
        var decision = TrustGate.Decide([new(KnowledgeTier.Community, 0.33)]);
        Assert.Equal(PrimaryChannel.CommunityBadged, decision.Primary);
        Assert.True(decision.BadgeCommunity);
    }

    [Fact]
    public void Gate_GeenOfficieel_ZwakkeCommunity_EerlijkGeenDekking()
    {
        var decision = TrustGate.Decide([new(KnowledgeTier.Community, 0.15)]);
        Assert.Equal(PrimaryChannel.None, decision.Primary);
        Assert.True(decision.BadgeCommunity); // wél zichtbaar dat er iets was
    }

    [Fact]
    public void Gate_NietsGevonden_None_ZonderBadge()
    {
        var decision = TrustGate.Decide([]);
        Assert.Equal(PrimaryChannel.None, decision.Primary);
        Assert.False(decision.BadgeCommunity);
    }

    [Fact]
    public void Gate_Authority_IsTieBreaker_NietAnnihilator()
    {
        // Gelijke relevantie: officieel breekt de tie, maar de community-scalar
        // wordt niet genuld — hij is er nog (badge), alleen niet primair.
        Assert.True(TrustGate.CompareForTieBreak(
            KnowledgeTier.Official, 0.9, KnowledgeTier.Community, 0.95) > 0);
    }

    // ── Labels: machine-leesbaar, officieel vs. community ──

    [Fact]
    public void Labels_OfficieelDraagtAlleenTag() =>
        Assert.Equal("[OFFICIEEL]", TrustLabels.For(KnowledgeTier.Official, TrustVector.OfficialDefault, 0, 0));

    [Fact]
    public void Labels_CommunityDraagtTrustEnCorroboratie()
    {
        var label = TrustLabels.For(KnowledgeTier.Community, new TrustVector(0.45, 0.8, 0.9, 0.9), 2, 5);
        Assert.StartsWith("[COMMUNITY trust=", label);
        Assert.Contains("corrob=2/5", label);
    }
}
