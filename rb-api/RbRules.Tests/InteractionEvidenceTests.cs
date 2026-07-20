using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De pure bouwstenen van de verscherpte bewijs-/tautologie-poort (#249):
/// wanneer drukt bewijs een RELATIE uit, en wanneer is een rollenpaar niets meer dan
/// een kaart met haar eigen keyword (al deterministisch bekend uit
/// <c>Card.Mechanics</c> → HAS_MECHANIC in de graph)?</summary>
public class InteractionEvidenceTests
{
    // ── ExpressesRelation ────────────────────────────────────────────────────

    [Fact]
    public void ExpressesRelation_BeideTextueel_IsBewijs()
    {
        // De klassieke bewijszin: "Deflect prevents Assault damage" — beide labels
        // staan letterlijk in één bron.
        Assert.True(InteractionEvidence.ExpressesRelation(
            EvidenceAnchor.Textual, EvidenceAnchor.Textual));
    }

    [Fact]
    public void ExpressesRelation_IdentiteitPlusTextueel_IsBewijs()
    {
        // Een kaart die een ANDER keyword in haar tekst noemt: de kaart is zichzelf
        // (identity), het keyword staat er letterlijk (textueel).
        Assert.True(InteractionEvidence.ExpressesRelation(
            EvidenceAnchor.Identity, EvidenceAnchor.Textual));
        Assert.True(InteractionEvidence.ExpressesRelation(
            EvidenceAnchor.Textual, EvidenceAnchor.Identity));
    }

    [Fact]
    public void ExpressesRelation_TweeIdentiteitsAnkers_IsGeenBewijs()
    {
        // Kern van #249: "deze kaart is deze kaart" zegt niets over een relatie.
        Assert.False(InteractionEvidence.ExpressesRelation(
            EvidenceAnchor.Identity, EvidenceAnchor.Identity));
    }

    [Theory]
    [InlineData(EvidenceAnchor.None, EvidenceAnchor.Textual)]
    [InlineData(EvidenceAnchor.Textual, EvidenceAnchor.None)]
    [InlineData(EvidenceAnchor.None, EvidenceAnchor.None)]
    [InlineData(EvidenceAnchor.None, EvidenceAnchor.Identity)]
    public void ExpressesRelation_OnverankerdeRol_IsGeenBewijs(EvidenceAnchor a, EvidenceAnchor b) =>
        Assert.False(InteractionEvidence.ExpressesRelation(a, b));

    // ── IsCardOwnKeywordPair ─────────────────────────────────────────────────

    private static readonly Dictionary<string, IReadOnlySet<string>> Own = new(StringComparer.Ordinal)
    {
        ["card:ogn-001"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "mechanic:Equip", "mechanic:Quick-Draw",
        },
        ["card:ogn-002"] = new HashSet<string>(StringComparer.Ordinal) { "mechanic:Tank" },
    };

    [Fact]
    public void IsCardOwnKeywordPair_EigenKeyword_IsTautologie()
    {
        Assert.True(InteractionTautology.IsCardOwnKeywordPair(
            "card:ogn-001", "mechanic:Equip", Own));
    }

    [Fact]
    public void IsCardOwnKeywordPair_IsRichtingOnafhankelijk()
    {
        // De tautologie zit in het paar, niet in de agent/patient-rolverdeling.
        Assert.True(InteractionTautology.IsCardOwnKeywordPair(
            "mechanic:Equip", "card:ogn-001", Own));
    }

    [Fact]
    public void IsCardOwnKeywordPair_AndermansKeyword_IsGeenTautologie()
    {
        // ogn-001 draagt Tank niet — een relatie met Tank is echte kennis.
        Assert.False(InteractionTautology.IsCardOwnKeywordPair(
            "card:ogn-001", "mechanic:Tank", Own));
    }

    [Fact]
    public void IsCardOwnKeywordPair_KeywordPaarEnKaartPaar_ZijnGeenTautologie()
    {
        Assert.False(InteractionTautology.IsCardOwnKeywordPair(
            "mechanic:Equip", "mechanic:Tank", Own));
        Assert.False(InteractionTautology.IsCardOwnKeywordPair(
            "card:ogn-001", "card:ogn-002", Own));
    }

    [Fact]
    public void IsCardOwnKeywordPair_OnbekendeRefs_ZijnGeenTautologie()
    {
        Assert.False(InteractionTautology.IsCardOwnKeywordPair(
            "card:onbekend", "mechanic:Equip", Own));
        Assert.False(InteractionTautology.IsCardOwnKeywordPair(null, "mechanic:Equip", Own));
        Assert.False(InteractionTautology.IsCardOwnKeywordPair("card:ogn-001", null, Own));
    }

    // ── De poort-guard (defense-in-depth naast de mining-filter) ─────────────

    [Fact]
    public void Gate_KaartMetEigenKeyword_WordtGeweigerdZonderGrafsteen()
    {
        // Zelfs met volledige steun én een positief verdict: dit feit hoort niet in
        // de interactie-tabel. Géén tombstone — de sleutel mag niet blijvend dicht.
        var result = InteractionPromotionGate.Evaluate(new InteractionGateSignals(
            SchemaValid: true, SchemaReason: null, LexicalSupport: true,
            ConsensusCount: 5, ConsensusThreshold: 2, LlmVerdictInteracts: true,
            IsEmergentCardCardPair: false, HasBlockingTombstone: false,
            IsCardOwnKeywordPair: true));

        Assert.Equal(InteractionGateOutcome.Rejected, result.Outcome);
        Assert.False(result.WritesTombstone);
        Assert.Contains("eigen keyword", result.StatusReason);
    }

    [Fact]
    public void Gate_SelfLoop_WordtGeweigerdZonderGrafsteen()
    {
        var result = InteractionPromotionGate.Evaluate(new InteractionGateSignals(
            SchemaValid: true, SchemaReason: null, LexicalSupport: true,
            ConsensusCount: 5, ConsensusThreshold: 2, LlmVerdictInteracts: true,
            IsEmergentCardCardPair: false, HasBlockingTombstone: false,
            RolesDistinct: false));

        Assert.Equal(InteractionGateOutcome.Rejected, result.Outcome);
        Assert.False(result.WritesTombstone);
    }

    [Fact]
    public void Gate_BestaandeTombstone_WintNogSteedsVanDeNieuweRollenPoort()
    {
        // Volgorde bewaken: stil-heropenen blijft de eerste, hardste poort.
        var result = InteractionPromotionGate.Evaluate(new InteractionGateSignals(
            SchemaValid: true, SchemaReason: null, LexicalSupport: true,
            ConsensusCount: 5, ConsensusThreshold: 2, LlmVerdictInteracts: true,
            IsEmergentCardCardPair: false, HasBlockingTombstone: true,
            IsCardOwnKeywordPair: true));

        Assert.Equal(InteractionGateOutcome.Rejected, result.Outcome);
        Assert.Contains("eerder verworpen", result.StatusReason);
    }

    [Fact]
    public void Gate_DefaultSignalen_VeranderenBestaandGedragNiet()
    {
        // De twee nieuwe signalen hebben veilige defaults: bestaande aanroepers
        // (ReifiedInteractionTests, andere pipelines) zien exact hetzelfde gedrag.
        var result = InteractionPromotionGate.Evaluate(new InteractionGateSignals(
            SchemaValid: true, SchemaReason: null, LexicalSupport: true,
            ConsensusCount: 1, ConsensusThreshold: 2, LlmVerdictInteracts: true,
            IsEmergentCardCardPair: false, HasBlockingTombstone: false));

        Assert.Equal(InteractionGateOutcome.Promoted, result.Outcome);
    }
}
