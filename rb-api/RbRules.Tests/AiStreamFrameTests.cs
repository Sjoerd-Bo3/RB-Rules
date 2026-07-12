using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>NDJSON-frameparser van de rb-ai-stream (#31): kapotte of
/// onbekende regels degraderen naar null i.p.v. een exception — een haperende
/// stream mag het antwoordpad nooit laten crashen.</summary>
public class AiStreamFrameTests
{
    [Fact]
    public void Parse_Delta_GeeftTekst()
    {
        var frame = AiStreamFrame.Parse("""{"type":"delta","text":"Oordeel: "}""");
        Assert.NotNull(frame);
        Assert.Equal("delta", frame!.Type);
        Assert.Equal("Oordeel: ", frame.Text);
    }

    [Fact]
    public void Parse_Done_GeeftVolledigAntwoord()
    {
        var frame = AiStreamFrame.Parse("""{"type":"done","answer":"**Oordeel:** Ja."}""");
        Assert.Equal("done", frame!.Type);
        Assert.Equal("**Oordeel:** Ja.", frame.Answer);
    }

    [Fact]
    public void Parse_Error_GeeftFoutmelding()
    {
        var frame = AiStreamFrame.Parse("""{"type":"error","error":"timeout"}""");
        Assert.Equal("error", frame!.Type);
        Assert.Equal("timeout", frame.Error);
    }

    [Theory]
    [InlineData("")]                     // lege regel tussen frames
    [InlineData("   ")]
    [InlineData("{\"text\":\"x\"}")]     // frame zonder type
    [InlineData("{niet-json")]           // afgekapte regel (verbinding weg)
    [InlineData("[1,2,3]")]              // geen object
    [InlineData("\"delta\"")]            // kale string
    public void Parse_OnbruikbareRegels_WordenNull(string line)
    {
        Assert.Null(AiStreamFrame.Parse(line));
    }

    [Fact]
    public void Parse_NietStringVelden_WordenGenegeerd()
    {
        // Robuust tegen vreemde types: alleen string-waarden tellen.
        var frame = AiStreamFrame.Parse("""{"type":"delta","text":42}""");
        Assert.Equal("delta", frame!.Type);
        Assert.Null(frame.Text);
    }

    [Fact]
    public void Parse_DoneMetUsage_GeeftTokens()
    {
        // Slotframe met echte token-tellingen (#121).
        var frame = AiStreamFrame.Parse(
            """{"type":"done","answer":"**Oordeel:** Ja.","usage":{"inputTokens":48012,"outputTokens":890}}""");
        Assert.Equal("done", frame!.Type);
        Assert.Equal("**Oordeel:** Ja.", frame.Answer);
        Assert.Equal(new AiUsage(48012, 890), frame.Usage);
    }

    [Theory]
    [InlineData("""{"type":"done","answer":"Ja."}""")]                                   // oude rb-ai zonder usage
    [InlineData("""{"type":"done","answer":"Ja.","usage":null}""")]                      // expliciet null
    [InlineData("""{"type":"done","answer":"Ja.","usage":{"inputTokens":"12","outputTokens":890}}""")] // verkeerd type
    [InlineData("""{"type":"done","answer":"Ja.","usage":{"inputTokens":12}}""")]        // veld ontbreekt
    public void Parse_KapotteOfOntbrekendeUsage_BlijftBruikbaarFrame(string line)
    {
        // Usage is best-effort (#121): het frame zelf mag er nooit op sneuvelen.
        var frame = AiStreamFrame.Parse(line);
        Assert.Equal("done", frame!.Type);
        Assert.Equal("Ja.", frame.Answer);
        Assert.Null(frame.Usage);
    }
}
