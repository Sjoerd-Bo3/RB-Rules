using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Unit-tests voor de kleine LRU rewrite-cache (#152): treffer/mis,
/// sleutel-normalisatie, en verdringing van de minst-recent-gebruikte entry
/// bij capaciteit. De service-integratie (cache-hit slaat de rb-ai-call over,
/// een null-uitkomst wordt nooit gecacht) zit in AskServiceRewriteCacheTests.</summary>
public class RewriteCacheTests
{
    private static QueryRewrite Rewrite(string normalized) => new(normalized, [], []);

    [Fact]
    public void TryGet_LegeCache_ReturnsNull() =>
        Assert.Null(new RewriteCache().TryGet("iets"));

    [Fact]
    public void Set_DanTryGet_LevertDezelfdeWaardeOp()
    {
        var cache = new RewriteCache();
        var value = Rewrite("how does deflect work");

        cache.Set("hoe werkt deflect", value);

        Assert.Same(value, cache.TryGet("hoe werkt deflect"));
    }

    [Fact]
    public void NormalizeKey_TrimtEnLowercast() =>
        Assert.Equal(
            "hoe werkt deflect?",
            RewriteCache.NormalizeKey("  Hoe Werkt DEFLECT?  "));

    [Fact]
    public void TryGet_OnbekendeSleutel_ReturnsNull()
    {
        var cache = new RewriteCache();
        cache.Set("a", Rewrite("a"));

        Assert.Null(cache.TryGet("b"));
    }

    [Fact]
    public void Set_OpCapaciteit_VerdringtMinstRecentGebruikte()
    {
        var cache = new RewriteCache(capacity: 2);
        cache.Set("a", Rewrite("a"));
        cache.Set("b", Rewrite("b"));

        // "c" erbij duwt de minst-recent-gebruikte ("a", nog niet opgevraagd
        // sinds "b") eruit.
        cache.Set("c", Rewrite("c"));

        Assert.Null(cache.TryGet("a"));
        Assert.NotNull(cache.TryGet("b"));
        Assert.NotNull(cache.TryGet("c"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryGet_PromootRecentheid_VoorkomtVerdringingVanEenNetGebruikteEntry()
    {
        var cache = new RewriteCache(capacity: 2);
        cache.Set("a", Rewrite("a"));
        cache.Set("b", Rewrite("b"));

        // "a" opvragen maakt hem recent gebruikt — "b" is nu de oudste.
        cache.TryGet("a");
        cache.Set("c", Rewrite("c"));

        Assert.NotNull(cache.TryGet("a"));
        Assert.Null(cache.TryGet("b"));
        Assert.NotNull(cache.TryGet("c"));
    }

    [Fact]
    public void Set_BestaandeSleutel_OverschrijftZonderCapaciteitTeKosten()
    {
        var cache = new RewriteCache(capacity: 2);
        cache.Set("a", Rewrite("a"));
        cache.Set("a", Rewrite("a2"));

        Assert.Equal(1, cache.Count);
        Assert.Equal("a2", cache.TryGet("a")!.NormalizedQuestion);
    }
}
