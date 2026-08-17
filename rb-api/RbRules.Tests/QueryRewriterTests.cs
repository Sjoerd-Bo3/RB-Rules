using RbRules.Domain;

namespace RbRules.Tests;

public class QueryRewriterTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsAllFields()
    {
        var raw = """
            Hier is de JSON:
            {"normalized": "Which cards can destroy a gear?",
             "queries": ["gear removal", "destroy gear", "kill a gear"],
             "terms": ["kill a gear", "destroy target gear"]}
            """;
        var r = QueryRewriter.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("Which cards can destroy a gear?", r.NormalizedQuestion);
        Assert.Equal(3, r.SearchQueries.Length);
        Assert.Equal(["kill a gear", "destroy target gear"], r.LexicalTerms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sorry, ik kan deze vraag niet herformuleren.")]
    [InlineData("{not valid json}")]
    [InlineData("[\"array\", \"in plaats van object\"]")]
    public void Parse_GarbageOutput_ReturnsNull(string raw) =>
        // Fallback-regressie (#66): onzin-output ⇒ null ⇒ rauwe-vraag-route.
        Assert.Null(QueryRewriter.Parse(raw));

    [Fact]
    public void Parse_EmptyNormalizedAndNoQueries_ReturnsNull() =>
        Assert.Null(QueryRewriter.Parse("""{"normalized": "  ", "queries": [], "terms": ["x"]}"""));

    [Fact]
    public void Parse_MissingNormalized_FallsBackToFirstQuery()
    {
        var r = QueryRewriter.Parse("""{"queries": ["gear removal", "destroy gear"]}""");
        Assert.NotNull(r);
        Assert.Equal("gear removal", r.NormalizedQuestion);
    }

    [Fact]
    public void Parse_CapsQueriesAndTerms()
    {
        var r = QueryRewriter.Parse("""
            {"normalized": "q",
             "queries": ["a", "b", "c", "d", "e"],
             "terms": ["1", "2", "3", "4", "5", "6", "7", "8"]}
            """);
        Assert.NotNull(r);
        Assert.Equal(QueryRewriter.MaxQueries, r.SearchQueries.Length);
        Assert.Equal(QueryRewriter.MaxTerms, r.LexicalTerms.Length);
    }

    [Fact]
    public void Parse_FiltersJunkItems()
    {
        // Niet-strings, lege strings en duplicaten (case-insensitive) vallen weg.
        var r = QueryRewriter.Parse("""
            {"normalized": "q",
             "queries": ["gear removal", 42, "  ", "Gear Removal"],
             "terms": [null, "kill a gear"]}
            """);
        Assert.NotNull(r);
        Assert.Equal(["gear removal"], r.SearchQueries);
        Assert.Equal(["kill a gear"], r.LexicalTerms);
    }

    [Fact]
    public void Parse_TruncatesRunawayNormalized()
    {
        var raw = $$"""{"normalized": "{{new string('x', 900)}}", "queries": []}""";
        var r = QueryRewriter.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(300, r.NormalizedQuestion.Length);
    }
}
