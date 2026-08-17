using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De koppeling fase 6 → fase 7 (#231, spec §7): een fase-6
/// <see cref="ErrataLifecycle.Plan"/> laat op claim-niveau een forbidden_claim
/// vervallen en op case-niveau een hele case. Kern: een vervallen forbidden_claim
/// telt niet meer als contradictie (de oude "hallucinatie" is nu waar), en een
/// achterhaalde case wordt overgeslagen — CI faalt daar niet op.</summary>
public class ErrataEvalExpiryTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 1);

    private static EvalCase Case(string id, params ForbiddenClaim[] forbidden) => new()
    {
        Id = id,
        Question = "vraag?",
        QueryType = EvalQueryType.Temporal,
        Status = EvalStatus.Active,
        ForbiddenClaims = forbidden,
    };

    private static ErrataLifecycle.Plan Plan(string erratumRef, params ErrataLifecycle.Invalidation[] inv) =>
        new(TargetRulingRef: null, ErratumRef: erratumRef, Invalidations: inv);

    [Fact]
    public void ForbiddenClaim_Verval_MaaktDeClaimInactief()
    {
        var @case = Case("c1", new ForbiddenClaim("fc:1", "oude regel"));
        var plan = Plan("err:9",
            new ErrataLifecycle.Invalidation("fc:1", ErrataEvalExpiry.ForbiddenClaimFactKind, "erratum keert om"));

        var updated = Assert.Single(ErrataEvalExpiry.Apply([@case], plan));

        var fc = Assert.Single(updated.ForbiddenClaims);
        Assert.False(fc.IsActive);
        Assert.Equal("err:9", fc.SupersededByErratum);
        // De case zelf blijft actief en scoort gewoon door.
        Assert.Null(updated.SupersededByErratum);
    }

    [Fact]
    public void VervallenClaim_TeltNietMeerAlsContradictie()
    {
        var @case = Case("c1", new ForbiddenClaim("fc:1", "oude regel"));
        var plan = Plan("err:9",
            new ErrataLifecycle.Invalidation("fc:1", ErrataEvalExpiry.ForbiddenClaimFactKind, "omgekeerd"));
        var updated = Assert.Single(ErrataEvalExpiry.Apply([@case], plan));

        // Het antwoord produceert de ooit-verboden claim; na verval is dat geen fout.
        var run = new EvalRunResult { ProducedClaims = ["fc:1"] };
        Assert.Empty(EvalScoringService.ViolatedForbiddenClaims(updated, run));
        Assert.Equal(1.0, EvalScoringService.ContradictionRecall(updated, run));

        // En de gate valt er niet op.
        var report = EvalGateEvaluator.Evaluate([(updated, run)], AsOf);
        Assert.True(report.Passed);
    }

    [Fact]
    public void CaseNiveau_Verval_SlaatDeHeleCaseOver()
    {
        var @case = Case("c1", new ForbiddenClaim("fc:1", "oude regel"));
        var plan = Plan("err:9",
            new ErrataLifecycle.Invalidation("c1", ErrataEvalExpiry.EvalCaseFactKind, "hele case achterhaald"));

        var updated = Assert.Single(ErrataEvalExpiry.Apply([@case], plan));

        Assert.Equal("err:9", updated.SupersededByErratum);
        Assert.False(updated.IsInEffect(AsOf)); // wordt overgeslagen
    }

    [Fact]
    public void GeenRaakvlak_LaatDeCaseOngewijzigd()
    {
        var @case = Case("c1", new ForbiddenClaim("fc:1", "oude regel"));
        var plan = Plan("err:9",
            new ErrataLifecycle.Invalidation("fc:andere", ErrataEvalExpiry.ForbiddenClaimFactKind, "n.v.t."));

        var updated = Assert.Single(ErrataEvalExpiry.Apply([@case], plan));

        Assert.Same(@case, updated); // exact dezelfde referentie terug
    }

    [Fact]
    public void ReedsVervallen_WordtNietHeropendDoorTweedeErratum()
    {
        var @case = Case("c1", new ForbiddenClaim("fc:1", "oude regel", SupersededByErratum: "err:eerste"));
        var plan = Plan("err:tweede",
            new ErrataLifecycle.Invalidation("fc:1", ErrataEvalExpiry.ForbiddenClaimFactKind, "opnieuw"));

        var updated = Assert.Single(ErrataEvalExpiry.Apply([@case], plan));

        // Eerste erratum wint; geen stille overschrijving. Ongewijzigde referentie.
        Assert.Same(@case, updated);
        Assert.Equal("err:eerste", Assert.Single(updated.ForbiddenClaims).SupersededByErratum);
    }
}
