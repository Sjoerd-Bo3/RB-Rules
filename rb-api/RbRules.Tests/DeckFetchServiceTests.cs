using System.Net;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>On-demand deck-fetch (#346): een geplakte PA-deck-id wordt strikt
/// gevalideerd (rommel = 400, bereikt Piltover nooit), een al bekend deck is
/// een gewone DB-read zonder externe request, en een onbekend deck loopt door
/// het bestaande ingest-pad (fixture-pagina, gestubde handler — nooit echte
/// HTTP naar Piltover in tests). PA-404 en PA-storing zijn verschillende
/// antwoorden (404 vs 502): "bestaat niet meer" is data, geen storing.</summary>
public class DeckFetchServiceTests
{
    private const string FixtureUuid = "b865434d-7247-41cd-aef8-0e8e4e4ec6c0";
    private const string Base = "https://piltoverarchive.com";

    private static readonly string FixturePage = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "pa-deck-2026-07-13.html"));

    // ── uuid-validatie: rommel wordt een 400 en bereikt Piltover nooit ──

    [Theory]
    [InlineData(null)]                                       // geen body-veld
    [InlineData("")]                                         // leeg
    [InlineData("   ")]                                      // alleen witruimte
    [InlineData("dit-is-geen-uuid")]                         // vorm klopt niet
    [InlineData("b865434d-7247-41cd-aef8-0e8e4e4ec6c")]      // één teken te kort
    [InlineData("b865434d-7247-41cd-aef8-0e8e4e4ec6c00")]    // één teken te lang
    [InlineData("b865434d-7247-41cd-aefg-0e8e4e4ec6c0")]     // 'g' buiten hex
    [InlineData("b865434d7247-41cd-aef8-0e8e4e4ec6c0-")]     // streepjes verschoven
    [InlineData("https://piltoverarchive.com/decks/view/b865434d-7247-41cd-aef8-0e8e4e4ec6c0")]
    public async Task FetchAsync_OngeldigeId_Geeft400_ZonderExterneRequest(string? id)
    {
        using var db = NewDb();
        var (svc, handler) = Service(db);

        var result = await svc.FetchAsync(id);

        Assert.Null(result.Deck);
        Assert.Equal(400, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_GeplakteMegastring_Geeft400_ZonderExterneRequest()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db);

        var result = await svc.FetchAsync(new string('a', 100_000));

        Assert.Equal(400, result.Status);
        Assert.Empty(handler.Requests);
    }

    // ── bestaand-in-DB-pad: gewone read, geen externe request ───────────

    [Fact]
    public async Task FetchAsync_DeckAlInDeDb_GeeftBestaandDetail_ZonderExterneRequest()
    {
        using var db = NewDb();
        SeedBestaandDeck(db);
        await db.SaveChangesAsync();
        var (svc, handler) = Service(db);

        var result = await svc.FetchAsync(FixtureUuid);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Deck);
        Assert.Equal(FixtureUuid, result.Deck!.Id);
        Assert.Equal("Bestaand deck", result.Deck.Name);
        var sectie = Assert.Single(result.Deck.Sections);
        Assert.Equal("legend", sectie.Section);
        // De kern: een bekend deck kost Piltover niets.
        Assert.Empty(handler.Requests);
    }

    /// <summary>Uppercase en witruimte zijn normalisatie, geen fout — de
    /// genormaliseerde id matcht de lowercase PaId in de DB.</summary>
    [Fact]
    public async Task FetchAsync_HoofdlettersEnWitruimte_WordenGenormaliseerd()
    {
        using var db = NewDb();
        SeedBestaandDeck(db);
        await db.SaveChangesAsync();
        var (svc, handler) = Service(db);

        var result = await svc.FetchAsync($"  {FixtureUuid.ToUpperInvariant()}  ");

        Assert.Equal(200, result.Status);
        Assert.Equal(FixtureUuid, result.Deck!.Id);
        Assert.Empty(handler.Requests);
    }

    // ── parse-pad: onbekend deck via het bestaande ingest-pad ───────────

    [Fact]
    public async Task FetchAsync_OnbekendDeck_HaaltParsetEnBewaartViaHetIngestPad()
    {
        using var db = NewDb();
        db.Cards.Add(new Card { RiftboundId = "unl-203-219", Name = "Poppy, Keeper of the Hammer" });
        await db.SaveChangesAsync();
        var (svc, handler) = Service(db);
        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] = () => Ok(FixturePage);

        var result = await svc.FetchAsync(FixtureUuid);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Deck);
        Assert.Equal(FixtureUuid, result.Deck!.Id);
        Assert.Equal("With Hammers.", result.Deck.Name);
        Assert.NotEmpty(result.Deck.Sections);

        // Opgeslagen via het ingest-pad: zelfde upsert, zelfde grootboek-"ok"
        // — de sitemap-run herkent dit deck en fetcht het niet nóg eens.
        var deck = await db.Decks.SingleAsync();
        Assert.Equal(FixtureUuid, deck.PaId);
        Assert.Equal($"{Base}/decks/view/{FixtureUuid}", deck.SourceUrl);
        Assert.Equal(48, await db.DeckCards.CountAsync());
        var ok = await db.RunLogs.SingleAsync(
            l => l.Kind == DeckIngestService.LedgerKind && l.Status == "ok");
        Assert.Equal($"deck:{FixtureUuid}", ok.Ref);

        // PA geeft 403 op kale clients — ook deze route draagt de browser-UA.
        var request = Assert.Single(handler.Requests);
        Assert.Contains("Mozilla", request.UserAgent);
    }

    [Fact]
    public async Task FetchAsync_PiltoverGeeft404_WordtEenEigen404()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db);
        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] =
            () => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await svc.FetchAsync(FixtureUuid);

        Assert.Null(result.Deck);
        Assert.Equal(404, result.Status);
        Assert.Equal("Deck bestaat niet (meer) op Piltover Archive.", result.Error);
    }

    [Fact]
    public async Task FetchAsync_PiltoverStoring_WordtEen502()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db);
        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] =
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await svc.FetchAsync(FixtureUuid);

        Assert.Null(result.Deck);
        Assert.Equal(502, result.Status);
        Assert.Contains("HTTP 500", result.Error);
    }

    [Fact]
    public async Task FetchAsync_PaginaZonderDeckObject_WordtEen502()
    {
        using var db = NewDb();
        var (svc, handler) = Service(db);
        handler.Routes[$"{Base}/decks/view/{FixtureUuid}"] =
            () => Ok("<html><body>geen flight-chunks</body></html>");

        var result = await svc.FetchAsync(FixtureUuid);

        Assert.Equal(502, result.Status);
        Assert.Contains("geen deck-object", result.Error);
    }

    // ── testinfra ───────────────────────────────────────────────────────

    /// <summary>Service met gestubde HTTP (RoutingHandler: alles wat niet
    /// geroute is geeft 404) en throttle nul — nooit echte requests naar
    /// Piltover vanuit tests.</summary>
    private static (DeckFetchService Svc, RoutingHandler Handler) Service(RbRulesDbContext db)
    {
        var handler = new RoutingHandler();
        var ingest = new DeckIngestService(db, new HttpClient(handler)) { Throttle = TimeSpan.Zero };
        return (new DeckFetchService(db, ingest, new DeckBrowserService(db)), handler);
    }

    private static void SeedBestaandDeck(RbRulesDbContext db)
    {
        var deck = new Deck
        {
            PaId = FixtureUuid, Name = "Bestaand deck",
            SourceUrl = $"{Base}/decks/view/{FixtureUuid}",
        };
        db.Decks.Add(deck);
        db.DeckCards.Add(new DeckCard
        {
            Deck = deck, Section = "legend", CardCode = "UNL-203", Quantity = 1,
        });
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
    private class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
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

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };
}
