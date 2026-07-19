using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Governance & schema-evolutie (fase 6, #230, §6): de semver-ontologie, de
/// has-pending-ontology-poort (CI-gate, spiegelt has-pending-model-changes), de
/// bump-classificatie en de deterministische staging-promotie-poort. Puur, geen IO.</summary>
public class OntologyGovernanceTests
{
    // ── SemVer ────────────────────────────────────────────────────────────────
    [Fact]
    public void SemVer_Parse_En_Bump_Kloppen()
    {
        var v = SemVer.Parse("1.2.3");
        Assert.NotNull(v);
        Assert.Equal(new SemVer(1, 2, 3), v!.Value);

        Assert.Equal(new SemVer(2, 0, 0), v.Value.Bump(OntologyBumpKind.Major));
        Assert.Equal(new SemVer(1, 3, 0), v.Value.Bump(OntologyBumpKind.Minor));
        Assert.Equal(new SemVer(1, 2, 4), v.Value.Bump(OntologyBumpKind.Patch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("-1.0.0")]
    public void SemVer_Parse_Malformed_GeeftNull(string bad) => Assert.Null(SemVer.Parse(bad));

    [Fact]
    public void SemVer_Ordening_IsSemantisch_NietLexicaal()
    {
        // Lexicaal zou "1.10.0" < "1.9.0" zijn; semantisch niet.
        Assert.True(SemVer.Parse("1.9.0")!.Value < SemVer.Parse("1.10.0")!.Value);
        Assert.True(SemVer.Parse("2.0.0")!.Value > SemVer.Parse("1.99.99")!.Value);
    }

    // ── has-pending-ontology-poort ────────────────────────────────────────────
    [Fact]
    public void HasPendingGate_CodeInSyncMetBaseline_Passeert()
    {
        // De checked-in baseline MOET de huidige schema-structuur beschrijven — dit is
        // exact de CI-gate. Faalt deze test, dan is OntologySchema gewijzigd zonder de
        // baseline (versie + fingerprint) bij te werken (has-pending-ontology-changes).
        var report = OntologyChangeGate.Check();
        Assert.True(report.Passes,
            $"Openstaande ontologie-wijziging. Werk OntologyBaseline.Fingerprint bij naar: " +
            $"{report.ComputedFingerprint}");
        Assert.False(report.HasPendingChanges);
    }

    [Fact]
    public void Fingerprint_IsDeterministisch_OverHerhaaldeCaptures()
    {
        var a = OntologySnapshot.CurrentFingerprint();
        var b = OntologySnapshot.Fingerprint(OntologySnapshot.Capture());
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex
    }

    // ── Bump-classificatie ────────────────────────────────────────────────────
    [Fact]
    public void Bump_IdentiekeStructuur_IsPatch()
    {
        var s = OntologySnapshot.Capture();
        Assert.Equal(OntologyBumpKind.Patch, OntologyBumpClassifier.Classify(s, s));
    }

    [Fact]
    public void Bump_NieuwRelatietype_IsMinor()
    {
        var prev = OntologySnapshot.Capture();
        var current = prev with { Relations = prev.Relations.Append("REDIRECTS:Card>Card:0..*:None:[]").ToList() };
        Assert.Equal(OntologyBumpKind.Minor, OntologyBumpClassifier.Classify(prev, current));
    }

    [Fact]
    public void Bump_NieuweSubklasse_IsMinor()
    {
        var prev = OntologySnapshot.Capture();
        var current = prev with { Classes = prev.Classes.Append("Overload<-Keyword").ToList() };
        Assert.Equal(OntologyBumpKind.Minor, OntologyBumpClassifier.Classify(prev, current));
    }

    [Fact]
    public void Bump_VerwijderdeKlasse_IsMajor()
    {
        var prev = OntologySnapshot.Capture();
        var current = prev with { Classes = prev.Classes.Take(prev.Classes.Count - 1).ToList() };
        Assert.Equal(OntologyBumpKind.Major, OntologyBumpClassifier.Classify(prev, current));
    }

    [Fact]
    public void Bump_DisjointnessWijziging_IsMajor_OokAdditief()
    {
        var prev = OntologySnapshot.Capture();
        // Additief nieuw disjoint-paar — toch major (§6: disjointness her-valideert alles).
        var current = prev with { DisjointPairs = prev.DisjointPairs.Append("Effect|Trigger").ToList() };
        Assert.Equal(OntologyBumpKind.Major, OntologyBumpClassifier.Classify(prev, current));
    }

    // ── Staging-promotie-poort (:Proposed) ────────────────────────────────────
    private static SchemaProposal Proposal(int cards, bool section) => new()
    {
        Kind = SchemaProposalKind.RelationType.ToString(),
        ProposedName = "REDIRECTS",
        OfficialCardCount = cards,
        HasRuleSectionEvidence = section,
        RuleSectionRef = section ? "section:core-rules-pdf/9.2" : null,
        Memo = "gemined uit set OGN",
        BumpKind = OntologyBumpKind.Minor.ToString(),
        RunId = "run-1",
    };

    [Fact]
    public void ProposalGate_ZonderBewijs_BlijftInStaging()
    {
        // Een LLM-vermoeden zonder officiële kaarten of sectie hardt NOOIT (rode draad #236).
        var r = SchemaProposalGate.Evaluate(Proposal(cards: 0, section: false));
        Assert.False(r.EligibleForReview);
        Assert.Contains(r.Violations, v => v.Code == SchemaProposalGate.Code.InsufficientOfficialCards);
        Assert.Contains(r.Violations, v => v.Code == SchemaProposalGate.Code.MissingRuleSection);
    }

    [Fact]
    public void ProposalGate_KaartenMaarGeenSectie_BlijftInStaging()
    {
        var r = SchemaProposalGate.Evaluate(Proposal(cards: 5, section: false));
        Assert.False(r.EligibleForReview);
        Assert.Contains(r.Violations, v => v.Code == SchemaProposalGate.Code.MissingRuleSection);
    }

    [Fact]
    public void ProposalGate_DeterministischBewijs_HaaltReviewdrempel()
    {
        // ≥N officiële kaarten ÉN een verankerende sectie → mag naar de reviewqueue.
        // Dat is nog GEEN migratie — die blijft een expliciete beheerdersactie.
        var r = SchemaProposalGate.Evaluate(Proposal(cards: 5, section: true));
        Assert.True(r.EligibleForReview);
        Assert.Empty(r.Violations);
    }

    [Fact]
    public void RelationProposalPolicy_DefaultReifieert_TenzijHoogFrequentEnWaardevol()
    {
        Assert.True(RelationProposalPolicy.ShouldReifyByDefault(observedFrequency: 3, hasRetrievalValue: true));
        Assert.True(RelationProposalPolicy.ShouldReifyByDefault(observedFrequency: 100, hasRetrievalValue: false));
        Assert.False(RelationProposalPolicy.ShouldReifyByDefault(observedFrequency: 100, hasRetrievalValue: true));
    }
}
