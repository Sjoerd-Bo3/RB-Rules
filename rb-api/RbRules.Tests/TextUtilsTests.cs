using RbRules.Domain;

namespace RbRules.Tests;

public class TextUtilsTests
{
    [Fact]
    public void Sha256_ProducesStableLowercaseHex()
    {
        var hash = TextUtils.Sha256("hello");
        Assert.Equal(64, hash.Length);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public void HtmlToText_StripsScriptsStylesAndTags()
    {
        var text = TextUtils.HtmlToText(
            "<div><script>x()</script><style>.a{}</style><b>Ban list:</b> Draven &amp; Scrapheap.</div>");
        Assert.Equal("Ban list: Draven & Scrapheap.", text);
    }

    [Fact]
    public void HtmlToText_NormalizesWhitespace()
    {
        Assert.Equal("a b c", TextUtils.HtmlToText("a\n\n  b\t c"));
    }
}
