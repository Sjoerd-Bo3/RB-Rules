using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>One-shot patch-notes-Change (#205): pure guard-logica, geen I/O —
/// IngestService levert TrustTier/effectieve kind/of er al een
/// niet-editoriale Change is/of het run_log-memo al bestaat.</summary>
public class PatchNotesOneShotChangeTests
{
    [Fact]
    public void IsCandidate_PatchNotesZonderChangeOfMemo_Kandidaat()
    {
        Assert.True(PatchNotesOneShotChange.IsCandidate(
            trustTier: 1, effectiveKind: SourceContentKind.PatchNotes,
            hasNonEditorialChange: false, hasOneShotMemo: false));
    }

    [Fact]
    public void IsCandidate_PatchNotesMetNietEditorialeChange_GeenKandidaat()
    {
        // De echte duiding draaide al eerder (normale diff of een eerdere
        // one-shot) — nooit opnieuw, ook niet als de hash weer ongewijzigd is.
        Assert.False(PatchNotesOneShotChange.IsCandidate(
            trustTier: 1, effectiveKind: SourceContentKind.PatchNotes,
            hasNonEditorialChange: true, hasOneShotMemo: false));
    }

    [Fact]
    public void IsCandidate_MetOneShotMemo_GeenKandidaat()
    {
        // #205-review (findings 4/5/9): de guard is gesloten onder zijn eigen
        // output — óók als de geminte Change (later) "editorial" gelabeld is
        // en er dus geen niet-editoriale Change meer telt, blokkeert het memo
        // een tweede poging.
        Assert.False(PatchNotesOneShotChange.IsCandidate(
            trustTier: 1, effectiveKind: SourceContentKind.PatchNotes,
            hasNonEditorialChange: false, hasOneShotMemo: true));
    }

    [Fact]
    public void IsCandidate_NietOfficieel_GeenKandidaat()
    {
        Assert.False(PatchNotesOneShotChange.IsCandidate(
            trustTier: 3, effectiveKind: SourceContentKind.PatchNotes,
            hasNonEditorialChange: false, hasOneShotMemo: false));
    }

    [Theory]
    [InlineData(SourceContentKind.Faq)]
    [InlineData(SourceContentKind.Other)]
    public void IsCandidate_AndereKind_GeenKandidaat(string kind)
    {
        Assert.False(PatchNotesOneShotChange.IsCandidate(
            trustTier: 1, effectiveKind: kind,
            hasNonEditorialChange: false, hasOneShotMemo: false));
    }
}
