using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure scoring van de eval-harness (#231): Recall/Relevancy/F1-
/// randgevallen, citatie-precisie en contradiction-recall — met de
/// errata-verval-invariant als kern (een door-erratum-omgekeerde forbidden
/// claim telt niet meer als fout).</summary>
public class EvalScoringServiceTests
{
    private static EvalCase Case(
        IReadOnlyList<string>? gold = null,
        IReadOnlyList<string>? citations = null,
        IReadOnlyList<ForbiddenClaim>? forbidden = null) => new()
    {
        Id = "c",
        Question = "vraag?",
        QueryType = EvalQueryType.Factoid,
        Status = EvalStatus.Active,
        GoldSupport = gold ?? [],
        ExpectedCitations = citations ?? [],
        ForbiddenClaims = forbidden ?? [],
    };

    private static EvalRunResult Run(
        IReadOnlyList<string>? retrieved = null,
        IReadOnlyList<string>? cited = null,
        IReadOnlyList<string>? claims = null) => new()
    {
        RetrievedSupport = retrieved ?? [],
        Citations = cited ?? [],
        ProducedClaims = claims ?? [],
    };

    // --- Recall ---

    [Fact]
    public void Recall_LegeGold_IsEen()
    {
        // Vacuüm-conventie: niets vereist → niets gemist → 1.0.
        Assert.Equal(1.0, EvalScoringService.Recall(Case(gold: []), Run(retrieved: ["a"])));
    }

    [Fact]
    public void Recall_VolledigeMatch_IsEen()
    {
        var recall = EvalScoringService.Recall(
            Case(gold: ["a", "b"]), Run(retrieved: ["a", "b", "c"]));
        Assert.Equal(1.0, recall);
    }

    [Fact]
    public void Recall_DeelsMatch_IsFractie()
    {
        // 1 van 3 gold-ids opgehaald (de andere twee gemist) → 1/3.
        var recall = EvalScoringService.Recall(
            Case(gold: ["a", "b", "c"]), Run(retrieved: ["a", "x"]));
        Assert.Equal(1.0 / 3, recall, 4);
    }

    [Fact]
    public void Recall_GeenMatch_IsNul()
    {
        Assert.Equal(0.0, EvalScoringService.Recall(Case(gold: ["a"]), Run(retrieved: ["x"])));
    }

    // --- Relevancy (retrieval-precisie) ---

    [Fact]
    public void Relevancy_LegeRetrieval_IsEen()
    {
        Assert.Equal(1.0, EvalScoringService.Relevancy(Case(gold: ["a"]), Run(retrieved: [])));
    }

    [Fact]
    public void Relevancy_HelftRuis_IsHalf()
    {
        var relevancy = EvalScoringService.Relevancy(
            Case(gold: ["a"]), Run(retrieved: ["a", "x"]));
        Assert.Equal(0.5, relevancy);
    }

    // --- F1 ---

    [Fact]
    public void F1_BeideNul_IsNul_GeenDelingDoorNul()
    {
        Assert.Equal(0.0, EvalScoringService.F1(0.0, 0.0));
    }

    [Fact]
    public void F1_HarmonischGemiddelde()
    {
        // precisie 0.5, recall 1.0 → 2*0.5*1/1.5 = 0.6667.
        Assert.Equal(2.0 / 3, EvalScoringService.F1(0.5, 1.0), 4);
    }

    // --- Citation-precisie ---

    [Fact]
    public void CitationPrecision_NietsGeciteerd_IsEen()
    {
        Assert.Equal(1.0, EvalScoringService.CitationPrecision(
            Case(citations: ["s1"]), Run(cited: [])));
    }

    [Fact]
    public void CitationPrecision_EenVerzonnenCitatie_VerlaagtPrecisie()
    {
        // s1 verwacht, s2 verzonnen → 1/2.
        var precision = EvalScoringService.CitationPrecision(
            Case(citations: ["s1"]), Run(cited: ["s1", "s2"]));
        Assert.Equal(0.5, precision);
    }

    [Fact]
    public void CitationPrecision_MinderMaarGeldig_IsEen()
    {
        // Onvolledig (s2 niet geciteerd) maar geen verzonnen id → precisie 1.0.
        // Completeness is een aparte, hier niet-gemeten as.
        var precision = EvalScoringService.CitationPrecision(
            Case(citations: ["s1", "s2"]), Run(cited: ["s1"]));
        Assert.Equal(1.0, precision);
    }

    // --- Contradiction-recall & forbidden claims ---

    [Fact]
    public void ContradictionRecall_ForbiddenClaimAanwezig_IsNul()
    {
        var @case = Case(forbidden: [new ForbiddenClaim("fc1", "een hallucinatie")]);
        var run = Run(claims: ["fc1", "iets-anders"]);

        Assert.Equal(0.0, EvalScoringService.ContradictionRecall(@case, run));
        Assert.Single(EvalScoringService.ViolatedForbiddenClaims(@case, run));
    }

    [Fact]
    public void ContradictionRecall_ForbiddenClaimVermeden_IsEen()
    {
        var @case = Case(forbidden: [new ForbiddenClaim("fc1", "een hallucinatie")]);
        var run = Run(claims: ["iets-onschuldigs"]);

        Assert.Equal(1.0, EvalScoringService.ContradictionRecall(@case, run));
        Assert.Empty(EvalScoringService.ViolatedForbiddenClaims(@case, run));
    }

    [Fact]
    public void ContradictionRecall_NaErrataVerval_GeenFout()
    {
        // Kritiek C: dezelfde claim is ná een erratum WAAR geworden
        // (SupersededByErratum gezet). Ook al produceert het antwoord hem, het
        // telt niet meer als contradictie — recall blijft 1.0, geen violation.
        var @case = Case(forbidden:
            [new ForbiddenClaim("fc1", "was fout, nu waar", SupersededByErratum: "errata-x")]);
        var run = Run(claims: ["fc1"]);

        Assert.Empty(@case.ActiveForbiddenClaims);
        Assert.Equal(1.0, EvalScoringService.ContradictionRecall(@case, run));
        Assert.Empty(EvalScoringService.ViolatedForbiddenClaims(@case, run));
    }

    [Fact]
    public void ContradictionRecall_ActiefEnVervallenGemengd_TeltAlleenActief()
    {
        // fc1 actief (blijft fout), fc2 door erratum vervallen. Beide
        // geproduceerd → alleen fc1 is een overtreding; noemer = 1 actieve.
        var @case = Case(forbidden:
        [
            new ForbiddenClaim("fc1", "nog steeds fout"),
            new ForbiddenClaim("fc2", "vervallen", SupersededByErratum: "errata-x"),
        ]);
        var run = Run(claims: ["fc1", "fc2"]);

        Assert.Equal(0.0, EvalScoringService.ContradictionRecall(@case, run));
        var violated = EvalScoringService.ViolatedForbiddenClaims(@case, run);
        Assert.Equal("fc1", Assert.Single(violated).Id);
    }

    [Fact]
    public void Score_BundeltAlleVierDeMeetlagen()
    {
        var @case = Case(
            gold: ["g1", "g2"],
            citations: ["s1"],
            forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);
        var run = Run(retrieved: ["g1", "g2"], cited: ["s1"], claims: ["ok"]);

        var m = EvalScoringService.Score(@case, run);

        Assert.Equal(1.0, m.Recall);
        Assert.Equal(1.0, m.Relevancy);
        Assert.Equal(1.0, m.F1);
        Assert.Equal(1.0, m.CitationPrecision);
        Assert.Equal(1.0, m.ContradictionRecall);
    }
}
