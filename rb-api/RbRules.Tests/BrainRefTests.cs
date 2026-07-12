using RbRules.Domain;

namespace RbRules.Tests;

public class BrainRefTests
{
    [Fact]
    public void Format_AllKinds_UseConventionPrefixes()
    {
        // docs/BRAIN.md §2.1: één tekstuele conventie over beide representaties.
        Assert.Equal("card:ogn-011-298", BrainRef.Card("ogn-011-298").Format());
        Assert.Equal("mechanic:Accelerate", BrainRef.Mechanic("Accelerate").Format());
        Assert.Equal("concept:turn-structure", BrainRef.Concept("turn-structure").Format());
        Assert.Equal("section:core-rules-pdf/101.2", BrainRef.Section("core-rules-pdf", "101.2").Format());
        Assert.Equal("claim:17", BrainRef.Claim(17).Format());
        Assert.Equal("source:riftbound-gg", BrainRef.Source("riftbound-gg").Format());
        Assert.Equal("erratum:3", BrainRef.Erratum(3).Format());
        Assert.Equal("change:42", BrainRef.Change(42).Format());
        Assert.Equal("set:OGN", BrainRef.Set("OGN").Format());
        Assert.Equal("domain:Fury", BrainRef.Domain("Fury").Format());
        Assert.Equal("tag:Yordle", BrainRef.Tag("Yordle").Format());
        Assert.Equal("ruling:9", BrainRef.Ruling(9).Format());
    }

    [Theory]
    [InlineData("card:ogn-011-298", BrainRefKind.Card, "ogn-011-298")]
    [InlineData("section:core-rules-pdf/101.2.d", BrainRefKind.Section, "core-rules-pdf/101.2.d")]
    [InlineData("mechanic:Chosen Champion", BrainRefKind.Mechanic, "Chosen Champion")]
    [InlineData("claim:17", BrainRefKind.Claim, "17")]
    [InlineData("  ruling:9  ", BrainRefKind.Ruling, "9")] // randwitruimte om de hele ref is vergeeflijk
    public void TryParse_ValidRefs_RoundTrip(string text, BrainRefKind kind, string key)
    {
        Assert.True(BrainRef.TryParse(text, out var r));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(key, r.Key);
        Assert.Equal(text.Trim(), r.Format());
    }

    [Fact]
    public void TryParse_KeyMayContainColon()
    {
        // Alleen de eerste dubbele punt scheidt kind en key.
        Assert.True(BrainRef.TryParse("source:extern:mirror", out var r));
        Assert.Equal(BrainRefKind.Source, r.Kind);
        Assert.Equal("extern:mirror", r.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("card")]          // geen scheider
    [InlineData("card:")]         // lege key
    [InlineData(":ogn-011-298")]  // leeg kind
    [InlineData("onzin:x")]       // onbekend kind
    [InlineData("card: x")]       // rafelige key (witruimte na de scheider)
    [InlineData("Card:x")]        // prefixen zijn exact lowercase
    public void TryParse_InvalidRefs_ReturnsFalse(string? text)
    {
        Assert.False(BrainRef.TryParse(text, out _));
    }

    [Fact]
    public void FormatTryParse_RoundTripsEveryKind()
    {
        foreach (var kind in Enum.GetValues<BrainRefKind>())
        {
            var formatted = new BrainRef(kind, "sleutel-1").Format();
            Assert.True(BrainRef.TryParse(formatted, out var parsed));
            Assert.Equal(kind, parsed.Kind);
            Assert.Equal("sleutel-1", parsed.Key);
        }
    }
}
