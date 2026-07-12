using RbRules.Domain;

namespace RbRules.Tests;

public class ClaimRetrievalTests
{
    private static RetrievedClaim Claim(
        int corroboration = 4, double trust = 0.94, string officialStatus = "unchecked") =>
        new("mechanic", "Deflect", "Deflect beschermt alleen tegen gekozen targets.",
            corroboration, trust, officialStatus);

    [Fact]
    public void TakeFor_EveryType_TakesAtLeastOne()
    {
        foreach (var t in Enum.GetValues<QuestionType>())
            Assert.True(ClaimRetrieval.TakeFor(t) >= 1);
    }

    [Fact]
    public void TakeFor_RulingWeighsLighterThanListMeta()
    {
        // Router-gewicht (#51 / docs/KNOWLEDGE.md): normatieve vragen krijgen
        // weinig interpretatie mee, lijst-/meta-vragen het meest.
        Assert.True(ClaimRetrieval.TakeFor(QuestionType.Ruling)
            < ClaimRetrieval.TakeFor(QuestionType.Lijst));
        Assert.True(ClaimRetrieval.TakeFor(QuestionType.Toernooi)
            <= ClaimRetrieval.TakeFor(QuestionType.Definitie));
    }

    [Fact]
    public void PromptLabel_MatchesKnowledgeDocFormat()
    {
        // Het label uit docs/KNOWLEDGE.md: "[community, 4 bronnen, trust 0.94]"
        // — invariant genoteerd (punt), ongeacht de server-culture.
        Assert.Equal("[community, 4 bronnen, trust 0.94]",
            ClaimRetrieval.PromptLabel(Claim()));
    }

    [Fact]
    public void PromptLabel_SingleSource_UsesSingular()
    {
        Assert.StartsWith("[community, 1 bron, trust 0.50]",
            ClaimRetrieval.PromptLabel(Claim(corroboration: 1, trust: 0.5)));
    }

    [Fact]
    public void PromptLabel_OfficiallyConfirmed_AddsSignal()
    {
        Assert.Equal(
            "[community, 2 bronnen, trust 0.75] (door de officiële regels bevestigd)",
            ClaimRetrieval.PromptLabel(Claim(corroboration: 2, trust: 0.75,
                officialStatus: "confirmed")));
    }

    [Fact]
    public void PromptBlock_Empty_WhenNoClaims() =>
        Assert.Equal("", ClaimRetrieval.PromptBlock([]));

    [Fact]
    public void PromptBlock_LabelsEveryClaim_AndKeepsLayeringRules()
    {
        var block = ClaimRetrieval.PromptBlock([Claim(), Claim(1, 0.5, "confirmed") with
        {
            TopicRef = "mulligan",
            Statement = "Een mulligan gaat altijd naar exact dezelfde handgrootte.",
        }]);

        // Elke claim staat gelabeld in het blok, met topic en bewering.
        Assert.Contains("[community, 4 bronnen, trust 0.94] Deflect:", block);
        Assert.Contains("[community, 1 bron, trust 0.50] (door de officiële regels bevestigd) mulligan:", block);
        // De laag is expliciet gelabeld en draagt nooit het oordeel.
        Assert.Contains("COMMUNITY-INTERPRETATIE", block);
        Assert.Contains("nooit dragen", block);
        // Het antwoordformat: apart Community-consensus-blok + uitgebreid
        // zekerheidslabel (issue #51).
        Assert.Contains("### Community-consensus", block);
        Assert.Contains("Community-consensus (N bronnen)", block);
        Assert.Contains("Bevestigd (officieel)", block);
    }

    [Fact]
    public void PromptBlock_NeverAsksForOwnRuleBasisSection()
    {
        // Citatencontract van #69 geldt ook hier: de instructies mogen het
        // model nooit een eigen "Regelbasis"-blok laten bouwen.
        Assert.DoesNotContain("Regelbasis", ClaimRetrieval.PromptBlock([Claim()]));
    }

    // ── Misvattingen-kanaal (#125) ─────────────────────────────────────

    private static MisconceptionCandidate Candidate(
        long id = 1, string status = "rejected",
        string? reason = "§466.2 zegt dat Deflect alleen gekozen targets blokkeert.",
        double distance = 0.2, string topicRef = "Deflect") =>
        new(id, "mechanic", topicRef,
            "Deflect blokkeert ook spells zonder targets.", status, reason, distance);

