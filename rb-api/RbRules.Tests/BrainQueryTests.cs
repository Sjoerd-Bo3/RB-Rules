using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De pure poort van de brein-API (#105): laag-filter, edge-type-
/// whitelist, richting-parameter en route-ref-parsing — precies de delen
/// waar gebruikersinvoer een query in zou kunnen lekken.</summary>
public class BrainQueryTests
{
    // ── laag-filter ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void TryParseLayers_Empty_MeansAllLayers(string? csv)
    {
        Assert.True(BrainQuery.TryParseLayers(csv, out var layers, out _));
        Assert.Equal(BrainQuery.Layers.ToHashSet(), layers);
    }

    [Fact]
    public void TryParseLayers_Subset_IsCaseInsensitiveAndTrimmed()
    {
        Assert.True(BrainQuery.TryParseLayers(" Rules, CLAIMS ,rules", out var layers, out _));
        Assert.Equal(["claims", "rules"], layers.Order());
    }

    [Fact]
    public void TryParseLayers_UnknownLayer_FailsWithHelpfulError()
    {
        // Stil negeren zou typo's verbergen — de fout noemt de geldige lagen.
        Assert.False(BrainQuery.TryParseLayers("rules,decks", out _, out var error));
        Assert.Contains("decks", error);
        Assert.Contains("rulings", error);
    }

    // ── edge-whitelist ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParseEdges_Empty_MeansNoFilter(string? csv)
    {
        Assert.True(BrainQuery.TryParseEdges(csv, out var edges, out _));
        Assert.Empty(edges);
    }

    [Fact]
    public void TryParseEdges_NormalizesCaseToWhitelist()
    {
        // De whitelist-vorm gaat de Cypher-parameter in — nooit de rauwe invoer.
        Assert.True(BrainQuery.TryParseEdges("has_mechanic, About,ABOUT", out var edges, out _));
        Assert.Equal(["HAS_MECHANIC", "ABOUT"], edges);
    }

    [Fact]
    public void TryParseEdges_UnknownType_Fails()
    {
        Assert.False(BrainQuery.TryParseEdges("ABOUT,DROP_ALL", out _, out var error));
        Assert.Contains("DROP_ALL", error);
    }

    [Fact]
    public void EdgeTypes_CoverTheFullGraphSchema()
    {
        // Elke relatie die GraphSyncService/InteractionService schrijft moet
        // filterbaar zijn; een nieuw edge-type hoort hier expliciet bij.
        string[] verwacht =
        [
            "FROM_SET", "HAS_DOMAIN", "HAS_TAG", "HAS_MECHANIC", "INTERACTS_WITH",
            "PART_OF", "EXPLAINS", "ABOUT", "SUPPORTED_BY", "SUPERSEDES", "AFFECTS",
            "RELATES_TO",
        ];
        Assert.Equal(verwacht.Order(), BrainQuery.EdgeTypes.Order());
    }

    // ── kind (#116): property-waarde, geen whitelist ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParseKind_Empty_MeansNoFilter(string? value)
    {
        Assert.True(BrainQuery.TryParseKind(value, out var kind, out _));
        Assert.Equal("", kind);
    }

    [Fact]
    public void TryParseKind_NormalizesLikeTheMiner()
    {
        // Zelfde normalisatie als opslag (RelationMiner): "Counters" filtert
        // hetzelfde als "counters" — de waarde blijft een Cypher-parameter.
        Assert.True(BrainQuery.TryParseKind("  Wordt_Beperkt  Door ", out var kind, out _));
        Assert.Equal("wordt beperkt door", kind);
    }

    [Fact]
    public void TryParseKind_UnusableValue_Fails()
    {
        Assert.False(BrainQuery.TryParseKind("...", out _, out var error));
        Assert.Contains("kind", error);
    }

    // ── richting ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, BrainDirection.Beide)]
    [InlineData("", BrainDirection.Beide)]
    [InlineData("beide", BrainDirection.Beide)]
    [InlineData("both", BrainDirection.Beide)]
    [InlineData("uit", BrainDirection.Uit)]
    [InlineData("out", BrainDirection.Uit)] // Engelse alias
    [InlineData("in", BrainDirection.In)]
    [InlineData(" UIT ", BrainDirection.Uit)]
    public void TryParseRichting_ValidValues(string? value, BrainDirection verwacht)
    {
        Assert.True(BrainQuery.TryParseRichting(value, out var richting, out _));
        Assert.Equal(verwacht, richting);
    }

    [Fact]
    public void TryParseRichting_UnknownValue_Fails()
    {
        Assert.False(BrainQuery.TryParseRichting("links", out _, out var error));
        Assert.Contains("links", error);
    }

    // ── graph-labels ───────────────────────────────────────────────────

    [Theory]
    [InlineData(BrainRefKind.Card, "Card")]
    [InlineData(BrainRefKind.Mechanic, "Mechanic")]
    [InlineData(BrainRefKind.Concept, "Concept")]
    [InlineData(BrainRefKind.Section, "RuleSection")]
    [InlineData(BrainRefKind.Claim, "Claim")]
    [InlineData(BrainRefKind.Source, "Source")]
    [InlineData(BrainRefKind.Erratum, "Erratum")]
    [InlineData(BrainRefKind.Change, "Change")]
    [InlineData(BrainRefKind.Set, "Set")]
    [InlineData(BrainRefKind.Domain, "Domain")]
    [InlineData(BrainRefKind.Tag, "Tag")]
    public void GraphLabel_MapsEveryGraphKind(BrainRefKind kind, string label)
    {
        Assert.Equal(label, BrainQuery.GraphLabel(kind));
    }

    [Fact]
    public void GraphLabel_Ruling_HasNoGraphNode()
    {
        // Geverifieerde rulings leven alleen in Postgres (docs/BRAIN.md §2.2).
        Assert.Null(BrainQuery.GraphLabel(BrainRefKind.Ruling));
    }

    // ── route-refs (de %2F-afspraak met de rb-ai-tools) ────────────────

    [Theory]
    [InlineData("card:ogn-011-298", BrainRefKind.Card, "ogn-011-298")]
    [InlineData("section:core-rules-pdf/101.2", BrainRefKind.Section, "core-rules-pdf/101.2")]
    // encodeURIComponent-vorm zoals rb-ai hem stuurt, vóór route-decodering:
    [InlineData("section%3Acore-rules-pdf%2F101.2", BrainRefKind.Section, "core-rules-pdf/101.2")]
    // alleen de slash ge-encodeerd (ASP.NET decodeerde %3A al wél):
    [InlineData("section:core-rules-pdf%2F101.2", BrainRefKind.Section, "core-rules-pdf/101.2")]
    [InlineData("mechanic%3ADeflect", BrainRefKind.Mechanic, "Deflect")]
    public void TryParseRouteRef_HandlesEncodedAndPlainRefs(
        string raw, BrainRefKind kind, string key)
    {
        Assert.True(BrainQuery.TryParseRouteRef(raw, out var parsed));
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(key, parsed.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("geen-ref")]
    [InlineData("deck:123")] // onbekend prefix
    [InlineData("%2F%2F")]
    public void TryParseRouteRef_InvalidInput_Fails(string? raw)
    {
        Assert.False(BrainQuery.TryParseRouteRef(raw, out _));
    }

    // ── mechaniek-namen met een procent-teken blijven letterlijk ───────

    [Fact]
    public void TryParseRouteRef_LiteralValueWinsOverUnescape()
    {
        // "mechanic:50%" is al een geldige ref; de unescape-poging is alléén
        // het vangnet voor invoer die eerst niet parsebaar was.
        Assert.True(BrainQuery.TryParseRouteRef("mechanic:50%", out var parsed));
        Assert.Equal("50%", parsed.Key);
    }
}
