using System.Net;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Run-semantiek van de Piltover Archive-deck-ingest (#15):
/// idempotent via het run_log-grootboek (kind "deckingest", "ok" pas na
/// parse+opslag — #93-patroon), gerichte versheid op de sitemap-lastmod,
/// een cap per run met hervatting, en fouten per deck als data. HTTP is een
/// gestubde handler (sitemap + pagina's), de deck-pagina is de echte
/// fixture; de database is EF InMemory (geen vector-operaties in dit pad).</summary>
public class DeckIngestServiceTests
{
    private const string FixtureUuid = "b865434d-7247-41cd-aef8-0e8e4e4ec6c0";
    private const string TweedeUuid = "11111111-2222-3333-4444-555555555555";
    private const string Base = "https://piltoverarchive.com";

    private static readonly string FixturePage = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "pa-deck-2026-07-13.html"));

    [Fact]
    public async Task RunAsync_SlaatDeckMetKaartenOp_EnKoppeltViaDeVariantgroepering()
    {
        using var db = NewDb();
        SeedCards(db);
        await db.SaveChangesAsync();
        var (svc, handler) = Service(db, ShardMet((FixtureUuid, "2026-07-13T13:53:29.172Z")));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Saved);
        Assert.Equal(0, r.Failed);
        Assert.False(r.CapHit);

        var deck = await db.Decks.SingleAsync();
        Assert.Equal(FixtureUuid, deck.PaId);
        Assert.Equal("With Hammers.", deck.Name);
        Assert.Equal(74, deck.Views);
        Assert.Equal(["Body", "Order"], deck.Domains);
        // Attributie: elk deck draagt zijn eigen PA-pagina als bron.
        Assert.Equal($"{Base}/decks/view/{FixtureUuid}", deck.SourceUrl);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 13, 13, 53, 29, 172, TimeSpan.Zero),
            deck.PaUpdatedAt);

        // 1 legend + 47 sectie-entries; koppeling loopt via de canonieke
        // kaart — de alt-art-rune telt op de basisprinting (#54/#57).
        var cards = await db.DeckCards.ToListAsync();
        Assert.Equal(48, cards.Count);
        var rune = Assert.Single(cards, c => c.CardCode == "OGN-126a");
        Assert.Equal("ogn-126-298", rune.CanonicalRiftboundId);
        Assert.Equal(8, rune.Quantity);
        var legend = Assert.Single(cards, c => c.Section == "legend");
        Assert.Equal("unl-203-219", legend.CanonicalRiftboundId);
        // Onbekende kaarten crashen niet maar tellen in het run-detail.
        Assert.Equal(cards.Count(c => c.CanonicalRiftboundId == null), r.UnknownCards);
        Assert.Equal(46, r.UnknownCards);

        // Grootboek: "ok" pas na geslaagde parse+opslag, mét detail.
        var ok = await db.RunLogs.SingleAsync(
            l => l.Kind == "deckingest" && l.Status == "ok");
        Assert.Equal($"deck:{FixtureUuid}", ok.Ref);
        Assert.Contains("48 kaartregels", ok.Detail);
        Assert.Contains("46 onbekende", ok.Detail);

        // PA geeft 403 op kale clients — elke request draagt de browser-UA.
        Assert.All(handler.Requests, req => Assert.Contains("Mozilla", req.UserAgent));
    }

    [Fact]
    public async Task RunAsync_TweedeRun_FetchtNietsOpnieuw()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db, ShardMet((FixtureUuid, "2026-07-13T13:53:29.172Z")));
        await svc.RunAsync();
        var paginasNaEersteRun = DeckRequests(handler);

        var r = await svc.RunAsync();

        // Grootboek-ok + ongewijzigde lastmod ⇒ niets te doen.
        Assert.Equal(0, r.Fetched);
        Assert.Equal(paginasNaEersteRun, DeckRequests(handler));
        Assert.Equal(1, await db.Decks.CountAsync());
        Assert.Equal(48, await db.DeckCards.CountAsync());
    }

    [Fact]
    public async Task RunAsync_NieuwereLastmod_VerverstZonderDuplicaten()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db, ShardMet((FixtureUuid, "2026-07-13T13:53:29.172Z")));
        await svc.RunAsync();
        var eersteFetch = (await db.Decks.SingleAsync()).FetchedAt;

        // PA meldt een wijziging: de sitemap-lastmod schuift voorbij onze fetch.
        handler.Routes[$"{Base}/sitemap/0"] =
            () => Ok(ShardMet((FixtureUuid, "2126-01-01T00:00:00.000Z")).Shard);
        var r = await svc.RunAsync();

        Assert.Equal(1, r.Saved);
        var deck = await db.Decks.SingleAsync(); // upsert op PaId, geen tweede rij
        Assert.True(deck.FetchedAt >= eersteFetch);
        // Kaartregels integraal vervangen — niet opgestapeld.
        Assert.Equal(48, await db.DeckCards.CountAsync());
    }

    [Fact]
    public async Task RunAsync_Cap_StoptNetjes_EnDeVolgendeRunGaatVerder()
    {
        using var db = NewDb();
        var (svc, _) = Service(db, ShardMet(
            (FixtureUuid, "2026-07-13T13:53:29.172Z"),
            (TweedeUuid, "2026-07-01T00:00:00.000Z")));

        var r1 = await svc.RunAsync(maxPages: 1);

        Assert.True(r1.CapHit);
        Assert.Equal(1, r1.Fetched);
        Assert.Contains("cap van 1", r1.Message);
        Assert.Equal(1, await db.Decks.CountAsync());

        // De volgende run pakt op waar het grootboek gebleven is.
        var r2 = await svc.RunAsync(maxPages: 1);
        Assert.Equal(1, r2.Fetched);
        Assert.False(r2.CapHit);
        Assert.Equal(2, await db.Decks.CountAsync());
    }

    [Fact]
    public async Task RunAsync_FoutBijEenDeck_IsData_EnDeRunGaatDoor()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db, ShardMet(
            (FixtureUuid, "2026-07-13T13:53:29.172Z"),
            (TweedeUuid, "2026-07-01T00:00:00.000Z")));
        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] =
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Equal(1, r.Saved);
        Assert.Contains("details in run_log", r.Message);
        var fout = await db.RunLogs.SingleAsync(
            l => l.Kind == "deckingest" && l.Status == "error");
        Assert.Equal($"deck:{FixtureUuid}", fout.Ref);
        Assert.Contains("HTTP 500", fout.Detail);
        // Geen "ok" zonder opslag: het mislukte deck komt de volgende run terug.
        Assert.Equal(TweedeUuid, (await db.Decks.SingleAsync()).PaId);

        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] = () => Ok(FixturePage);
        var herstel = await svc.RunAsync();
        Assert.Equal(1, herstel.Saved);
        Assert.Equal(2, await db.Decks.CountAsync());
    }

    [Fact]
    public async Task RunAsync_SitemapWeg_IsEenNetteFout()
    {
        using var db = NewDb();
        var handler = new RoutingHandler();
        var svc = new DeckIngestService(db, new HttpClient(handler)) { Throttle = TimeSpan.Zero };

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Saved);
        Assert.Contains("sitemap-index niet opgehaald", r.Message);
        var fout = await db.RunLogs.SingleAsync(l => l.Kind == "deckingest");
        Assert.Equal("error", fout.Status);
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>Service + handler met sitemap-index, één shard en per deck
    /// een pagina: de fixture voor het fixture-deck, synthetische flight-HTML
    /// voor andere uuids. Throttle op nul — netiquette is productie-gedrag.</summary>
    private static (DeckIngestService Svc, RoutingHandler Handler) Service(
        RbRulesDbContext db, (string Shard, (string Uuid, string? LastMod)[] Decks) shard)
    {
        var handler = new RoutingHandler();
        handler.Routes[$"{Base}/sitemap.xml"] = () => Ok(
            $"<sitemapindex><sitemap><loc>{Base}/sitemap/0</loc></sitemap></sitemapindex>");
        handler.Routes[$"{Base}/sitemap/0"] = () => Ok(shard.Shard);
        foreach (var (uuid, _) in shard.Decks)
            handler.Routes[$"{Base}/decks/view/{uuid}"] = uuid == FixtureUuid
                ? () => Ok(FixturePage)
                : () => Ok(SynthetischeDeckPagina(uuid));
        var svc = new DeckIngestService(db, new HttpClient(handler)) { Throttle = TimeSpan.Zero };
        return (svc, handler);
    }

    private static (string Shard, (string Uuid, string? LastMod)[] Decks) ShardMet(
        params (string Uuid, string? LastMod)[] decks)
    {
        var urls = string.Join("", decks.Select(d =>
            $"<url><loc>{Base}/decks/view/{d.Uuid}</loc>"
            + (d.LastMod is null ? "" : $"<lastmod>{d.LastMod}</lastmod>")
            + "</url>"));
        return ($"<urlset>{urls}</urlset>", decks);
    }

    private static string SynthetischeDeckPagina(string uuid) =>
        "<script>self.__next_f.push([1,\"{\\\"deck\\\":{\\\"id\\\":\\\"" + uuid
        + "\\\",\\\"name\\\":\\\"Tweede deck\\\",\\\"views\\\":1}}\"])</script>";

    private static int DeckRequests(RoutingHandler handler) =>
        handler.Requests.Count(r => r.Url.Contains("/decks/view/"));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    private static void SeedCards(RbRulesDbContext db)
    {
        db.Cards.Add(new Card { RiftboundId = "ogn-126-298", Name = "Body Rune" });
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-126a-298", Name = "Body Rune (Alternate Art)",
            VariantOf = "ogn-126-298",
        });
        db.Cards.Add(new Card { RiftboundId = "unl-203-219", Name = "Poppy, Keeper of the Hammer" });
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(
                            new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                                .ValueConverter<Pgvector.Vector, string>(
                                v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<(string Url, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Headers.UserAgent.ToString()));
            return Task.FromResult(Routes.TryGetValue(url, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
