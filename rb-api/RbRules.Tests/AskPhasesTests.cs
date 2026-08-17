using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Per-fase-timings (#152): de JSON-vorm op AskTrace.PhaseTimings is
/// een koppelvlak tussen AskService, het stats-endpoint en de beheer-UI —
/// camelCase-sleutels en een tolerante parse zijn het contract.</summary>
public class AskPhasesTests
{
    [Fact]
    public void ToJson_CamelCaseSleutels_CompactRoundTrip()
    {
        var phases = new AskPhases(
            RewriteMs: 800, EmbedMs: 120, RetrievalMs: 1500, AiMs: 38_000, TotalMs: 41_000);

        var json = phases.ToJson();

        // camelCase zoals de rest van de API-payloads — de beheer-UI parset
        // het veld rechtstreeks.
        Assert.Contains("\"rewriteMs\":800", json);
        Assert.Contains("\"embedMs\":120", json);
        Assert.Contains("\"retrievalMs\":1500", json);
        Assert.Contains("\"aiMs\":38000", json);
        Assert.Contains("\"totalMs\":41000", json);
        Assert.Equal(phases, AskPhases.Parse(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geen json")]
    [InlineData("{kapot")]
    public void Parse_LegeOfKapotteRij_DegradeertNaarNull(string? json) =>
        Assert.Null(AskPhases.Parse(json));

    [Fact]
    public void Parse_OntbrekendeVelden_VallenTerugOpNul()
    {
        // Toekomstvast: een oudere/gedeeltelijke rij mag de weergave niet
        // breken — ontbrekende fasen tellen als 0, niet als fout.
        var phases = AskPhases.Parse("""{"aiMs":5000}""");
        Assert.NotNull(phases);
        Assert.Equal(5000, phases!.AiMs);
        Assert.Equal(0, phases.RewriteMs);
        Assert.Equal(0, phases.TotalMs);
    }
}
