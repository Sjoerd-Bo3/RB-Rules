using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Ops-observability-rollups (#231, spec §7, inzicht #236): mining-precisie
/// per (soort × model), kosten/latency per retrieval-modus, community-stabiliteit en
/// het samengestelde admin-tegel-rapport. Kern: onafgemaakte runs tellen niet mee;
/// community-stabiliteit is label-onafhankelijk (Leiden hernummert).</summary>
public class ObservabilityRollupsTests
{
    private static MiningRun Run(
        string kind, string? model, int cand, int ver, int rej, bool done = true) => new()
    {
        Id = Ulid.NewUlid(),
        Kind = kind,
        LlmModel = model,
        Candidates = cand,
        Verified = ver,
        Rejected = rej,
        CompletedAt = done ? DateTimeOffset.UtcNow : null,
    };

    // --- Mining-precisie ---

    [Fact]
    public void MiningPrecision_GroepeertPerSoortEnModel()
    {
        var rows = ObservabilityRollups.MiningPrecision(
        [
            Run("relation", "claude-opus-4-8", cand: 100, ver: 30, rej: 10),
            Run("relation", "claude-opus-4-8", cand: 100, ver: 40, rej: 10),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal("relation", row.Kind);
        Assert.Equal("claude-opus-4-8", row.Model);
        Assert.Equal(2, row.Runs);
        Assert.Equal(200, row.Candidates);
        Assert.Equal(70, row.Verified);
        Assert.Equal(20, row.Rejected);
        Assert.Equal(70.0 / 90, row.Precision, 6);   // verified / (verified+rejected)
        Assert.Equal(70.0 / 200, row.AcceptRate, 6); // verified / candidates
    }

    [Fact]
    public void MiningPrecision_OnafgemaakteRun_TeltNietMee()
    {
        var rows = ObservabilityRollups.MiningPrecision(
        [
            Run("relation", "m", cand: 10, ver: 5, rej: 5, done: false),
        ]);
        Assert.Empty(rows);
    }

    [Fact]
    public void MiningPrecision_ZonderModel_HeetDeterministisch()
    {
        var rows = ObservabilityRollups.MiningPrecision(
        [
            Run("mechanic", null, cand: 10, ver: 10, rej: 0),
        ]);
        Assert.Equal("(deterministisch)", Assert.Single(rows).Model);
    }

    // --- Retrieval-kosten ---

    [Fact]
    public void RetrievalCost_AggregeertPerModus()
    {
        var rows = ObservabilityRollups.RetrievalCost(
        [
            new RetrievalModeCostSample("Drift", 100, 500),
            new RetrievalModeCostSample("Drift", 300, 1500),
            new RetrievalModeCostSample("Local", 50, 200),
        ]);

        Assert.Equal(2, rows.Count);
        var drift = rows.First(r => r.Mode == "Drift");
        Assert.Equal(2, drift.Runs);
        Assert.Equal(200, drift.MeanLatencyMs, 6);
        Assert.Equal(1000, drift.MeanTokens, 6);
        Assert.Equal(2000L, drift.TotalTokens);
    }

    [Fact]
    public void RetrievalCost_SorteertOpMeesteRuns()
    {
        var rows = ObservabilityRollups.RetrievalCost(
        [
            new RetrievalModeCostSample("Local", 1, 1),
            new RetrievalModeCostSample("Drift", 1, 1),
            new RetrievalModeCostSample("Drift", 1, 1),
        ]);
        Assert.Equal("Drift", rows[0].Mode);
    }

    // --- Community-stabiliteit ---

    [Fact]
    public void CommunityStability_IdentiekeIndeling_IsEen()
    {
        // Zelfde groepering, maar met andere labels (Leiden hernummert): moet 1.0 blijven.
        var prev = new Dictionary<string, string> { ["a"] = "0", ["b"] = "0", ["c"] = "1" };
        var curr = new Dictionary<string, string> { ["a"] = "7", ["b"] = "7", ["c"] = "9" };
        Assert.Equal(1.0, CommunityStability.Score(prev, curr), 6);
    }

    [Fact]
    public void CommunityStability_OmgeschudLidmaatschap_ZaktOnderEen()
    {
        var prev = new Dictionary<string, string> { ["a"] = "0", ["b"] = "0", ["c"] = "0", ["d"] = "0" };
        var curr = new Dictionary<string, string> { ["a"] = "0", ["b"] = "0", ["c"] = "1", ["d"] = "1" };
        var score = CommunityStability.Score(prev, curr);
        Assert.True(score < 1.0);
        Assert.True(score > 0.0);
    }

    [Fact]
    public void CommunityStability_LegeHuidige_IsEen()
    {
        Assert.Equal(1.0, CommunityStability.Score(
            new Dictionary<string, string> { ["a"] = "0" }, new Dictionary<string, string>()));
    }

    // --- Samengesteld rapport ---

    [Fact]
    public void Report_Build_BundeltSecties()
    {
        var taken = DateTimeOffset.UtcNow;
        var report = ObservabilityReport.Build(
            taken,
            graphDrift: [new GraphDriftEntry("Card", 10, 8, -2)],
            miningRuns: [Run("relation", "m", 10, 8, 2)],
            retrievalCost: [new RetrievalModeCostSample("Local", 20, 100)],
            communityHealth: new CommunityHealthRow(0.42, 12, 3, 0.9));

        Assert.Equal(taken, report.TakenAt);
        Assert.Single(report.GraphDrift);
        Assert.Single(report.MiningPrecision);
        Assert.Single(report.RetrievalCost);
        Assert.Equal(0.42, report.CommunityHealth!.Modularity);
        Assert.Null(report.CanonicalDrift); // niet meegegeven → leeg
    }
}
