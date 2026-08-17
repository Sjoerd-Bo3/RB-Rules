using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Riot-nieuws-feedparser (#167): dezelfde React-kaartcomponent
/// (data-testid="articlefeaturedcard-component") verschijnt op de smalle
/// rules-and-releases-index, de brede algemene nieuws-hub en de
/// artikel-carrousel onderaan de Rules Hub — getest tegen echte, ingekorte
/// snapshots van alle drie (2026-07-14).</summary>
public class RiotNewsFeedTests
{
    private static readonly Uri RulesAndReleasesBase =
        new("https://playriftbound.com/en-us/news/rules-and-releases/");
    private static readonly Uri NewsHubBase = new("https://playriftbound.com/en-us/news/");
    private static readonly Uri RulesHubBase = new("https://playriftbound.com/en-us/rules-hub/");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ── Smalle rules-and-releases-feed ──────────────────────────────────

    [Fact]
    public void ParseArticles_RulesAndReleasesSnapshot_FindsAllTwelveArticles()
    {
        // Ook de "smalle" rules-and-releases-index toont af en toe een
        // announcements-/organizedplay-artikel tussendoor (precies zoals
        // issue #167 het beschrijft) — vandaar dat ook déze feed een
        // CategoryFilter heeft, niet alleen de brede nieuws-hub.
        var html = Fixture("riot-news-rules-and-releases-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase);

        Assert.Equal(12, articles.Count);
        Assert.Equal(10, articles.Count(a => a.Category == "rules-and-releases"));
        Assert.Single(articles, a => a.Category == "announcements");
        Assert.Single(articles, a => a.Category == "organizedplay");
        Assert.All(articles, a => Assert.StartsWith("https://playriftbound.com/en-us/news/", a.Url));

        var faq = Assert.Single(articles, a => a.Url.EndsWith("riftbound-origins-faq"));
        Assert.Equal("Riftbound Origins FAQ", faq.Title);
        Assert.Equal(new DateTimeOffset(2025, 10, 16, 19, 0, 0, TimeSpan.Zero), faq.Date);

        var errata = Assert.Single(articles, a => a.Url.EndsWith("unleashed-errata-updates"));
        Assert.Equal("Unleashed Errata Updates", errata.Title);
    }

    [Fact]
    public void ParseArticles_WithCategoryFilter_KeepsOnlyThatCategory()
    {
        var html = Fixture("riot-news-rules-and-releases-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase, "rules-and-releases");

        Assert.Equal(10, articles.Count);
        Assert.All(articles, a => Assert.Equal("rules-and-releases", a.Category));
    }

    // ── Brede algemene nieuws-hub: categoriemix + externe kaart ─────────

    [Fact]
    public void ParseArticles_NewsHubSnapshot_ExcludesExternalCardAndKeepsCategoryMix()
    {
        var html = Fixture("riot-news-hub-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(html, NewsHubBase);

        // 12 kaarten in de fixture, 1 is een externe YouTube-videokaart.
        Assert.Equal(11, articles.Count);
        Assert.DoesNotContain(articles, a => a.Url.Contains("youtube"));

        Assert.Equal(7, articles.Count(a => a.Category == "announcements"));
        Assert.Equal(3, articles.Count(a => a.Category == "organizedplay"));
        // /en-us/news/hartfords-top-decks heeft geen categorie-segment.
        var noCategory = Assert.Single(articles, a => a.Category is null);
        Assert.EndsWith("hartfords-top-decks", noCategory.Url);
        Assert.Equal("Hartford’s Top Decks", noCategory.Title);
    }

    [Fact]
    public void ParseArticles_CategoryFilter_ExcludesUnlistedAndNoCategoryArticles()
    {
        var html = Fixture("riot-news-hub-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(
            html, NewsHubBase, "rules-and-releases,announcements,organizedplay");

        // De artikel-zonder-categorie kan het filter niet bevestigen en valt
        // dus weg, óók al zou het inhoudelijk prima kunnen passen.
        Assert.Equal(10, articles.Count);
        Assert.DoesNotContain(articles, a => a.Category is null);
        Assert.All(articles, a => Assert.True(a.Category is "announcements" or "organizedplay"));
    }

    [Fact]
    public void ParseArticles_CategoryFilter_IsCaseInsensitiveAndTrimsWhitespace()
    {
        var html = Fixture("riot-news-hub-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(html, NewsHubBase, " ANNOUNCEMENTS , OrganizedPlay ");
        Assert.Equal(10, articles.Count);
    }

    // ── Rules Hub: dezelfde kaartcomponent, andere paginacontext ────────

    [Fact]
    public void ParseArticles_RulesHubCarouselSnapshot_ParsesWithTheSameParser()
    {
        var html = Fixture("riot-rules-hub-carousel-2026-07-14.html");
        var articles = RiotNewsFeed.ParseArticles(html, RulesHubBase);

        // 9 kaarten in de fixture, 1 externe YouTube-kaart eruit.
        Assert.Equal(8, articles.Count);
        Assert.DoesNotContain(articles, a => a.Url.Contains("youtube"));
        Assert.Contains(articles, a => a.Url.EndsWith("the-vendetta-overview") && a.Category == "announcements");
    }

    // ── Puur unit-testbaar gedrag (synthetische snippets) ───────────────

    [Fact]
    public void ParseArticles_ResolvesRelativeHrefsAgainstBaseUri()
    {
        const string html = """
            <a role="button" aria-label="Titel" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Titel</div>
            </a>
            """;
        var article = Assert.Single(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
        Assert.Equal("https://playriftbound.com/en-us/news/rules-and-releases/x", article.Url);
    }

    [Fact]
    public void ParseArticles_SkipsCardsWithoutHref()
    {
        const string html = """
            <a role="button" aria-label="Titel" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Titel</div>
            </a>
            """;
        Assert.Empty(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
    }

    [Fact]
    public void ParseArticles_SkipsCardsWithoutAnyTitle()
    {
        const string html = """
            <a role="button" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-date"><time dateTime="2026-01-01T00:00:00.000Z">x</time></div>
            </a>
            """;
        Assert.Empty(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
    }

    [Fact]
    public void ParseArticles_FallsBackToAriaLabel_WhenCardTitleMissing()
    {
        const string html = """
            <a role="button" aria-label="Aria titel" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-date"><time dateTime="2026-01-01T00:00:00.000Z">x</time></div>
            </a>
            """;
        var article = Assert.Single(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
        Assert.Equal("Aria titel", article.Title);
    }

    [Fact]
    public void ParseArticles_MissingTime_YieldsNullDate_NeverAGuess()
    {
        const string html = """
            <a role="button" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Titel</div>
            </a>
            """;
        var article = Assert.Single(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
        Assert.Null(article.Date);
    }

    [Fact]
    public void ParseArticles_RejectsNonHttpsAndCrossHostCards()
    {
        var html = $"""
            <a role="button" href="http://playriftbound.com/en-us/news/rules-and-releases/onveilig" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Onveilig</div>
            </a>
            <a role="button" href="https://www.youtube.com/watch?v=abc" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Extern</div>
            </a>
            """;
        Assert.Empty(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
    }

    [Fact]
    public void ParseArticles_DeduplicatesRepeatedCards()
    {
        const string card = """
            <a role="button" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Titel</div>
            </a>
            """;
        var articles = RiotNewsFeed.ParseArticles($"{card}\n{card}", RulesAndReleasesBase);
        Assert.Single(articles);
    }

    [Fact]
    public void ParseArticles_StripsNestedMarkupAndDecodesEntitiesInTitle()
    {
        const string html = """
            <a role="button" href="/en-us/news/rules-and-releases/x" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Rules &amp; <span>Releases</span></div>
            </a>
            """;
        var article = Assert.Single(RiotNewsFeed.ParseArticles(html, RulesAndReleasesBase));
        Assert.Equal("Rules & Releases", article.Title);
    }

    [Fact]
    public void ParseArticles_CategoryNull_ForPathWithoutCategorySegment()
    {
        const string html = """
            <a role="button" href="/en-us/news/hartfords-top-decks" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Hartford's Top Decks</div>
            </a>
            """;
        var article = Assert.Single(RiotNewsFeed.ParseArticles(html, NewsHubBase));
        Assert.Null(article.Category);
    }

    [Fact]
    public void ParseArticles_SkipsNonNewsLinks()
    {
        // Een kaart die toevallig hetzelfde component gebruikt maar niet naar
        // /en-us/news/ linkt (bv. een andere sectie van de site) is geen
        // artikel voor deze feed — nooit crashen, gewoon overslaan.
        const string html = """
            <a role="button" href="/en-us/cards/some-card" data-testid="articlefeaturedcard-component">
              <div data-testid="card-title">Geen nieuwsartikel</div>
            </a>
            """;
        Assert.Empty(RiotNewsFeed.ParseArticles(html, NewsHubBase));
    }

    [Fact]
    public void ParseArticles_EmptyHtml_ReturnsEmptyList()
    {
        Assert.Empty(RiotNewsFeed.ParseArticles("", RulesAndReleasesBase));
    }

    [Fact]
    public void NormalizeUrl_TrimsTrailingSlashAndWhitespace()
    {
        Assert.Equal("https://playriftbound.com/en-us/news/x", RiotNewsFeed.NormalizeUrl(" https://playriftbound.com/en-us/news/x/ "));
    }
}
