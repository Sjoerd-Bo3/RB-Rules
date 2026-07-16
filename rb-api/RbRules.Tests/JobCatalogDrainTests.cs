using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Drained-semantiek van de JobCatalog-wrappers (review-fix #190):
/// "gedraineerd" betekent GEEN VERS werk meer — items die zojuist faalden
/// tellen niet mee, anders herkauwt een pad-drain bij rb-ai-uitval of een
/// poison item MaxRepeats keer exact dezelfde falende backlog. Getest door
/// de échte catalogus-jobs (JobCatalog.Find(...).Run) op een minimale DI-
/// container met de echte services over gestubde HTTP en EF InMemory.</summary>
public class JobCatalogDrainTests
{
    // ── classify: ongecapt — na één volledige pass resteren alleen failures ──

    [Fact]
    public async Task Classify_RbAiUitval_AlleenFailuresOver_IsGedraineerd()
    {
        // Regressie (finding 1): Remaining telt na een ongecapte run alléén
        // de zojuist gefaalde changes — kale Remaining==0 zou een drain-lus
        // de volle falende backlog laten herkauwen.
        using var db = NewDb();
        db.Sources.Add(Bron("s1"));
        db.Changes.Add(new Change { SourceId = "s1", ChangeType = "unknown", Diff = "iets" });
        await db.SaveChangesAsync();
        await using var sp = Sp(s => s.AddSingleton(
            new ChangeClassificationService(db, Ai(() => null))));

        var outcome = await JobCatalog.Find("classify")!.Run(sp, _ => { }, CancellationToken.None);

        Assert.Contains("1 mislukt", outcome.Detail);
        Assert.Contains("1 resterend", outcome.Detail);
        Assert.True(outcome.Drained); // failures zijn geen verse werklast
    }

    // ── mine: per run gecapt (25 batches × 8) — failures tellen niet mee ──

    [Fact]
    public async Task Mine_RbAiUitval_AllesFaalt_IsGedraineerd()
    {
        // Regressie (finding 2): rb-ai down ⇒ alle kaarten falen en blijven
        // Remaining — zonder vers-werk-semantiek zou een pad-drain tot
        // MaxRepeats × 25 batches aan futiele calls doen.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298"));
        db.Cards.Add(Kaart("ogn-002-298"));
        await db.SaveChangesAsync();
        await using var sp = Sp(s => s.AddSingleton(
            new MechanicMiningService(db, Ai(() => null))));

        var outcome = await JobCatalog.Find("mine")!.Run(sp, _ => { }, CancellationToken.None);

        Assert.Contains("0 kaarten gemined, 2 resterend", outcome.Detail);
        Assert.True(outcome.Drained);
    }

    [Fact]
    public async Task Mine_MeerKaartenDanDeRunCap_MetVersWerk_IsNietGedraineerd()
    {
        // 201 kaarten bij een run-cap van 200 (25 batches × 8): de laatste
        // kaart is écht vers werk — de drain-lus moet doorgaan.
        using var db = NewDb();
        var ids = Enumerable.Range(0, 201).Select(i => $"ogn-{i:D3}-298").ToList();
        foreach (var id in ids) db.Cards.Add(Kaart(id));
        await db.SaveChangesAsync();
        // Eén statisch antwoord met álle ids: ParseBatch pakt per batch alleen
        // de eigen kaarten eruit, dus elke batch slaagt volledig.
        var allMined = "[" + string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","mechanics":[]}""")) + "]";
        await using var sp = Sp(s => s.AddSingleton(
            new MechanicMiningService(db, Ai(() => allMined))));

        var outcome = await JobCatalog.Find("mine")!.Run(sp, _ => { }, CancellationToken.None);

        Assert.Contains("200 kaarten gemined, 1 resterend", outcome.Detail);
        Assert.False(outcome.Drained);

        // De vervolg-run pakt de laatste kaart en drained.
        var again = await JobCatalog.Find("mine")!.Run(sp, _ => { }, CancellationToken.None);
        Assert.Contains("1 kaarten gemined, 0 resterend", again.Detail);
        Assert.True(again.Drained);
    }

    // ── decks: per run gecapt — de bestaande CapHit gaat nu écht mee ──

    [Fact]
    public async Task Decks_GeenCap_IsGedraineerd_OokAlsDeEnigePaginaFaalt()
    {
        using var db = NewDb();
        await using var sp = Sp(s => s.AddSingleton(DeckService(db, deckCount: 1)));

        var outcome = await JobCatalog.Find("decks")!.Run(sp, _ => { }, CancellationToken.None);

        // De ene pagina faalt (404) — dat is geen cap: gedraineerd.
        Assert.True(outcome.Drained);
    }

    [Fact]
    public async Task Decks_CapGeraakt_IsNietGedraineerd()
    {
        // Regressie (finding 3): de wrapper discardde DeckIngestResult.CapHit
        // en meldde Drained=true op een per-run gecapte job. 401 decks bij de
        // vaste run-cap van 400 ⇒ CapHit ⇒ niet gedraineerd.
        using var db = NewDb();
        await using var sp = Sp(s => s.AddSingleton(DeckService(db, deckCount: 401)));

        var outcome = await JobCatalog.Find("decks")!.Run(sp, _ => { }, CancellationToken.None);

        Assert.Contains("cap van 400", outcome.Detail);
        Assert.False(outcome.Drained);
    }

    // --- testinfra ---------------------------------------------------------

    private const string PaBase = "https://piltoverarchive.com";

    /// <summary>DeckIngestService op een gestubde sitemap met
    /// <paramref name="deckCount"/> entries; de deck-pagina's zelf geven 404
    /// (CapHit wordt vóór het fetchen bepaald — de pagina-inhoud doet er voor
    /// deze tests niet toe). Throttle nul (test-seam).</summary>
    private static DeckIngestService DeckService(RbRulesDbContext db, int deckCount)
    {
        var urls = new StringBuilder("<urlset>");
        for (var i = 0; i < deckCount; i++)
            urls.Append($"<url><loc>{PaBase}/decks/view/00000000-0000-0000-0000-{i:D12}</loc></url>");
        urls.Append("</urlset>");
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            $"{PaBase}/sitemap.xml" => Ok(
                $"<sitemapindex><sitemap><loc>{PaBase}/sitemap/0</loc></sitemap></sitemapindex>"),
            $"{PaBase}/sitemap/0" => Ok(urls.ToString()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        return new DeckIngestService(db, new HttpClient(handler)) { Throttle = TimeSpan.Zero };
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static Source Bron(string id) => new()
    {
        Id = id, Name = "Bron", Url = "https://example.test/x", Type = "official",
        TrustTier = 1, Rank = 1, Parser = "html", Cadence = "daily",
    };

    private static Card Kaart(string id) => new()
    {
        RiftboundId = id, Name = $"Kaart {id}", TextPlain = "Doet iets nuttigs.", Tags = [],
    };

    private static ServiceProvider Sp(Action<ServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Echte RbAiClient op een gestubde handler: null ⇒ 500 (uitval),
    /// anders het gegeven antwoord als {"answer": ...} — zelfde patroon als
    /// ClaimMiningServiceTests/RelationMiningServiceTests.</summary>
    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { answer = a }) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet (patroon JobLedgerTests).</summary>
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
}
