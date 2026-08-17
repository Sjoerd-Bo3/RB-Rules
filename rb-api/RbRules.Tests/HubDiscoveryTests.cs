using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Hub-ontdekking (#94): per-set-artikelen ("… Patch Notes",
/// "… Errata") vinden op de Rules Hub-index. Getest tegen een echte snapshot
/// van de hub (fixture) — de live pagina is de datavorm, geen verzinsel.</summary>
public class HubDiscoveryTests
{
    private static readonly Uri HubBase = new("https://playriftbound.com/en-us/rules-hub/");

    private static string HubSnapshot() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "rules-hub-2026-07-11.html"));

    [Fact]
    public void FindSetPages_RealHubSnapshot_FindsAllSixSetPages()
    {
        var pages = HubDiscovery.FindSetPages(HubSnapshot(), HubBase);

        Assert.Equal(6, pages.Count);
        Assert.Equal(3, pages.Count(p => p.Kind == HubDiscovery.KindPatchNotes));
        Assert.Equal(3, pages.Count(p => p.Kind == HubDiscovery.KindErrata));

        // De hub linkt via het legacy-domein riftbound.leagueoflegends.com;
        // de vondsten moeten op het canonieke domein staan.
        Assert.All(pages, p => Assert.StartsWith("https://playriftbound.com/", p.Url));

        Assert.Contains(pages, p =>
            p.Url == "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/"
            && p.Title == "Core Rules Patch Notes" && p.Kind == HubDiscovery.KindPatchNotes);
        Assert.Contains(pages, p =>
            p.Url == "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-origins-card-errata/"
            && p.Title == "Origins Errata" && p.Kind == HubDiscovery.KindErrata);
        // "Unleashed Errata" heeft een afwijkende URL-slug (…-errata-updates);
        // de ankertekst is dus het houvast, niet de URL.
        Assert.Contains(pages, p =>
            p.Url == "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-errata-updates/"
            && p.Kind == HubDiscovery.KindErrata);
    }

    [Fact]
    public void FindSetPages_EveryHubSetPage_IsCoveredBySeed()
    {
        // Registerdekking: alles wat de hub-snapshot linkt moet als bron in
        // SourceSeed staan. Wie deze fixture ná een nieuwe set ververst,
        // wordt hier naar SourceSeed gestuurd (of laat het bewust een
        // hub-voorstel blijven en past deze test aan).
        var seeded = SourceSeed.Defaults
            .Select(s => HubDiscovery.ComparisonKey(s.Url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var page in HubDiscovery.FindSetPages(HubSnapshot(), HubBase))
            Assert.Contains(HubDiscovery.ComparisonKey(page.Url), seeded);
    }

    [Fact]
    public void FindSetPages_ResultIsIndependentOfLinkOrder()
    {
        // De hub wisselt de linkvolgorde per request (flip-flop) — de
        // ontdekking moet daar doof voor zijn, anders ontstaat run_log-ruis.
        const string a = """<a href="https://riftbound.leagueoflegends.com/en-us/news/rules-and-releases/vendetta-patch-notes/">Vendetta Patch Notes</a>""";
        const string b = """<a href="https://riftbound.leagueoflegends.com/en-us/news/rules-and-releases/vendetta-errata/">Vendetta Errata</a>""";

        var forward = HubDiscovery.FindSetPages($"{a}\n{b}", HubBase);
        var reversed = HubDiscovery.FindSetPages($"{b}\n{a}", HubBase);

        Assert.Equal(2, forward.Count);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void FindSetPages_DeduplicatesRepeatedLinks()
    {
        // Dezelfde pagina meermaals gelinkt (nav + kaartje) is één vondst,
        // ook bij trailing-slash- of domeinvariatie.
        var html = """
            <a href="https://riftbound.leagueoflegends.com/x/vendetta-patch-notes/">Vendetta Patch Notes</a>
            <a href="https://playriftbound.com/x/vendetta-patch-notes">Vendetta Patch Notes</a>
            """;
        var pages = HubDiscovery.FindSetPages(html, HubBase);
        Assert.Single(pages);
    }

    [Fact]
    public void FindSetPages_ResolvesRelativeLinks()
    {
        var pages = HubDiscovery.FindSetPages(
            """<a href="/en-us/news/vendetta-errata/">Vendetta Errata</a>""", HubBase);
        var page = Assert.Single(pages);
        Assert.Equal("https://playriftbound.com/en-us/news/vendetta-errata/", page.Url);
    }

    [Fact]
    public void FindSetPages_StripsNestedMarkupFromTitle()
    {
        var pages = HubDiscovery.FindSetPages(
            """<a href="/x/"><div><span>Vendetta</span> <span>Patch Notes</span></div></a>""",
            HubBase);
        Assert.Equal("Vendetta Patch Notes", Assert.Single(pages).Title);
    }

    [Fact]
    public void FindSetPages_SkipsPdfLinks_ThatIsPdfDiscoveryTerritory()
    {
        Assert.Empty(HubDiscovery.FindSetPages(
            """<a href="https://cms.example/abc123.pdf">Core Rules Patch Notes</a>""", HubBase));
    }

    [Fact]
    public void FindSetPages_SkipsNonHttpsAndLongTeaserTexts()
    {
        var html = $"""
            <a href="http://onveilig.example/patch-notes/">Vendetta Patch Notes</a>
            <a href="https://playriftbound.com/teaser/">{new string('x', 60)} patch notes {new string('y', 60)}</a>
            """;
        Assert.Empty(HubDiscovery.FindSetPages(html, HubBase));
    }

    [Fact]
    public void CanonicalUrl_MapsLegacyDomain_AndKeepsOthers()
    {
        Assert.Equal(
            "https://playriftbound.com/en-us/news/x/",
            HubDiscovery.CanonicalUrl(new Uri("https://riftbound.leagueoflegends.com/en-us/news/x/")));
        Assert.Equal(
            "https://playriftbound.com/en-us/news/x/",
            HubDiscovery.CanonicalUrl(new Uri("https://playriftbound.com/en-us/news/x/")));
        Assert.Equal(
            "https://uvsgames.com/riftbound/",
            HubDiscovery.CanonicalUrl(new Uri("https://uvsgames.com/riftbound/")));
    }

    [Fact]
    public void CanonicalUrl_DropsNavigationFragments()
    {
        Assert.Equal(
            "https://playriftbound.com/en-us/news/x/",
            HubDiscovery.CanonicalUrl(new Uri("https://playriftbound.com/en-us/news/x/#faq")));
    }

    [Fact]
    public void ComparisonKey_MatchesAcrossDomainSlashAndCaseVariants()
    {
        var keys = new[]
        {
            "https://riftbound.leagueoflegends.com/en-us/news/x/",
            "https://playriftbound.com/en-us/news/x",
            "HTTPS://PLAYRIFTBOUND.COM/en-us/news/x/",
        }.Select(HubDiscovery.ComparisonKey).ToList();

        // Sleutels zijn bedoeld voor een OrdinalIgnoreCase-set (zelfde
        // dedupe-vorm als SourceScout) — alle varianten vallen samen.
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(set.Add(keys[0]));
        Assert.False(set.Add(keys[1]));
        Assert.False(set.Add(keys[2]));
    }
}
