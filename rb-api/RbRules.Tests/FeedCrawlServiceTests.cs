using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Bron-feed-crawl (#167): AutoApprove ⇒ bron / anders ⇒ voorstel,
/// idempotent op genormaliseerde URL (ook over feeds heen binnen één run —
/// de drie hoofdfeeds overlappen deels), onlyDue respecteert de cadence per
/// feed (zelfde patroon als IngestService voor bronnen), en een fout bij één
/// feed (fetch, guard) is data en stopt de andere feeds niet. HTTP is een
/// gestubde handler (RoutingHandler, DeckIngestServiceTests-patroon);
/// database is EF InMemory.</summary>
public class FeedCrawlServiceTests
{
    private const string Base = "https://playriftbound.com/en-us/news/rules-and-releases/";

    [Fact]
    public async Task RunAsync_AutoApproveFeed_NewArticle_CreatesEnabledOfficialSource()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed Eén", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card($"{Base}x", "Nieuw Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(1, r.FeedsChecked);
        Assert.Equal(1, r.ArticlesSeen);
        Assert.Equal(1, r.NewSources);
        Assert.Equal(0, r.NewProposals);

        var src = await db.Sources.SingleAsync();
        Assert.Equal("Nieuw Artikel", src.Name);
        Assert.Equal($"{Base}x", src.Url);
        Assert.Equal("official", src.Type);
        Assert.Equal(1, src.TrustTier);
        Assert.Equal("html", src.Parser);
        Assert.True(src.Enabled);
        Assert.Equal("f1", src.FeedId);
        Assert.Equal(FeedCrawlService.AutoDiscoveredRank, src.Rank);

        var feed = await db.SourceFeeds.SingleAsync();
        Assert.NotNull(feed.LastChecked);
        Assert.NotNull(feed.LastHash);

        var log = await db.RunLogs.SingleAsync(l => l.Kind == "feeds");
        Assert.Equal("f1", log.Ref);
        Assert.Equal("ok", log.Status);
        Assert.Contains("1 artikelen gezien", log.Detail);
        Assert.Contains("1 bron", log.Detail);
    }

    [Fact]
    public async Task RunAsync_NonAutoApproveFeed_NewArticle_CreatesProposal()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Minder vertrouwde feed", Url = Base, AutoApprove = false, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card($"{Base}y", "Voorstel-artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.NewSources);
        Assert.Equal(1, r.NewProposals);
        Assert.Empty(db.Sources);
        var p = await db.SourceProposals.SingleAsync();
        Assert.Equal($"{Base}y", p.Url);
        Assert.Equal("Voorstel-artikel", p.Name);
        Assert.Equal("official", p.Type);
        Assert.Equal("proposed", p.Status);
        Assert.Contains("Minder vertrouwde feed", p.Motivation);
    }

    [Fact]
    public async Task RunAsync_ArticleAlreadyRegisteredAsSource_IsSkipped()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "bestaand", Name = "Bestaand", Url = $"{Base}x", Type = "official",
            TrustTier = 1, Rank = 100, Parser = "html", Cadence = "weekly",
        });
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card($"{Base}x", "Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.NewSources);
        Assert.Single(await db.Sources.ToListAsync()); // geen tweede rij
    }

    [Fact]
    public async Task RunAsync_ArticleAlreadyProposed_IsSkipped()
    {
        using var db = NewDb();
        db.SourceProposals.Add(new SourceProposal
        {
            Url = $"{Base}x", Name = "Al voorgesteld", Type = "official", Motivation = "eerder",
        });
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = Base, AutoApprove = false, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card($"{Base}x", "Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.NewProposals);
        Assert.Single(await db.SourceProposals.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_TwoOverlappingFeeds_SameArticle_OnlyCreatedOnce()
    {
        // De rules-hub-carrousel en de nieuws-hub tonen soms hetzelfde
        // artikel — binnen één run mag dat nooit een dubbele bron of
        // voorstel opleveren, ook al verwerkt feed B het pas ná feed A.
        using var db = NewDb();
        const string baseB = "https://playriftbound.com/en-us/rules-hub/";
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "a-eerste", Name = "Feed A", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "b-tweede", Name = "Feed B", Url = baseB, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(
            db,
            (Base, Ok(Card($"{Base}gedeeld-artikel", "Gedeeld Artikel"))),
            (baseB, Ok(Card($"{Base}gedeeld-artikel", "Gedeeld Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(1, r.NewSources);
        Assert.Single(await db.Sources.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_SlugCollision_GetsSuffixed()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        // Twee verschillende categorieën, zelfde laatste padsegment ⇒ zelfde
        // SlugForUrl-basisvorm ("playriftbound-com-x").
        var html = Card($"{Base}x", "Eerste X")
            + Card("https://playriftbound.com/en-us/news/announcements/x", "Tweede X");
        var (svc, _) = Service(db, (Base, Ok(html)));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(2, r.NewSources);
        var ids = (await db.Sources.Select(s => s.Id).ToListAsync()).OrderBy(x => x).ToList();
        Assert.Equal(["playriftbound-com-x", "playriftbound-com-x-2"], ids);
    }

    [Fact]
    public async Task RunAsync_UnchangedHash_SkipsReparse_ButUpdatesLastChecked()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var html = Card($"{Base}x", "Artikel");
        var (svc, handler) = Service(db, (Base, Ok(html)));

        await svc.RunAsync(onlyDue: false);
        var eersteChecked = (await db.SourceFeeds.SingleAsync()).LastChecked;
        var r2 = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r2.NewSources);
        Assert.Single(await db.Sources.ToListAsync());
        Assert.True((await db.SourceFeeds.SingleAsync()).LastChecked >= eersteChecked);
        Assert.Equal(2, handler.Requests.Count(u => u == Base)); // wél twee keer opgehaald
    }

    [Fact]
    public async Task RunAsync_ReorderedCardsDifferentHash_StillNoDuplicates()
    {
        // Flip-flop-tolerantie (#167, Rules Hub-valkuil): een gewijzigde hash
        // door herordening levert bij reparse dezelfde, al-bekende URL's op —
        // nooit een duplicaat, ook al verschilt de ruwe HTML.
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var cardA = Card($"{Base}a", "A");
        var cardB = Card($"{Base}b", "B");
        var handler = new RoutingHandler();
        handler.Routes[Base] = Ok($"{cardA}\n{cardB}");
        var svc = new FeedCrawlService(db, new HttpClient(handler));
        await svc.RunAsync(onlyDue: false);
        Assert.Equal(2, await db.Sources.CountAsync());

        handler.Routes[Base] = Ok($"{cardB}\n{cardA}"); // andere volgorde, andere hash
        var r2 = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r2.NewSources);
        Assert.Equal(2, await db.Sources.CountAsync());
    }

    [Fact]
    public async Task RunAsync_OnlyDue_SkipsFeedsNotYetDue_ButForceRunsAll()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Net gecontroleerd", Url = Base, AutoApprove = true,
            Cadence = "daily", LastChecked = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var (svc, handler) = Service(db, (Base, Ok(Card($"{Base}x", "Artikel"))));

        var due = await svc.RunAsync(onlyDue: true);
        Assert.Equal(0, due.FeedsChecked);
        Assert.Empty(handler.Requests);

        var forced = await svc.RunAsync(onlyDue: false);
        Assert.Equal(1, forced.FeedsChecked);
        Assert.Single(await db.Sources.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_OneFeedFails_OtherFeedStillProcessed()
    {
        using var db = NewDb();
        const string baseB = "https://playriftbound.com/en-us/rules-hub/";
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "kapot", Name = "Kapotte feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "gezond", Name = "Gezonde feed", Url = baseB, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(
            db,
            (Base, () => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            (baseB, Ok(Card("https://playriftbound.com/en-us/news/announcements/x", "Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(2, r.FeedsChecked);
        Assert.Equal(1, r.NewSources);
        var fout = await db.RunLogs.SingleAsync(l => l.Kind == "feeds" && l.Status == "error");
        Assert.Equal("kapot", fout.Ref);
        Assert.Contains("HTTP 500", fout.Detail);
        var ok = await db.RunLogs.SingleAsync(l => l.Kind == "feeds" && l.Status == "ok");
        Assert.Equal("gezond", ok.Ref);
    }

    [Fact]
    public async Task RunAsync_FeedUrlBlockedBySsrfGuard_LogsErrorAndDoesNotFetch()
    {
        using var db = NewDb();
        // Rechtstreeks toegevoegd (voorbij de endpoint-validatie) om de
        // guard-controle in de service zelf te testen — verdedigen in de
        // diepte, net als bij IngestService/SourceScoutService.
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "kwaadaardig", Name = "Interne feed", Url = "https://10.0.0.7/nieuws/",
            AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var fetched = false;
        var handler = new RoutingHandler();
        handler.Fallback = () => { fetched = true; return Ok("").Invoke(); };
        var svc = new FeedCrawlService(db, new HttpClient(handler));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.False(fetched);
        Assert.Equal(0, r.NewSources);
        var fout = await db.RunLogs.SingleAsync(l => l.Kind == "feeds");
        Assert.Equal("error", fout.Status);
        Assert.Contains("SSRF-guard", fout.Detail);
    }

    [Fact]
    public async Task RunAsync_NoEnabledFeeds_ReturnsZeroWithoutError()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "uit", Name = "Uitgeschakeld", Url = Base, Enabled = false, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = new FeedCrawlService(db, new HttpClient(new RoutingHandler()));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.FeedsChecked);
        Assert.Contains("geen feeds aan de beurt", r.Message);
    }

    [Fact]
    public async Task RunAsync_AutoApproveFeedOnNonOfficialHost_RoutesToProposalNotSource()
    {
        // Security-gate (#167): een AutoApprove-feed op een niet-officieel
        // domein (typo/look-alike) mag NOOIT auto-enablen — nieuwe artikelen
        // gaan naar de reviewqueue, community-getypeerd, nooit trust-1.
        using var db = NewDb();
        const string evilBase = "https://playriftbound.com.evil.example/en-us/news/";
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "look-alike", Name = "Look-alike feed", Url = evilBase,
            AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db,
            (evilBase, Ok(Card($"{evilBase}rules-and-releases/nep-artikel", "Nep Artikel"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.NewSources);
        Assert.Empty(await db.Sources.ToListAsync());
        Assert.Equal(1, r.NewProposals);
        var p = await db.SourceProposals.SingleAsync();
        Assert.Equal("proposed", p.Status);
        Assert.Equal("community", p.Type); // gedegradeerd: geen officieel domein
        Assert.Contains("niet-officieel domein", p.Motivation);
    }

    [Fact]
    public async Task RunAsync_OfficialAutoApproveFeed_StaysTrustOneOfficial()
    {
        // Positieve keerzijde: op een officieel domein doet AutoApprove
        // gewoon zijn werk — enabled, official, trust 1.
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "off", Name = "Officiële feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card($"{Base}nieuw", "Nieuw"))));

        await svc.RunAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Equal("official", src.Type);
        Assert.Equal(1, src.TrustTier);
        Assert.True(src.Enabled);
    }

    [Fact]
    public async Task RunAsync_TombstonedUrl_IsNotReCreatedAsSource()
    {
        // Tombstone (#167): een bewust verwijderde feed-bron laat een
        // "rejected" SourceProposal achter; die houdt de URL in de known-set,
        // dus de crawl maakt hem bij een reparse niet stil opnieuw aan — ook
        // niet op een officiële AutoApprove-feed.
        using var db = NewDb();
        var url = $"{Base}verwijderd-artikel";
        db.SourceProposals.Add(new SourceProposal
        {
            Url = url, Name = "Verwijderd", Type = "official", Status = "rejected",
            Motivation = "Handmatig verwijderde feed-bron — niet opnieuw automatisch toevoegen (#167).",
        });
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "off", Name = "Officiële feed", Url = Base, AutoApprove = true, Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var (svc, _) = Service(db, (Base, Ok(Card(url, "Verwijderd"))));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(0, r.NewSources);
        Assert.Empty(await db.Sources.ToListAsync());
        // De tombstone blijft precies één rij (geen duplicaat-proposal).
        Assert.Single(await db.SourceProposals.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_CategoryFilterOnFeed_AppliesDuringCrawl()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Gefilterde feed", Url = "https://playriftbound.com/en-us/news/",
            AutoApprove = true, Cadence = "daily", CategoryFilter = "rules-and-releases",
        });
        await db.SaveChangesAsync();
        var html = Card("https://playriftbound.com/en-us/news/rules-and-releases/behouden", "Behouden")
            + Card("https://playriftbound.com/en-us/news/announcements/weggefilterd", "Weggefilterd");
        var (svc, _) = Service(db, ("https://playriftbound.com/en-us/news/", Ok(html)));

        var r = await svc.RunAsync(onlyDue: false);

        Assert.Equal(1, r.NewSources);
        var src = await db.Sources.SingleAsync();
        Assert.Equal("Behouden", src.Name);
    }

    // --- testinfra -------------------------------------------------------

    private static string Card(string href, string title) =>
        $"""
         <a role="button" href="{href}" data-testid="articlefeaturedcard-component">
           <div data-testid="card-date"><time dateTime="2026-01-01T00:00:00.000Z">x</time></div>
           <div data-testid="card-title">{title}</div>
         </a>
         """;

    private static Func<HttpResponseMessage> Ok(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static (FeedCrawlService Svc, RoutingHandler Handler) Service(
        RbRulesDbContext db, params (string Url, Func<HttpResponseMessage> Respond)[] routes)
    {
        var handler = new RoutingHandler();
        foreach (var (url, respond) in routes) handler.Routes[url] = respond;
        var svc = new FeedCrawlService(db, new HttpClient(handler));
        return (svc, handler);
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests,
    /// zelfde constructie als DeckIngestServiceTests/UrlGuardTests).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public Func<HttpResponseMessage>? Fallback { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Routes.TryGetValue(url, out var respond)) return Task.FromResult(respond());
            if (Fallback is not null) return Task.FromResult(Fallback());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
