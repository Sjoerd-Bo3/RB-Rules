using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De token-verdeling tussen rewrite en antwoord (#381). Sinds de
/// rewrite op Haiku draait, zou de oude "som tegen het antwoordmodel"-boeking
/// precies de besparing verbergen die de light-trede oplevert.</summary>
public class AiUsageSplitTests
{
    [Fact]
    public void Apply_RewriteEnAntwoord_WordenGescheiden()
    {
        var r = AiUsageSplit.Apply(total: (1000, 300), rewrite: (200, 40));

        Assert.Equal(new AiUsageSplit.Tokens(200, 40), r.Rewrite);
        Assert.Equal(new AiUsageSplit.Tokens(800, 260), r.Answer);
    }

    [Fact]
    public void Apply_ZonderRewrite_IsAllesAntwoord()
    {
        // Cache-hit op de rewrite (#152): geen tweede call, dus geen tweede rij.
        var r = AiUsageSplit.Apply(total: (500, 100), rewrite: null);

        Assert.Null(r.Rewrite);
        Assert.Equal(new AiUsageSplit.Tokens(500, 100), r.Answer);
    }

    [Fact]
    public void Apply_ZonderTotal_BlijftOnbekend()
    {
        // Onbekend ≠ 0 (#121): geen usage teruggekregen betekent null, nooit 0.
        var r = AiUsageSplit.Apply(total: null, rewrite: null);

        Assert.Null(r.Rewrite);
        Assert.Null(r.Answer);
    }

    [Fact]
    public void Apply_AlleenRewriteBekend_BoektAlleenDeRewrite()
    {
        // Rewrite geslaagd, antwoord-call viel uit vóór er usage terugkwam.
        var r = AiUsageSplit.Apply(total: null, rewrite: (200, 40));

        Assert.Equal(new AiUsageSplit.Tokens(200, 40), r.Rewrite);
        Assert.Null(r.Answer);
    }

    [Fact]
    public void Apply_SomKleinerDanDeel_KlemtOpNul()
    {
        // Kan alleen uit een programmeerfout komen; 0 is dan eerlijker dan een
        // negatief tokenaantal in het grootboek.
        var r = AiUsageSplit.Apply(total: (100, 10), rewrite: (200, 40));

        Assert.Equal(new AiUsageSplit.Tokens(0, 0), r.Answer);
    }
}
