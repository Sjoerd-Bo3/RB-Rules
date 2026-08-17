using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Rondt de voorbeeld-gouden-set (RbRules.Tests/Fixtures/poracle-eval-seed.json, #231)
/// door de echte parser — bewijst dat de seed-vorm deserialiseert naar <see
/// cref="EvalCase"/>-records en dat de levenscyclus-velden (shadow-status,
/// claim-niveau errata-verval, gekwalificeerde-interactie-support) correct
/// worden overgenomen. De JSON is de enige bron; hij wordt als fixture gelinkt.</summary>
public class EvalSeedTests
{
    private static IReadOnlyList<EvalCase> LoadSeed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "poracle-eval-seed.json");
        return EvalSeed.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Seed_Parseert_VijfCases_AlleQueryTypes()
    {
        var cases = LoadSeed();

        Assert.Equal(5, cases.Count);
        Assert.Equal(
            [EvalQueryType.Factoid, EvalQueryType.Inference, EvalQueryType.Comparison, EvalQueryType.Temporal],
            cases.Select(c => c.QueryType).Distinct().Order().ToList());
    }

    [Fact]
    public void Seed_ShadowCase_IsShadowEnVanKracht()
    {
        var shadow = LoadSeed().Single(c => c.Id == "eval-shadow-set2-overload");

        Assert.Equal(EvalStatus.Shadow, shadow.Status);
        Assert.True(shadow.IsInEffect(new DateOnly(2026, 7, 19)));   // scoort wél
    }

    [Fact]
    public void Seed_ErrataCase_HeeftVervallenForbiddenClaim()
    {
        // Claim-niveau errata-verval: de forbidden claim draagt een erratum-ref
        // en telt daardoor niet meer als actief.
        var errata = LoadSeed().Single(c => c.Id == "eval-temporal-exhausted-block-errata");

        var claim = Assert.Single(errata.ForbiddenClaims);
        Assert.Equal("errata-2026-03-showdown", claim.SupersededByErratum);
        Assert.False(claim.IsActive);
        Assert.Empty(errata.ActiveForbiddenClaims);
    }

    [Fact]
    public void Seed_InferenceCase_DraagtGekwalificeerdeInteractieSupport()
    {
        // Path-recall (faalmodus 3): de gold-support bevat de conditie-dragende
        // window=showdown-knoop, zodat structuurverlies meetbaar is.
        var deflect = LoadSeed().Single(c => c.Id == "eval-inference-deflect-showdown");

        Assert.Contains("interaction:deflect-showdown#window-showdown", deflect.GoldSupport);
    }

    [Fact]
    public void Seed_PathRecall_MistDeWindowConditie_MeetStructuurverlies()
    {
        // Een run die de mechaniek en het §-fragment ophaalt maar de
        // window=showdown-conditie mist → recall < 1: het structuurverlies
        // wordt een getal (spec §7, faalmodus 3), zonder één LLM-call.
        var deflect = LoadSeed().Single(c => c.Id == "eval-inference-deflect-showdown");
        var run = new EvalRunResult
        {
            RetrievedSupport = ["mechanic:deflect", "section:core-rules-pdf/7.4"],
        };

        Assert.Equal(2.0 / 3, EvalScoringService.Recall(deflect, run), 4);
    }

    [Fact]
    public void Seed_Gate_ShadowBlokkeertNiet_ActiefCleanSlaagt()
    {
        // De hele seed door de gate met "perfecte" runs (alles opgehaald,
        // alleen verwachte citaties, geen verboden claims) → groen, met de
        // shadow-case als observatie i.p.v. als gate-blokker.
        var asOf = new DateOnly(2026, 7, 19);
        var runs = LoadSeed().Select(c => (c, new EvalRunResult
        {
            RetrievedSupport = c.GoldSupport,
            Citations = c.ExpectedCitations,
            ProducedClaims = [],
        })).ToList();

        var report = EvalGateEvaluator.Evaluate(runs, asOf);

        Assert.True(report.Passed);
        Assert.Single(report.ShadowObservations);   // exact de set-2-shadow-case
        Assert.Empty(report.SkippedCaseIds);         // geen retired/verlopen in de seed
    }
}
