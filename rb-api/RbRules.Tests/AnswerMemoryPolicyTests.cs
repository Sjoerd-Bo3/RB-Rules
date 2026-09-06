using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De zuivere regels van het antwoordgeheugen (#384): drempels,
/// promotielus, bron-momentopname en de in-aanmerking-poort. Drempels staan
/// hier als UITGESCHREVEN literals (#286/#293-les): een test die tegen de
/// constante zelf toetst schuift met haar mee.</summary>
public class AnswerMemoryPolicyTests
{
    [Theory]
    [InlineData(0.92, true)]
    [InlineData(0.99, true)]
    [InlineData(0.919, false)]
    [InlineData(0.80, false)]
    public void IsSameQuestion_ScherpeDrempelOp092(double sim, bool expected) =>
        Assert.Equal(expected, AnswerMemoryPolicy.IsSameQuestion(sim));

    [Theory]
    [InlineData(0.80, true)]
    [InlineData(0.91, true)]
    [InlineData(0.92, false)]
    [InlineData(0.79, false)]
    public void IsSimilarQuestion_TussenDeDrempels(double sim, bool expected) =>
        Assert.Equal(expected, AnswerMemoryPolicy.IsSimilarQuestion(sim));

    [Theory]
    [InlineData("candidate", false)]
    [InlineData("confirmed", true)]
    [InlineData("verified", true)]
    [InlineData("retracted", false)]
    public void MayServe_AlleenBevestigdOfGeverifieerd(string trust, bool expected) =>
        Assert.Equal(expected, AnswerMemoryPolicy.MayServe(trust));

    [Fact]
    public void TrustAfterHit_DerdeHitBevestigtEenKandidaat()
    {
        Assert.Equal("candidate", AnswerMemoryPolicy.TrustAfterHit("candidate", 1));
        Assert.Equal("candidate", AnswerMemoryPolicy.TrustAfterHit("candidate", 2));
        Assert.Equal("confirmed", AnswerMemoryPolicy.TrustAfterHit("candidate", 3));
        // Andere toestanden bewegen niet op hits — ook een ingetrokken rij niet.
        Assert.Equal("verified", AnswerMemoryPolicy.TrustAfterHit("verified", 3));
        Assert.Equal("retracted", AnswerMemoryPolicy.TrustAfterHit("retracted", 99));
    }

    [Fact]
    public void TrustAfterFeedback_DuimOmhoogBevestigt_DuimOmlaagTrektIn()
    {
        Assert.Equal("confirmed", AnswerMemoryPolicy.TrustAfterFeedback("candidate", thumbsUp: true));
        Assert.Equal("verified", AnswerMemoryPolicy.TrustAfterFeedback("verified", thumbsUp: true));
        Assert.Equal("retracted", AnswerMemoryPolicy.TrustAfterFeedback("candidate", thumbsUp: false));
        Assert.Equal("retracted", AnswerMemoryPolicy.TrustAfterFeedback("confirmed", thumbsUp: false));
        // Ook een geverifieerde rij: bij tegenspraak liever opnieuw reviewen dan doorserveren.
        Assert.Equal("retracted", AnswerMemoryPolicy.TrustAfterFeedback("verified", thumbsUp: false));
        // Een ingetrokken rij komt via feedback nooit terug.
        Assert.Equal("retracted", AnswerMemoryPolicy.TrustAfterFeedback("retracted", thumbsUp: true));
    }

    [Fact]
    public void Snapshot_IsStabielGesorteerdEnRoundtript()
    {
        var snap = AnswerMemoryPolicy.EncodeSnapshot([("core", "h1"), ("hub", null), ("core", "dup")]);
        Assert.Equal(",core=h1,hub=,", snap);
        var back = AnswerMemoryPolicy.DecodeSnapshot(snap);
        Assert.Equal([("core", "h1"), ("hub", "")], back);
        Assert.Contains(AnswerMemoryPolicy.SnapshotKey("core"), snap);
        // Geen vals-positief op een bron-id dat als prefix in een ander zit.
        Assert.DoesNotContain(AnswerMemoryPolicy.SnapshotKey("cor"), snap);
    }

    [Fact]
    public void SourcesUnchanged_GewijzigdeOfVerdwenenBronMaaktHetAntwoordVerdacht()
    {
        var snap = AnswerMemoryPolicy.EncodeSnapshot([("core", "h1"), ("hub", "h2")]);
        Assert.True(AnswerMemoryPolicy.SourcesUnchanged(snap,
            new Dictionary<string, string?> { ["core"] = "h1", ["hub"] = "h2" }));
        Assert.False(AnswerMemoryPolicy.SourcesUnchanged(snap,
            new Dictionary<string, string?> { ["core"] = "h1-nieuw", ["hub"] = "h2" }));
        Assert.False(AnswerMemoryPolicy.SourcesUnchanged(snap,
            new Dictionary<string, string?> { ["core"] = "h1" }));
        // Nooit gescand op beide momenten = niets veranderd.
        var unscanned = AnswerMemoryPolicy.EncodeSnapshot([("hub", null)]);
        Assert.True(AnswerMemoryPolicy.SourcesUnchanged(unscanned,
            new Dictionary<string, string?> { ["hub"] = null }));
    }

    [Fact]
    public void Eligible_AlleenEersteBeurtZonderFotoBuitenBenchmark()
    {
        Assert.True(AnswerMemoryPolicy.Eligible(firstTurn: true, hasImage: false, benchmark: false, modelOverride: false));
        Assert.False(AnswerMemoryPolicy.Eligible(firstTurn: false, hasImage: false, benchmark: false, modelOverride: false));
        Assert.False(AnswerMemoryPolicy.Eligible(firstTurn: true, hasImage: true, benchmark: false, modelOverride: false));
        Assert.False(AnswerMemoryPolicy.Eligible(firstTurn: true, hasImage: false, benchmark: true, modelOverride: false));
        Assert.False(AnswerMemoryPolicy.Eligible(firstTurn: true, hasImage: false, benchmark: false, modelOverride: true));
    }
}
