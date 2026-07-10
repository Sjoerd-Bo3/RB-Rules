using RbRules.Domain;

namespace RbRules.Tests;

public class DiffUtilsTests
{
    [Fact]
    public void LineDiff_DetectsAddedSentence()
    {
        var diff = DiffUtils.LineDiff("Banned: A. Banned: B.", "Banned: A. Banned: B. Banned: C.");
        Assert.Contains("+ toegevoegd:", diff);
        Assert.Contains("Banned: C.", diff);
        Assert.DoesNotContain("- verwijderd:", diff);
    }

    [Fact]
    public void LineDiff_DetectsRemovedSentence()
    {
        var diff = DiffUtils.LineDiff("Rule one. Rule two.", "Rule one.");
        Assert.Contains("- verwijderd:", diff);
        Assert.Contains("Rule two.", diff);
    }

    [Fact]
    public void LineDiff_EmptyWhenIdentical()
    {
        Assert.Equal("", DiffUtils.LineDiff("Same text.", "Same text."));
    }

    [Fact]
    public void LineDiff_EmptyWhenOnlyReordered()
    {
        // Rules Hub wisselt de volgorde van gerelateerde-artikellinks per
        // request; herordening zonder tekstwijziging mag geen change opleveren.
        var diff = DiffUtils.LineDiff(
            "The Vendetta Begins. The Vendetta Overview.",
            "The Vendetta Overview. The Vendetta Begins.");
        Assert.Equal("", diff);
    }
}

public class BoilerplateTests
{
    [Fact]
    public void StripBoilerplate_RemovesNavHeaderFooterAside()
    {
        var html = "<header>logo</header><nav>menu</nav><main>De regels.</main>" +
                   "<aside>ads</aside><footer>© Riot</footer>";
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(html));
        Assert.Equal("De regels.", text);
    }

    [Fact]
    public void StripBoilerplate_MenuChangeDoesNotChangeHash()
    {
        const string content = "<main>Regel 601.2 blijft gelijk.</main>";
        var v1 = TextUtils.Sha256(TextUtils.HtmlToText(TextUtils.StripBoilerplate(
            $"<nav>menu-v1</nav>{content}")));
        var v2 = TextUtils.Sha256(TextUtils.HtmlToText(TextUtils.StripBoilerplate(
            $"<nav>menu-v2 <a href='/x'>nieuw</a></nav>{content}")));
        Assert.Equal(v1, v2);
    }
}

public class SchedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_WhenNeverChecked() =>
        Assert.True(Scheduling.IsDue("daily", null, Now));

    [Fact]
    public void Daily_NotDueAfterOneHour() =>
        Assert.False(Scheduling.IsDue("daily", Now.AddHours(-1), Now));

    [Fact]
    public void Daily_DueAfterOneDay() =>
        Assert.True(Scheduling.IsDue("daily", Now.AddDays(-1), Now));

    [Fact]
    public void Weekly_NotDueAfterTwoDays() =>
        Assert.False(Scheduling.IsDue("weekly", Now.AddDays(-2), Now));

    [Fact]
    public void Weekly_DueAfterSevenDays() =>
        Assert.True(Scheduling.IsDue("weekly", Now.AddDays(-7), Now));
}

public class ClassifierTests
{
    [Fact]
    public void Parse_ExtractsJsonFromNoisyResponse()
    {
        var cls = Classifier.Parse(
            "Hier is de classificatie:\n{\"change_type\": \"ban\", \"severity\": \"high\", " +
            "\"summary\": \"Kaart X gebanned\", \"meaning\": \"Check je deck.\"}\nKlaar!");
        Assert.NotNull(cls);
        Assert.Equal("ban", cls!.ChangeType);
        Assert.Equal("high", cls.Severity);
    }

    [Fact]
    public void Parse_ClampsSeverityToKnownValues()
    {
        var cls = Classifier.Parse("{\"change_type\": \"errata\", \"severity\": \"CATASTROPHIC\"}");
        Assert.Equal("medium", cls!.Severity);
    }

    [Fact]
    public void Parse_ReturnsNullOnGarbage() =>
        Assert.Null(Classifier.Parse("geen json hier"));
}
