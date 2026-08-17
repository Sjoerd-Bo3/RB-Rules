using RbRules.Api;

namespace RbRules.Tests;

/// <summary>Input-validatie van de bulk-actie (#199 review-fix finding 6):
/// een ontbrekend of ongeldig veld is een 400 met een duidelijke fout — het
/// oude pad NRE-500'de op een ontbrekende recommendation. De validator is
/// puur (op het contract-record zelf) zodat het endpoint dun blijft.</summary>
public class RelationBulkDecideRequestTests
{
    private static readonly DateTimeOffset SomeAsOf = DateTimeOffset.UtcNow;

    [Fact]
    public void Geldig_GeeftGeenFout()
    {
        var body = new RelationBulkDecideRequest("accept", "accept", 3, SomeAsOf);
        Assert.Null(body.ValidationError());
    }

    [Fact]
    public void GeldigMetHoofdlettersEnWitruimte_GeeftGeenFout()
    {
        var body = new RelationBulkDecideRequest(" Reject ", " REJECT ", 0, SomeAsOf);
        Assert.Null(body.ValidationError());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void OntbrekendeOfOnbekendeRecommendation_Faalt(string? recommendation)
    {
        var body = new RelationBulkDecideRequest(recommendation, "accept", 1, SomeAsOf);
        Assert.Contains("recommendation", body.ValidationError());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    // "unsure" is een geldige GROEP maar geen geldige BESLISSING — de bulk
    // kan een onzekere groep niet en-masse beslissen.
    [InlineData("unsure")]
    public void OntbrekendeOfOnbekendeDecision_Faalt(string? decision)
    {
        var body = new RelationBulkDecideRequest("accept", decision, 1, SomeAsOf);
        Assert.Contains("decision", body.ValidationError());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void OntbrekendeOfNegatieveExpectedCount_Faalt(int? expectedCount)
    {
        var body = new RelationBulkDecideRequest("accept", "accept", expectedCount, SomeAsOf);
        Assert.Contains("expectedCount", body.ValidationError());
    }

    [Fact]
    public void OntbrekendeAsOf_Faalt()
    {
        var body = new RelationBulkDecideRequest("accept", "accept", 1, AsOf: null);
        Assert.Contains("asOf", body.ValidationError());
    }
}
