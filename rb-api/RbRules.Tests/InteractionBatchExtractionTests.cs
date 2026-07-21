using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De envelop-parser van het batch-done-frame (#323) — tweede muur,
/// zelfde defensieve rol als <see cref="InteractionExtraction.ParseDetailed"/>:
/// schema-drift of een afgekapte body is <c>null</c> (uitval), nooit stil een
/// leeg resultaat.</summary>
public class InteractionBatchExtractionTests
{
    [Fact]
    public void Parse_GeldigDoneFrame_LevertPerKaartUitslagen()
    {
        var env = InteractionBatchExtraction.Parse("""
            {"type":"done","results":[
              {"code":"ogn-001","ok":true,"interactions":[{"from":"mechanic:Deflect","to":"mechanic:Assault","kind":"COUNTERS","interacts":true}]},
              {"code":"ogn-002","ok":true,"interactions":[]},
              {"code":"ogn-003","ok":false,"reason":"timeout"}
            ],"unknownCode":2,"usage":{"inputTokens":5200,"outputTokens":830}}
            """);

        Assert.NotNull(env);
        Assert.Equal(3, env.Results.Count);
        Assert.True(env.Results[0].Ok);
        Assert.Contains("mechanic:Deflect", env.Results[0].RawInteractions);
        Assert.True(env.Results[1].Ok);
        Assert.Equal("[]", env.Results[1].RawInteractions);
        Assert.False(env.Results[2].Ok);
        Assert.Equal("timeout", env.Results[2].Reason);
        Assert.Null(env.Results[2].RawInteractions);
        Assert.Equal(2, env.UnknownCode);
        Assert.Equal(5200, env.InputTokens);
        Assert.Equal(830, env.OutputTokens);
    }

    [Fact]
    public void Parse_ZonderUsage_BlijftUsageOnbekend_NooitNul()
    {
        var env = InteractionBatchExtraction.Parse(
            """{"type":"done","results":[{"code":"a","ok":true,"interactions":[]}]}""");
        Assert.NotNull(env);
        Assert.Null(env.InputTokens);
        Assert.Null(env.OutputTokens);
        Assert.Equal(0, env.UnknownCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"results\":")]                          // afgekapt
    [InlineData("{\"results\":\"none\"}")]                 // schema-drift
    [InlineData("{\"iets\":[]}")]                          // geen results
    [InlineData("{\"results\":[{\"ok\":true}]}")]          // kaart zonder code
    [InlineData("{\"results\":[{\"code\":\"a\",\"ok\":true}]}")] // ok zonder array
    public void Parse_KapotteEnvelop_IsUitval_GeenLeegResultaat(string? raw)
    {
        Assert.Null(InteractionBatchExtraction.Parse(raw));
    }
}
