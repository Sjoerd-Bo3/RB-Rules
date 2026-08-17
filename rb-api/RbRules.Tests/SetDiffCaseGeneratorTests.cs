using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Auto-generatie van kandidaat-eval-cases uit een set/errata-diff (#231,
/// spec §7, cold-start stap 3/4). Kern: alles landt in SHADOW (breekt de CI van main
/// niet vóór menselijke curatie); een erratum levert een Temporal-case met de oude
/// bewoording als forbidden_claim; ids zijn deterministisch (idempotente reviewqueue).</summary>
public class SetDiffCaseGeneratorTests
{
    private static readonly DateOnly Released = new(2026, 8, 1);

    [Fact]
    public void NieuweKaart_LevertShadowFactoid()
    {
        var diff = new SetDiff("ogn2",
            NewCards: [new NewCardFact("card:ogn2-001", "Poro Herald", "cost:ogn2-001")],
            NewConcepts: [],
            Errata: []);

        var c = Assert.Single(SetDiffCaseGenerator.Generate(diff, Released));

        Assert.Equal("evc:ogn2:card:card:ogn2-001", c.Id);
        Assert.Equal(EvalStatus.Shadow, c.Status);
        Assert.Equal(EvalQueryType.Factoid, c.QueryType);
        Assert.Equal(Released, c.ValidFrom);
        Assert.Contains("card:ogn2-001", c.GoldSupport);
        Assert.Contains("cost:ogn2-001", c.GoldSupport);
    }

    [Fact]
    public void NieuwKeyword_LevertShadowInferenceMetCitatie()
    {
        var diff = new SetDiff("ogn2",
            NewCards: [],
            NewConcepts: [new NewConceptFact("kw:accelerate", "Accelerate", "§4.2-accelerate")],
            Errata: []);

        var c = Assert.Single(SetDiffCaseGenerator.Generate(diff, Released));

        Assert.Equal(EvalStatus.Shadow, c.Status);
        Assert.Equal(EvalQueryType.Inference, c.QueryType);
        Assert.Contains("§4.2-accelerate", c.GoldSupport);
        Assert.Equal(["§4.2-accelerate"], c.ExpectedCitations);
    }

    [Fact]
    public void Erratum_LevertTemporalMetForbiddenClaim()
    {
        var diff = new SetDiff("ogn2",
            NewCards: [],
            NewConcepts: [],
            Errata:
            [
                new ErratumFact("err:001", "kw:deflect",
                    PreErrataClaim: "Deflect voorkomt alle schade",
                    RuleSectionId: "§7.4-showdown-damage"),
            ]);

        var c = Assert.Single(SetDiffCaseGenerator.Generate(diff, Released));

        Assert.Equal(EvalStatus.Shadow, c.Status);
        Assert.Equal(EvalQueryType.Temporal, c.QueryType);
        var fc = Assert.Single(c.ForbiddenClaims);
        Assert.Equal("fc:err:001:preerrata", fc.Id);
        Assert.Equal("Deflect voorkomt alle schade", fc.Text);
        Assert.True(fc.IsActive); // nog niet zelf omgekeerd
        Assert.Contains("§7.4-showdown-damage", c.GoldSupport);
    }

    [Fact]
    public void Generatie_IsDeterministisch()
    {
        var diff = new SetDiff("ogn2",
            NewCards: [new NewCardFact("card:x", "X")],
            NewConcepts: [],
            Errata: []);

        var a = SetDiffCaseGenerator.Generate(diff, Released);
        var b = SetDiffCaseGenerator.Generate(diff, Released);

        Assert.Equal(a.Select(x => x.Id), b.Select(x => x.Id));
    }

    [Fact]
    public void GegenereerdeCases_ZijnVanKrachtOpReleaseMaarGatenNiet()
    {
        var diff = new SetDiff("ogn2",
            NewCards: [new NewCardFact("card:x", "X")],
            NewConcepts: [],
            Errata: []);

        var c = Assert.Single(SetDiffCaseGenerator.Generate(diff, Released));

        // Van kracht op de peildatum, maar shadow → telt niet voor de gate.
        Assert.True(c.IsInEffect(Released));
        Assert.NotEqual(EvalStatus.Active, c.Status);
    }
}
