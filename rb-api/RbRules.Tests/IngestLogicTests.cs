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

    // #205: playriftbound-artikelen (bv. de Vendetta patch notes) tonen een
    // "Related Articles"-carousel in een gewone <section id="related-articles">
    // (dus NIET in een <aside>) die van scan tot scan verandert zodra elders
    // op de site een nieuw artikel verschijnt — editorial-ruis in de
    // wijzigingen-feed. Fixture spiegelt de echte markup-vorm (data-testid +
    // andere attributen vóór id, zoals playriftbound.com die rendert).
    private static string RelatedArticlesSection(string relatedTitle) =>
        "<section data-testid=\"article-card-carousel\" layout=\"3up\" id=\"related-articles\" class=\"blade\">"
        + "<div class=\"header\"><h2>Related Articles</h2></div>"
        + $"<a href=\"/en-us/news/announcements/related-slug\">{relatedTitle}</a>"
        + "</section>";

    [Fact]
    public void StripBoilerplate_RemovesRelatedArticlesCarousel()
    {
        var html = "<main>Core Rules: Vendetta Patch Notes. Legion is a dependent keyword.</main>"
                   + RelatedArticlesSection("July Ban List Updates");

        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(html));

        Assert.Contains("Legion is a dependent keyword", text);
        Assert.DoesNotContain("Related Articles", text);
        Assert.DoesNotContain("July Ban List Updates", text);
    }

    [Fact]
    public void StripBoilerplate_RelatedArticlesChangeDoesNotChangeHash()
    {
        const string content = "<main>Legion is a dependent keyword.</main>";
        var v1 = TextUtils.Sha256(TextUtils.HtmlToText(TextUtils.StripBoilerplate(
            content + RelatedArticlesSection("July Ban List Updates"))));
        var v2 = TextUtils.Sha256(TextUtils.HtmlToText(TextUtils.StripBoilerplate(
            content + RelatedArticlesSection("August Set Release"))));
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

    // ── Vensterlogica periodieke zelfverrijking (#122) ──────────────────
    // De scheduler bepaalt op het run_log-grootboek of relatie-mining
    // (nachtelijk) en de scout (wekelijks) aan de beurt zijn.

    [Fact]
    public void Window_EersteRunIsAltijdAanDeBeurt() =>
        Assert.True(Scheduling.IsWindowDue(TimeSpan.FromDays(1), null, Now));

    [Fact]
    public void Window_NietAanDeBeurtBinnenHetVenster() =>
        Assert.False(Scheduling.IsWindowDue(TimeSpan.FromDays(1), Now.AddHours(-12), Now));

    [Fact]
    public void Window_AanDeBeurtNaHetVenster() =>
        Assert.True(Scheduling.IsWindowDue(TimeSpan.FromDays(1), Now.AddDays(-1), Now));

    [Fact]
    public void Window_MargeVoorkomtTickDrift()
    {
        // Een uurlijkse tick valt zelden exact op de venstergrens: 23u40m na
        // de vorige run telt als 'aan de beurt' (marge 30m) — anders schuift
        // elke nachtelijke run een tick verder op. Ruim daarbinnen niet.
        Assert.True(Scheduling.IsWindowDue(
            TimeSpan.FromDays(1), Now.AddHours(-23).AddMinutes(-40), Now));
        Assert.False(Scheduling.IsWindowDue(TimeSpan.FromDays(1), Now.AddHours(-23), Now));
    }

    [Fact]
    public void Window_WeekvensterVoorDeScout()
    {
        // De scout kost een research-call: binnen de week nooit opnieuw.
        Assert.False(Scheduling.IsWindowDue(TimeSpan.FromDays(7), Now.AddDays(-6), Now));
        Assert.True(Scheduling.IsWindowDue(TimeSpan.FromDays(7), Now.AddDays(-7), Now));
    }

    [Fact]
    public void Window_DrieUursvensterVoorDecks()
    {
        // Piltover-decks (#15 fase 3, spoor C): de ~10k-deck-backfill
        // verdeelt zich over vele runs à 400 pagina's; een venster van 3 uur
        // triggert de volgende run niet vaker dan dat, ook al draait de tick
        // zelf elk uur.
        Assert.False(Scheduling.IsWindowDue(TimeSpan.FromHours(3), Now.AddHours(-2), Now));
        Assert.True(Scheduling.IsWindowDue(TimeSpan.FromHours(3), Now.AddHours(-3), Now));
    }

    [Fact]
    public void Window_MargeGeldtOokVoorHetDecksvenster()
    {
        // Zelfde 30-min-marge als de andere periodieke jobs: voorkomt dat de
        // uurlijkse tick het 3-uursvenster net mist en de decks-run zo een
        // tick verder opschuift.
        Assert.True(Scheduling.IsWindowDue(
            TimeSpan.FromHours(3), Now.AddHours(-2).AddMinutes(-40), Now));
        Assert.False(Scheduling.IsWindowDue(
            TimeSpan.FromHours(3), Now.AddHours(-2).AddMinutes(-25), Now));
    }
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

/// <summary>#58: naclassificatie van changes die bij rb-ai-uitval zonder
/// samenvatting/duiding zijn opgeslagen.</summary>
public class ClassificationBackfillTests
{
    /// <summary>Zoals IngestService een change opslaat als de LLM-call faalde.</summary>
    private static Change UnclassifiedChange() => new()
    {
        SourceId = "core-rules",
        ChangeType = "unknown",
        Severity = "medium",
        Summary = null,
        Meaning = null,
        Diff = "+ toegevoegd: Regel 601.2 nieuw.",
    };

    [Fact]
    public void NeedsClassification_TrueForUnclassifiedScanResult() =>
        Assert.True(Classifier.NeedsClassification(UnclassifiedChange()));

    [Fact]
    public void NeedsClassification_TrueWhenSummaryWhitespace()
    {
        var change = UnclassifiedChange();
        change.ChangeType = "errata";
        change.Summary = "   ";
        change.Meaning = "Duiding.";
        Assert.True(Classifier.NeedsClassification(change));
    }

    [Fact]
    public void NeedsClassification_TrueWhenTypeUnknownDespiteSummary()
    {
        var change = UnclassifiedChange();
        change.Summary = "Samenvatting.";
        change.Meaning = "Duiding.";
        Assert.True(Classifier.NeedsClassification(change));
    }

    [Fact]
    public void NeedsClassification_FalseWhenComplete()
    {
        var change = UnclassifiedChange();
        change.ChangeType = "errata";
        change.Summary = "Samenvatting.";
        change.Meaning = "Duiding.";
        Assert.False(Classifier.NeedsClassification(change));
    }

    [Fact]
    public void Apply_FullClassification_FillsAllFieldsAndCompletes()
    {
        var change = UnclassifiedChange();
        var done = Classifier.Apply(change,
            new Classification("ban", "high", "Kaart X gebanned", "Check je deck."));
        Assert.True(done);
        Assert.Equal("ban", change.ChangeType);
        Assert.Equal("high", change.Severity);
        Assert.Equal("Kaart X gebanned", change.Summary);
        Assert.Equal("Check je deck.", change.Meaning);
    }

    [Fact]
    public void Apply_Null_LeavesChangeUntouched()
    {
        // rb-ai-uitval tijdens de backfill zelf: change blijft staan voor een
        // volgende run, niets wordt overschreven.
        var change = UnclassifiedChange();
        Assert.False(Classifier.Apply(change, null));
        Assert.Equal("unknown", change.ChangeType);
        Assert.Null(change.Summary);
        Assert.Null(change.Meaning);
    }

    [Fact]
    public void Apply_ParseWithMissingFields_KeepsChangePending()
    {
        // Parse vult ontbrekende keys met lege strings — dat mag een change
        // niet als "geclassificeerd" markeren.
        var change = UnclassifiedChange();
        var cls = Classifier.Parse("{\"change_type\": \"errata\"}");
        Assert.False(Classifier.Apply(change, cls));
        Assert.Equal("errata", change.ChangeType);
        Assert.Null(change.Summary);
    }

    [Fact]
    public void Apply_DoesNotDegradeExistingValues()
    {
        // Een eerdere (deels) geslaagde poging mag niet worden weggegumd door
        // een latere mislukte parse met lege velden of type "unknown".
        var change = UnclassifiedChange();
        change.ChangeType = "errata";
        change.Summary = "Bestaande samenvatting";
        var done = Classifier.Apply(change, new Classification("unknown", "medium", "", ""));
        Assert.False(done);
        Assert.Equal("errata", change.ChangeType);
        Assert.Equal("Bestaande samenvatting", change.Summary);
    }
}
