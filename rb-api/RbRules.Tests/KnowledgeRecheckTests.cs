using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>#119, de pure koppeling: change → betrokken refs → acties.
/// De AFFECTS-mapper (#104) is de enige bron van "betrokken"; docs hangen er
/// via hun SectionRefs aan (EXPLAINS-semantiek), claims via hun topic
/// (ABOUT-semantiek).</summary>
public class KnowledgeRecheckPlanTests
{
    private static ChangeAffectsMapper Affects() => ChangeAffectsMapper.Create(
        canonicalCards: [("ogn-011-298", "Viktor"), ("ogn-045-001", "Annie")],
        sections: [("core-rules-pdf", "101.2"), ("core-rules-pdf", "205")]);

    private static ClaimTopicMapper Topics() => ClaimTopicMapper.Create(
        cards: [("ogn-011-298", "Viktor", null), ("ogn-045-001", "Annie", null)],
        mechanics: [],
        sections: [("core-rules-pdf", "101.2"), ("core-rules-pdf", "205")],
        concepts: []);

    private static readonly KnowledgeRecheck.DocCandidate DocOnHitSection =
        new(1, "101.2, 205");
    private static readonly KnowledgeRecheck.DocCandidate DocOnOtherSection =
        new(2, "205");
    private static readonly KnowledgeRecheck.ClaimCandidate ClaimOnSection =
        new(10, "section", "101.2");
    private static readonly KnowledgeRecheck.ClaimCandidate ClaimOnCard =
        new(11, "card", "Viktor");
    private static readonly KnowledgeRecheck.ClaimCandidate ClaimOnConcept =
        new(12, "concept", "mulligan");

    [Fact]
    public void PlanFor_CoreRuleChange_RaaktDocEnClaimOpDieSectie()
    {
        var plan = KnowledgeRecheck.PlanFor(
            12, "core-rule", "Regel 101.2 herschreven", null, "+ 101.2 nieuw",
            Affects(), Topics(),
            [DocOnHitSection, DocOnOtherSection],
            [ClaimOnSection, ClaimOnCard, ClaimOnConcept]);

        var mark = Assert.Single(plan.Docs);
        Assert.Equal(1, mark.DocId);
        Assert.Contains("#12", mark.Reason);
        Assert.Contains("§101.2", mark.Reason);
        // §205 is niet geraakt: het doc leunt er wel op, maar de reden noemt
        // alleen wat de change raakt.
        Assert.DoesNotContain("205", mark.Reason);
        Assert.Equal(10, Assert.Single(plan.ClaimIds));
    }

    [Fact]
    public void PlanFor_BanChange_RaaktClaimOverDieKaart_MaarNooitPrimerDocs()
    {
        // Primer-docs leunen op secties; een ban raakt kaarten — docs blijven
        // dus met rust, de kaart-claim gaat opnieuw langs de official-check.
        var plan = KnowledgeRecheck.PlanFor(
            7, "ban", "Viktor is banned in alle formats", null, null,
            Affects(), Topics(),
            [DocOnHitSection], [ClaimOnSection, ClaimOnCard]);

        Assert.Empty(plan.Docs);
        Assert.Equal(11, Assert.Single(plan.ClaimIds));
    }

