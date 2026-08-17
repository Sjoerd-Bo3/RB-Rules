using RbRules.Domain;

namespace RbRules.Tests;

public class ChangeConsolidationGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 6, 46, 0, TimeSpan.Zero);

    private static ChangeConsolidationCandidate Candidate(
        string changeType, DateTimeOffset detectedAt, string sourceId, params BrainRef[] refs) =>
        new(changeType, detectedAt, sourceId, refs);

    [Fact]
    public void IsCandidate_16JuliBans_MatchtBeideKanten()
    {
        // Exact het terugwerkende scenario uit issue #206: Rules Hub 06:46
        // (officieel), Mobalytics 06:51 (community), zelfde kaart gebanned.
        var official = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var community = Candidate("ban", Now.AddMinutes(5), "mobalytics", BrainRef.Card("ogn-001"));

        Assert.True(ChangeConsolidationGate.IsCandidate(official, community));
        Assert.True(ChangeConsolidationGate.IsCandidate(community, official));
    }

    [Fact]
    public void IsCandidate_VerschillendChangeType_IsGeenKandidaat()
    {
        var a = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("errata", Now.AddMinutes(5), "mobalytics", BrainRef.Card("ogn-001"));
        Assert.False(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_ZelfdeBron_IsGeenKandidaat()
    {
        // Twee changes van dezelfde bron zijn nooit een consolidatie-paar —
        // dat is gewoon een opeenvolgende wijziging, geen cross-source-bevestiging.
        var a = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("ban", Now.AddMinutes(5), "rules-hub", BrainRef.Card("ogn-001"));
        Assert.False(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_BuitenVenster_IsGeenKandidaat()
    {
        var a = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("ban", Now + ChangeConsolidationGate.Window + TimeSpan.FromMinutes(1),
            "mobalytics", BrainRef.Card("ogn-001"));
        Assert.False(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_PreciesOpDeRandVanHetVenster_IsNogWelKandidaat()
    {
        var a = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("ban", Now + ChangeConsolidationGate.Window, "mobalytics", BrainRef.Card("ogn-001"));
        Assert.True(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_GeenOverlappendeRefs_IsGeenKandidaat()
    {
        var a = Candidate("ban", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("ban", Now.AddMinutes(5), "mobalytics", BrainRef.Card("ogn-002"));
        Assert.False(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_GeenBruikbareRefsAanEenKant_IsGeenKandidaat()
    {
        // Liever twee kaarten in de feed dan een fout gekoppeld paar (#206-eis):
        // zonder refs aan een van beide kanten is er nooit een kandidaat, ook
        // al kloppen type/bron/venster.
        var a = Candidate("ban", Now, "rules-hub");
        var b = Candidate("ban", Now.AddMinutes(5), "mobalytics", BrainRef.Card("ogn-001"));
        Assert.False(ChangeConsolidationGate.IsCandidate(a, b));
        Assert.False(ChangeConsolidationGate.IsCandidate(b, a));
    }

    [Fact]
    public void IsCandidate_PoortZelfToetstAlleenTypeBronVensterEnRefs()
    {
        // De poort krijgt kant-en-klare Refs binnen en her-beoordeelt het
        // ChangeType zelf niet — dat "set-release/editorial/unknown leveren
        // nooit refs op" is de verantwoordelijkheid van de aanroeper
        // (ChangeAffectsMapper.Resolve, al gedekt door
        // BrainRefMappingTests.Resolve_NonRuleNonCardTypes_YieldNoTargets).
        // Krijgt de poort hier toch refs voor zo'n type, dan is dat gewoon
        // een kandidaat — dit pint die grens vast.
        var a = Candidate("set-release", Now, "rules-hub", BrainRef.Card("ogn-001"));
        var b = Candidate("set-release", Now.AddMinutes(5), "mobalytics", BrainRef.Card("ogn-001"));
        Assert.True(ChangeConsolidationGate.IsCandidate(a, b));
    }

    [Fact]
    public void IsCandidate_SectionRefsOverlappen_IsKandidaat()
    {
        var a = Candidate("core-rule", Now, "core-rules-pdf",
            BrainRef.Section("core-rules-pdf", "7.4"));
        var b = Candidate("core-rule", Now.AddHours(1), "rules-hub",
            BrainRef.Section("core-rules-pdf", "7.4"));
        Assert.True(ChangeConsolidationGate.IsCandidate(a, b));
    }
}

public class ChangeConsolidationPrimaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Wins_LagereTrustTierWintAltijd_OngeachtDatum()
    {
        // Officieel (tier 1), later gedetecteerd, wint alsnog van community
        // (tier 3) die eerder was.
        Assert.True(ChangeConsolidationPrimary.Wins(
            tierA: 1, detectedAtA: Now.AddMinutes(5), tierB: 3, detectedAtB: Now));
        Assert.False(ChangeConsolidationPrimary.Wins(
            tierA: 3, detectedAtA: Now, tierB: 1, detectedAtB: Now.AddMinutes(5)));
    }

    [Fact]
    public void Wins_GelijkeTrust_VroegsteDetectieWint()
    {
        Assert.True(ChangeConsolidationPrimary.Wins(
            tierA: 3, detectedAtA: Now, tierB: 3, detectedAtB: Now.AddMinutes(5)));
        Assert.False(ChangeConsolidationPrimary.Wins(
            tierA: 3, detectedAtA: Now.AddMinutes(5), tierB: 3, detectedAtB: Now));
    }

    [Fact]
    public void Wins_VolledigGelijkeStand_ABlijftPrimair()
    {
        // De aanroeper geeft hier altijd de bestaande root als A door — bij
        // een exacte gelijke stand verdringt een nieuwkomer die nooit.
        Assert.True(ChangeConsolidationPrimary.Wins(tierA: 2, detectedAtA: Now, tierB: 2, detectedAtB: Now));
    }
}

public class ChangeEventJudgeTests
{
    [Fact]
    public void Parse_SameEventTrue_ReturnsJudgement()
    {
        var j = ChangeEventJudge.Parse("""{"sameEvent": true}""");
        Assert.NotNull(j);
        Assert.True(j.SameEvent);
    }

    [Fact]
    public void Parse_SameEventFalse_ReturnsJudgement()
    {
        var j = ChangeEventJudge.Parse("""{"sameEvent": false}""");
        Assert.NotNull(j);
        Assert.False(j.SameEvent);
    }

    [Theory]
    [InlineData("""{"sameEvent": "yes"}""")]   // geen boolean
    [InlineData("""{"same_event": true}""")]   // verkeerde sleutel
    [InlineData("{}")]
    [InlineData("geen json")]
    [InlineData("")]
    public void Parse_UnusableOutput_ReturnsNull(string raw) =>
        // null ⇒ de aanroeper behandelt het paar als NIET geconsolideerd
        // (de veilige kant, zelfde discipline als ClaimJudge).
        Assert.Null(ChangeEventJudge.Parse(raw));

    [Fact]
    public void Parse_ProseAndFenceAroundJson_IsTolerated()
    {
        // Zelfde tolerantie als ClaimJudge/RelationTriage (#93/#188): prose
        // met een "[1]"-marker vóór de JSON en een fence eromheen mogen het
        // oordeel niet onbruikbaar maken.
        var raw = """
            Both changes mention the same card [1] and effective date:
            ```json
            {"sameEvent": true}
            ```
            """;
        var j = ChangeEventJudge.Parse(raw);
        Assert.NotNull(j);
        Assert.True(j.SameEvent);
    }

    [Fact]
    public void Parse_BareArrayFromCitationMarker_NeverThrows_ReturnsNull()
    {
        // Objectvorm-guard (#188-les): LlmJson.Candidates levert ook
        // array-vormige blokken op uit toevallige bronvermeldingen ("[1]") —
        // zonder de guard zou TryGetProperty op een niet-object root een
        // InvalidOperationException gooien in plaats van netjes te degraderen.
        var j = ChangeEventJudge.Parse("See source [1] for details.");
        Assert.Null(j);
    }

    [Fact]
    public void BuildPrompt_BevatBeideBronnenEnTekst()
    {
        var p = ChangeEventJudge.BuildPrompt(
            "Rules Hub", "Viktor is banned.", "- Viktor\n+ Viktor (banned)",
            "Mobalytics", "Viktor banned in constructed.", null);
        Assert.Contains("Rules Hub", p);
        Assert.Contains("Mobalytics", p);
        Assert.Contains("Viktor is banned.", p);
        Assert.Contains("Viktor banned in constructed.", p);
    }
}