    [Fact]
    public void SelectMisconceptions_RejectedZonderWeerlegging_DoetNietMee()
    {
        // Randvoorwaarde uit #125: een kale rejected zonder reden is geen
        // kennis — alleen misvattingen mét weerlegging doen mee.
        var selected = ClaimRetrieval.SelectMisconceptions([
            Candidate(1, reason: null),
            Candidate(2, reason: ""),
            Candidate(3, reason: "   "),
            Candidate(4, reason: "§101 spreekt dit tegen."),
        ]);

        var only = Assert.Single(selected);
        Assert.Equal(4, only.Id);
    }

    [Fact]
    public void SelectMisconceptions_AlleenRejectedOfSuperseded()
    {
        // Accepted/unreviewed claims zijn géén misvattingen — ook niet als er
        // toevallig een StatusReason staat.
        var selected = ClaimRetrieval.SelectMisconceptions([
            Candidate(1, status: "accepted"),
            Candidate(2, status: "unreviewed"),
            Candidate(3, status: "superseded"),
        ]);

        var only = Assert.Single(selected);
        Assert.Equal(3, only.Id);
    }

    [Fact]
    public void SelectMisconceptions_CaptOpTwee_DichtstbijEerst()
    {
        var selected = ClaimRetrieval.SelectMisconceptions([
            Candidate(1, distance: 0.30),
            Candidate(2, distance: 0.10),
            Candidate(3, distance: 0.20),
        ]);

        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { 2L, 3L }, selected.Select(c => c.Id));
    }

    [Fact]
    public void SelectMisconceptions_RespecteertHetAfstandsPlafond()
    {
        // Zelfde plafond als het claims-kanaal (#51): liever géén misvatting
        // dan een misvatting over een ander onderwerp.
        var selected = ClaimRetrieval.SelectMisconceptions([
            Candidate(1, distance: ClaimRetrieval.MaxDistance + 0.01),
        ]);

        Assert.Empty(selected);
    }

    [Fact]
    public void MisconceptionPromptLabel_MetSectie_NoemtDeParagraaf()
    {
        var m = new RetrievedMisconception("mechanic", "Deflect",
            "Deflect blokkeert alles.", "§466.2 zegt dat Deflect alleen gekozen targets blokkeert.");
        Assert.Equal("[misvatting, weerlegd door §466.2]",
            ClaimRetrieval.MisconceptionPromptLabel(m));
    }

    [Fact]
    public void MisconceptionPromptLabel_ZonderSectie_BlijftOfficieelWeerlegd()
    {
        // De beheerders-afwijzing heeft geen §-verwijzing — het label blijft
        // eerlijk generiek in plaats van een sectie te verzinnen.
        var m = new RetrievedMisconception("mechanic", "Deflect",
            "Deflect blokkeert alles.", "door de beheerder afgewezen");
        Assert.Equal("[misvatting, officieel weerlegd]",
            ClaimRetrieval.MisconceptionPromptLabel(m));
    }

    [Fact]
    public void MisconceptionBlock_Empty_WhenNone() =>
        Assert.Equal("", ClaimRetrieval.MisconceptionBlock([]));

    [Fact]
    public void MisconceptionBlock_DwingtDeWeerlegFramingAf()
    {
        // Regressietest in de stijl van StructureFor_NeverAsksForOwnRuleBasisSection
        // (#69/#125): het misvattingen-blok moet om de weerleg-framing vragen —
        // alleen benoemen bij echte gelijkenis, altijd in "Let op", nooit als
        // waarheid — en mag nooit een eigen Regelbasis-sectie uitlokken.
        var block = ClaimRetrieval.MisconceptionBlock([new RetrievedMisconception(
            "mechanic", "Deflect", "Deflect blokkeert ook spells zonder targets.",
            "§466.2 zegt dat Deflect alleen gekozen targets blokkeert.")]);

        Assert.Contains("[misvatting, weerlegd door §466.2] Deflect:", block);
        Assert.Contains("GEDOCUMENTEERDE MISVATTINGEN", block);
        // De framing uit issue #125, letterlijk afgedwongen.
        Assert.Contains("een veelgemaakte lezing is X, maar [n] zegt Y", block);
        Assert.Contains("### Let op", block);
        Assert.Contains("nooit als waarheid", block);
        Assert.Contains("als de vraag er inhoudelijk echt op lijkt", block);
        // #69-contract intact: geen eigen Regelbasis-sectie.
        Assert.DoesNotContain("Regelbasis", block);
    }
}