    [Theory]
    [InlineData("set-release")]
    [InlineData("editorial")]
    [InlineData(null)]
    public void PlanFor_ChangeZonderDoelsoort_IsLeegPlan(string? changeType)
    {
        var plan = KnowledgeRecheck.PlanFor(
            3, changeType, "Regel 101.2 en Viktor genoemd", null, null,
            Affects(), Topics(), [DocOnHitSection], [ClaimOnSection, ClaimOnCard]);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void PlanFor_GeenMatchendeTekst_IsLeegPlan_NooitEenCrash()
    {
        var plan = KnowledgeRecheck.PlanFor(
            4, "core-rule", "Alleen redactionele aanpassingen.", null, null,
            Affects(), Topics(), [DocOnHitSection], [ClaimOnSection]);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void PlanFor_DocZonderSectionRefs_WordtNooitGeraakt()
    {
        var plan = KnowledgeRecheck.PlanFor(
            5, "core-rule", "101.2 gewijzigd", null, null,
            Affects(), Topics(),
            [new KnowledgeRecheck.DocCandidate(9, null)], []);
        Assert.Empty(plan.Docs);
    }
}

/// <summary>#119: de reden reist zonder migratie mee — als kanttekening
/// vooraan in de doc-tekst (het #110-patroon) en als StatusReason-prefix op
/// de claim.</summary>
public class KnowledgeRecheckMarkerTests
{
    private const string Body = "Eerste alinea.\n\nTweede alinea (§101.2).";

    [Fact]
    public void AddMarker_ZetKanttekeningVooraan_EnStapeltDezelfdeNooit()
    {
        var marker = KnowledgeRecheck.Marker(KnowledgeRecheck.DocReason(12, ["101.2"]));
        var once = KnowledgeRecheck.AddMarker(Body, marker);
        var twice = KnowledgeRecheck.AddMarker(once, marker);

        Assert.StartsWith("[hertoetsing: regelwijziging #12 raakt §101.2", once);
        Assert.EndsWith(Body, once);
        Assert.Equal(once, twice); // idempotent: her-run stapelt niet
    }

    [Fact]
    public void AddMarker_AndereChange_KrijgtEigenRegel_EnStripVerwijdertBeide()
    {
        var m12 = KnowledgeRecheck.Marker(KnowledgeRecheck.DocReason(12, ["101.2"]));
        var m13 = KnowledgeRecheck.Marker(KnowledgeRecheck.DocReason(13, ["205"]));
        var body = KnowledgeRecheck.AddMarker(KnowledgeRecheck.AddMarker(Body, m12), m13);

        Assert.Equal(2, KnowledgeRecheck.MarkerReasons(body).Count);
        // Goedkeuren stript álle kanttekeningen: de tekst is daarna weer
        // byte-voor-byte de tekst waar de embedding bij hoort.
        Assert.Equal(Body, KnowledgeRecheck.StripMarkers(body));
    }

    [Fact]
    public void StripMarkers_ZonderKanttekening_LaatTekstExactStaan()
    {
        Assert.Equal(Body, KnowledgeRecheck.StripMarkers(Body));
        // Ook een tekst die zelf met lege regels begint blijft ongemoeid.
        Assert.Equal("\n\nTekst.", KnowledgeRecheck.StripMarkers("\n\nTekst."));
    }

    [Fact]
    public void MarkerReasons_ZonderKanttekening_IsLeeg() =>
        Assert.Empty(KnowledgeRecheck.MarkerReasons(Body));

    [Fact]
    public void Marker_SluitendeBlokhaakInReden_BlijftEenRegelKanttekening()
    {
        // "]" in de reden zou de kanttekening-regel vroegtijdig afsluiten —
        // gesaneerd naar ")" blijft parse en strip kloppen.
        var marker = KnowledgeRecheck.Marker("reden met ] blokhaak");
        var body = KnowledgeRecheck.AddMarker(Body, marker);
        Assert.Equal("reden met ) blokhaak", Assert.Single(KnowledgeRecheck.MarkerReasons(body)));
        Assert.Equal(Body, KnowledgeRecheck.StripMarkers(body));
    }

    [Fact]
    public void ApplyContradicted_AcceptedWordtSuperseded_MetHerleidbareReden()
    {
        var (status, reason) = KnowledgeRecheck.ApplyContradicted(
            12, "accepted", "§101.2 zegt het tegendeel");
        Assert.Equal("superseded", status);
        Assert.StartsWith(KnowledgeRecheck.ClaimReasonPrefix, reason);
        Assert.Contains("#12", reason);
        Assert.Contains("§101.2 zegt het tegendeel", reason);
    }

    [Fact]
    public void ApplyContradicted_ZonderOordeelReden_KrijgtStandaardtekst()
    {
        var (status, reason) = KnowledgeRecheck.ApplyContradicted(12, "unreviewed", null);
        Assert.Equal("rejected", status); // zelfde semantiek als de claims-hertoets
        Assert.Contains("de officiële regels spreken deze claim tegen", reason);
    }
}
