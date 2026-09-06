using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Eval-gevallen uit echt verkeer (#387): id-vocabulaire, het parsen van
/// de §-lijst uit een trace (mét de blokhaak-markers van #100/#152/#364), de
/// vraagklasse-mapping en de afbeelding van een echt antwoord op de harness-
/// vorm.</summary>
public class EvalCasePromotionTests
{
    [Fact]
    public void SectionIdsFromTrace_SlaatMarkersOver_EnDedupet()
    {
        var ids = EvalCasePromotion.SectionIdsFromTrace(
            "[embedding-uitval: vector-kanalen overgeslagen] [§-bijgeladen: 2] §101, §466.2.c, §101");
        Assert.Equal(["section:101", "section:466.2.c"], ids);
        Assert.Empty(EvalCasePromotion.SectionIdsFromTrace(null));
        Assert.Empty(EvalCasePromotion.SectionIdsFromTrace("[kanaal-uitval: fts]"));
    }

    [Theory]
    [InlineData("Ruling", "Mag ik Deflect gebruiken tijdens een showdown?", EvalQueryType.Inference)]
    [InlineData("Interactie", "Wat gebeurt er als Stun en Exhaust samenkomen?", EvalQueryType.Inference)]
    [InlineData("Definitie", "Wat is het verschil tussen Stun en Exhaust?", EvalQueryType.Comparison)]
    [InlineData("Kaart", "Wat kost Mountain Drake?", EvalQueryType.Factoid)]
    [InlineData("Legaliteit", "Is Viktor sinds het erratum nog toegestaan?", EvalQueryType.Temporal)]
    public void QueryTypeFor_MaptRouterTypeEnVraagtekst(string type, string q, EvalQueryType expected) =>
        Assert.Equal(expected, EvalCasePromotion.QueryTypeFor(type, q));

    [Fact]
    public void CaseId_IsStabielLeesbaarEnBegrensd()
    {
        var a = EvalCasePromotion.CaseId("Mag een exhausted unit blokkeren?");
        var b = EvalCasePromotion.CaseId("  mag een EXHAUSTED unit blokkeren?  ");
        Assert.StartsWith("eval-mag-een-exhausted-unit-blokkeren-", a);
        Assert.Equal(a, b); // hoofdletters/witruimte maken geen ander geval
        var lang = EvalCasePromotion.CaseId(string.Join(" ", Enumerable.Repeat("woord", 30)));
        Assert.True(lang.Length <= 5 + 40 + 1 + 6 + 1, lang);
    }

    [Fact]
    public void Draft_IsShadow_MetCitatiesAlsGoldEnVerwachting()
    {
        var c = EvalCasePromotion.Draft("Vraag?", "Ruling", ["section:101", "section:101", "card:x"],
            new DateOnly(2026, 9, 6));
        Assert.Equal(EvalStatus.Shadow, c.Status);
        Assert.Equal(new DateOnly(2026, 9, 6), c.ValidFrom);
        Assert.Equal(["section:101", "card:x"], c.GoldSupport);
        Assert.Equal(c.GoldSupport, c.ExpectedCitations);
        Assert.Equal(EvalQueryType.Inference, c.QueryType);
    }

    [Fact]
    public void ToRunResult_CiteertAlleenSecties_EnDetecteertVerbodenClaimLexicaal()
    {
        var forbidden = new List<ForbiddenClaim>
        {
            new("fc-1", "exhausted units can block"),
            new("fc-2", "deflect stacks"),
        };
        var r = EvalRunMapping.ToRunResult(
            ["101", null, "466.2.c", "101"],
            "**Oordeel:** No. Exhausted units can block only when readied.", forbidden);
        Assert.Equal(["section:101", "section:466.2.c"], r.Citations);
        Assert.Equal(r.Citations, r.RetrievedSupport);
        Assert.Equal(["fc-1"], r.ProducedClaims);
    }

    [Fact]
    public void ClassSummary_PerKlasse_MetAantal()
    {
        var samples = new List<ClassifiedSample>
        {
            new(EvalQueryType.Factoid, EvalMetricNames.Recall, 1.0, true),
            new(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 0.5, true),
            new(EvalQueryType.Factoid, EvalMetricNames.Recall, 0.0, false),
            new(EvalQueryType.Factoid, EvalMetricNames.CitationPrecision, 1.0, false),
        };
        Assert.Equal("Factoid: recall 0.50, citaties 0.75 (n=2)", EvalRunMapping.ClassSummary(samples));
        Assert.Equal("geen samples", EvalRunMapping.ClassSummary([]));
    }
}
