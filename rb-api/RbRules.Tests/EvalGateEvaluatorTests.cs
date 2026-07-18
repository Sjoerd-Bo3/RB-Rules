using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De CI-gate van de eval-harness (#231, Ring A). Kern: shadow-cases
/// scoren wél maar blokkeren de gate niet (cold-start B4), retired/achterhaalde
/// cases worden overgeslagen, en een claim-niveau errata-verval laat CI niet
/// falen op een claim die inmiddels klopt (Kritiek C). De harde poorten zijn
/// citation-validity (100%) en nul geproduceerde actieve forbidden claims.</summary>
public class EvalGateEvaluatorTests
{
    private static readonly DateOnly AsOf = new(2026, 7, 19);

    private static EvalCase Case(
        string id = "c",
        EvalStatus status = EvalStatus.Active,
        IReadOnlyList<string>? gold = null,
        IReadOnlyList<string>? citations = null,
        IReadOnlyList<ForbiddenClaim>? forbidden = null,
        DateOnly? validFrom = null,
        DateOnly? validUntil = null,
        string? supersededByErratum = null) => new()
    {
        Id = id,
        Question = "vraag?",
        QueryType = EvalQueryType.Inference,
        Status = status,
        GoldSupport = gold ?? [],
        ExpectedCitations = citations ?? [],
        ForbiddenClaims = forbidden ?? [],
        ValidFrom = validFrom ?? DateOnly.MinValue,
        ValidUntil = validUntil,
        SupersededByErratum = supersededByErratum,
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

    [Fact]
    public void Evaluate_ActieveCleanRun_Slaagt()
    {
        var report = EvalGateEvaluator.Evaluate(
            [(Case(gold: ["g1"], citations: ["s1"]), Run(retrieved: ["g1"], cited: ["s1"]))],
            AsOf);

        Assert.True(report.Passed);
        Assert.Empty(report.GatingFailures);
        Assert.Empty(report.SkippedCaseIds);
        Assert.True(Assert.Single(report.Results).CountedTowardGate);
    }

    [Fact]
    public void Evaluate_ActieveForbiddenClaim_Faalt()
    {
        var @case = Case(forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);
        var report = EvalGateEvaluator.Evaluate([(@case, Run(claims: ["fc1"]))], AsOf);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.GatingFailures);
        Assert.Contains("forbidden-claim", Assert.Single(failure.Violations));
    }

    [Fact]
    public void Evaluate_VerzonnenCitatie_Faalt()
    {
        // s2 zit niet in de verwachte set → verzonnen citatie → harde gate.
        var @case = Case(citations: ["s1"]);
        var report = EvalGateEvaluator.Evaluate([(@case, Run(cited: ["s1", "s2"]))], AsOf);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.GatingFailures);
        Assert.Contains("citation-validity", Assert.Single(failure.Violations));
    }

    [Fact]
    public void Evaluate_ShadowCaseFaalt_MaarBlokkeertGateNiet()
    {
        // Cold-start B4: shadow scoort en rapporteert, maar telt niet mee.
        var shadow = Case(status: EvalStatus.Shadow,
            forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);
        var report = EvalGateEvaluator.Evaluate([(shadow, Run(claims: ["fc1"]))], AsOf);

        Assert.True(report.Passed);            // gate niet geblokkeerd
        Assert.Empty(report.GatingFailures);
        var obs = Assert.Single(report.ShadowObservations);
        Assert.False(obs.CountedTowardGate);
        Assert.NotEmpty(obs.Violations);       // wél gerapporteerd
    }

    [Fact]
    public void Evaluate_RetiredCase_Genegeerd()
    {
        var retired = Case(status: EvalStatus.Retired,
            forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);
        var report = EvalGateEvaluator.Evaluate([(retired, Run(claims: ["fc1"]))], AsOf);

        Assert.True(report.Passed);
        Assert.Empty(report.Results);
        Assert.Equal("c", Assert.Single(report.SkippedCaseIds));
    }

    [Fact]
    public void Evaluate_DoorErratumAchterhaaldeCase_Overgeslagen()
    {
        // Case-niveau errata-verval (Kritiek C): de hele case wacht op herziening.
        var superseded = Case(supersededByErratum: "errata-x",
            forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);
        var report = EvalGateEvaluator.Evaluate([(superseded, Run(claims: ["fc1"]))], AsOf);

        Assert.True(report.Passed);
        Assert.Empty(report.Results);
        Assert.Single(report.SkippedCaseIds);
    }

    [Fact]
    public void Evaluate_ClaimNiveauErrataVerval_FaaltNiet()
    {
        // Kritiek C op claim-niveau: de case blijft active, maar de enige
        // forbidden claim is door een erratum omgekeerd. Het antwoord mag hem
        // nu produceren zonder de CI te breken.
        var @case = Case(forbidden:
            [new ForbiddenClaim("fc1", "nu waar", SupersededByErratum: "errata-x")]);
        var report = EvalGateEvaluator.Evaluate([(@case, Run(claims: ["fc1"]))], AsOf);

        Assert.True(report.Passed);
        Assert.Empty(Assert.Single(report.Results).Violations);
    }

    [Fact]
    public void Evaluate_VerlopenCase_Overgeslagen()
    {
        var expired = Case(validUntil: new DateOnly(2026, 1, 1));
        var report = EvalGateEvaluator.Evaluate([(expired, Run())], AsOf);

        Assert.Single(report.SkippedCaseIds);
        Assert.Empty(report.Results);
    }

    [Fact]
    public void Evaluate_NogNietGeldigeCase_Overgeslagen()
    {
        var future = Case(validFrom: new DateOnly(2030, 1, 1));
        var report = EvalGateEvaluator.Evaluate([(future, Run())], AsOf);

        Assert.Single(report.SkippedCaseIds);
        Assert.Empty(report.Results);
    }

    [Fact]
    public void Evaluate_MinRecallDrempel_FaaltOnderDrempel()
    {
        // recall = 1/3 < 0.5 → violation.
        var @case = Case(gold: ["a", "b", "c"]);
        var report = EvalGateEvaluator.Evaluate(
            [(@case, Run(retrieved: ["a"]))], AsOf, minRecall: 0.5);

        Assert.False(report.Passed);
        Assert.Contains("path-recall", Assert.Single(Assert.Single(report.GatingFailures).Violations));
    }

    [Fact]
    public void Evaluate_ZonderMinRecall_GeenRecallGate()
    {
        // Dezelfde lage recall, maar zonder drempel → geen violation.
        var @case = Case(gold: ["a", "b", "c"]);
        var report = EvalGateEvaluator.Evaluate([(@case, Run(retrieved: ["a"]))], AsOf);

        Assert.True(report.Passed);
        Assert.Empty(Assert.Single(report.Results).Violations);
    }

    [Fact]
    public void Evaluate_GemengdeSet_ShadowFaaltActiefSlaagt_GateBlijftGroen()
    {
        // Realistische cold-start-mix: één active clean, één shadow met fout.
        var active = Case(id: "active", citations: ["s1"], gold: ["g1"]);
        var shadow = Case(id: "shadow", status: EvalStatus.Shadow,
            forbidden: [new ForbiddenClaim("fc1", "hallucinatie")]);

        var report = EvalGateEvaluator.Evaluate(
        [
            (active, Run(retrieved: ["g1"], cited: ["s1"])),
            (shadow, Run(claims: ["fc1"])),
        ], AsOf);

        Assert.True(report.Passed);
        Assert.Equal(2, report.Results.Count);
        Assert.Single(report.ShadowObservations);
        Assert.Empty(report.GatingFailures);
    }
}
