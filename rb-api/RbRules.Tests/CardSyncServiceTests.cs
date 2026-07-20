using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Bronvolgorde van de kaart-sync (#150): auto-modus is Riot eerst
/// (officiële bron leidend, onvoorwaardelijke upsert) en riftcodex daarna
/// alléén als aanvulling — bestaande kaarten blijven daar onaangeraakt, zodat
/// een net herstelde naam niet opnieuw beschadigd raakt. Bronnen zijn gestubde
/// HTTP-routes (RoutingHandler, DeckIngestServiceTests-patroon); database is
/// EF InMemory. CARD_SOURCE wordt per test expliciet gezet en hersteld —
/// binnen één testklasse draait xUnit sequentieel.</summary>
public class CardSyncServiceTests
{
    // ---- auto-modus -------------------------------------------------------

    [Fact]
    public async Task Auto_RiotLeadsAndRiftcodexSupplements()
    {
        using var db = NewDb();
        // Productie-situatie #150: een corrupte streepjes-naam zonder
        // komma-bewijs — alleen de Riot-pass kan die nog herstellen.
        var damaged = SeedCard("ogn-001-298", "Darius - Trifarian", "Overwhelm.");
        damaged.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        damaged.EmbeddingModel = EmbeddingConfig.Model;
        db.Cards.Add(damaged);
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        RouteRiot(handler,
            RiotCardJson("ogn-001-298", "Darius, Trifarian"),
            RiotCardJson("ogn-002-298", "Jinx, Loose Cannon"));
        RouteRiftcodexSets(handler, ("OGN", "Origins"), ("JDG", "Judge Program"));
        RouteRiftcodexCards(handler, "OGN",
            // Zelfde printing, maar met riftcodex-schade: naam, rariteit en
            // tekst wijken af — de aanvul-pass mag hier niets mee doen.
            RiftcodexCardJson("ogn-001-298", "Darius - Trifarian",
                text: "Andere tekst.", rarity: "Epic"));
        RouteRiftcodexCards(handler, "JDG",
            RiftcodexCardJson("jdg-001-001", "Darius - Trifarian (Prerelease)",
                setId: "JDG", setLabel: "Judge Program", rarity: "Promo"));

        var result = await WithCardSourceAsync(null, () => service.SyncAsync());

        Assert.Equal(("riot", 2, "riftcodex", 1),
            (result.Source, result.Cards, result.SupplementSource, result.SupplementCards));
        Assert.Equal("riot+riftcodex", result.SourceLabel);
        Assert.Equal("2 kaarten via riot + 1 aanvullend via riftcodex", result.CardsSummary);

        // Riot herstelde de naam; de riftcodex-pass liet de kaart met rust.
        var repaired = await db.Cards.FindAsync("ogn-001-298");
        Assert.Equal("Darius, Trifarian", repaired!.Name);
        Assert.Equal("Common", repaired.Rarity);
        Assert.Equal("Overwhelm.", repaired.TextPlain);
        // Naamwijziging: de naam zit in de embeddingtekst (CardText.Compose),
        // dus de embed-pijplijn moet de kaart opnieuw oppakken.
        Assert.Null(repaired.Embedding);
        Assert.Null(repaired.EmbeddingModel);

        // De JDG-promo is aangevuld, met naambewijs uit de vérse Riot-pass:
        // "Darius, Trifarian" is dan al bekend, dus de streepjes-vorm
        // normaliseert direct naar de komma-vorm.
        var promo = await db.Cards.FindAsync("jdg-001-001");
        Assert.Equal("Darius, Trifarian (Prerelease)", promo!.Name);

        // Set-metadata is óók aanvulling: de Riot-gallery kent alleen de
        // set-code, riftcodex levert échte naam en releasedatum.
        var ogn = await db.CardSets.FindAsync("OGN");
        Assert.Equal("Origins", ogn!.Name);
        Assert.Equal(new DateOnly(2025, 10, 31), ogn.PublishedOn);
        Assert.NotNull(await db.CardSets.FindAsync("JDG"));
    }

    [Fact]
    public async Task Auto_RiotDown_FallsBackToRiftcodexAlone()
    {
        using var db = NewDb();
        var (service, handler) = Service(db);
        handler.Routes[GalleryUrl] = () => new(HttpStatusCode.InternalServerError);
        RouteRiftcodexSets(handler, ("OGN", "Origins"));
        RouteRiftcodexCards(handler, "OGN",
            RiftcodexCardJson("ogn-002-298", "Jinx, Loose Cannon"));

        var progress = new List<string>();
        var result = await WithCardSourceAsync(null, () => service.SyncAsync(progress.Add));

        Assert.Equal(("riftcodex", 1, null), (result.Source, result.Cards, result.SupplementSource));
        Assert.Equal("1 kaarten via riftcodex", result.CardsSummary);
        Assert.Contains(progress, p => p.Contains("overschakelen naar Riftcodex"));
        Assert.NotNull(await db.Cards.FindAsync("ogn-002-298"));
    }

    [Fact]
    public async Task Auto_RiftcodexDown_RiotResultStandsWithInfoLog()
    {
        using var db = NewDb();
        var (service, handler) = Service(db);
        RouteRiot(handler, RiotCardJson("ogn-002-298", "Jinx, Loose Cannon"));
        handler.Routes[SetsUrl] = () => new(HttpStatusCode.InternalServerError);

        var progress = new List<string>();
        // Fouten zijn hier data, geen jobfout: geen exception.
        var result = await WithCardSourceAsync(null, () => service.SyncAsync(progress.Add));

        Assert.Equal(("riot", 1, null), (result.Source, result.Cards, result.SupplementSource));
        Assert.Equal("riot", result.SourceLabel);
        Assert.Contains(progress, p => p.Contains("Riot-resultaat staat"));

        var log = Assert.Single(await db.RunLogs.ToListAsync());
        Assert.Equal(("cards", "riftcodex-aanvulling", "info"), (log.Kind, log.Ref, log.Status));
        Assert.Contains("aanvulling overgeslagen", log.Detail);
    }

    // ---- voorrangsregel op de presentatievelden (#270) --------------------

    /// <summary>Riot-battlefield met alle presentatievelden, in de echte
    /// gallery-vorm (liggend, 1039x744).</summary>
    private const string RiotBattlefieldJson = """
        {
          "id": "unl-205-219",
          "collectorNumber": 205,
          "name": "Abandoned Hall",
          "publicCode": "UNL-205/219",
          "set": {"label": "Card Set", "value": {"id": "UNL", "label": "Unleashed"}},
          "cardType": {"label": "Card Type", "type": [{"id": "battlefield", "label": "Battlefield"}]},
          "domain": {"label": "Domain", "values": [{"id": "colorless", "label": "Colorless"}]},
          "cardImage": {
            "type": "image",
            "url": "https://example.com/hall-1039x744.png",
            "accessibilityText": "Riftbound Battlefield: Abandoned Hall. Officiële alt-tekst.",
            "dimensions": {"width": 1039, "height": 744, "aspectRatio": 1.3965},
            "colors": {"primary": "#222C44", "secondary": "#DCEBF9"}
          },
          "orientation": "landscape",
          "illustrator": {"label": "Artist", "values": [{"id": "envar", "label": "Envar Studio"}]},
          "flags": [{"id": "new", "label": "New"}]
        }
        """;

    [Fact]
    public async Task Sync_BattlefieldBehoudtLiggendeAfmetingen()
    {
        // Regressie #269: battlefields (66 van de 1178) werden als portret
        // bijgesneden omdat de maat nooit gesynchroniseerd werd.
        using var db = NewDb();
        var (service, handler) = Service(db);
        RouteRiot(handler, RiotBattlefieldJson);
        RouteRiftcodexSets(handler);

        await WithCardSourceAsync("riot", () => service.SyncAsync());

        var card = await db.Cards.FindAsync("unl-205-219");
        Assert.Equal((1039, 744), (card!.ImageWidth, card.ImageHeight));
        Assert.True(CardPresentation.IsLandscape(card.ImageWidth, card.ImageHeight));
        Assert.Equal("UNL-205/219", card.PublicCode);
        Assert.Equal("Envar Studio", card.Illustrator);
        Assert.Equal(["New"], card.Flags);
        Assert.Equal("#222c44", card.ImageColorPrimary);
        Assert.Equal("Riftbound Battlefield: Abandoned Hall. Officiële alt-tekst.",
            card.ImageAltText);
    }

    [Fact]
    public async Task Sync_RiotOverschrijftEenEerderAangevuldVeld()
    {
        // Voorrangsregel (#270): zodra Riot een waarde levert, wint die van de
        // aanvulling — zonder per-veld-herkomstadministratie.
        using var db = NewDb();
        var seeded = SeedCard("unl-205-219", "Abandoned Hall");
        seeded.Illustrator = "Aanvulling Studio";
        seeded.ImageAltText = "Zelf samengestelde alt-tekst.";
        seeded.ImageWidth = 744;
        seeded.ImageHeight = 1039;
        db.Cards.Add(seeded);
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        RouteRiot(handler, RiotBattlefieldJson);
        RouteRiftcodexSets(handler);

        await WithCardSourceAsync("riot", () => service.SyncAsync());

        var card = await db.Cards.FindAsync("unl-205-219");
        Assert.Equal("Envar Studio", card!.Illustrator);
        Assert.Equal("Riftbound Battlefield: Abandoned Hall. Officiële alt-tekst.",
            card.ImageAltText);
        Assert.Equal((1039, 744), (card.ImageWidth, card.ImageHeight));
    }

    [Fact]
    public async Task Sync_AanvullingVultAlleenGatenEnRaaktRiotVeldenNiet()
    {
        // De andere helft van de voorrangsregel: de aanvul-pass mag een door
        // Riot gevuld veld nooit aanraken, en vult alleen wat leeg is.
        using var db = NewDb();
        var (service, handler) = Service(db);
        RouteRiot(handler, RiotCardJson("ogn-001-298", "Darius, Trifarian"));
        RouteRiftcodexSets(handler, ("OGN", "Origins"));
        RouteRiftcodexCards(handler, "OGN",
            // Riftcodex kent supertype (Riot niet) én een eigen artist.
            RiftcodexCardJson("ogn-001-298", "Darius - Trifarian"));

        await WithCardSourceAsync(null, () => service.SyncAsync());

        var card = await db.Cards.FindAsync("ogn-001-298");
        // Gat gevuld: de gallery kent geen supertype.
        Assert.Equal("Champion", card!.Supertype);
        Assert.Equal("Shawn Tan", card.Illustrator);
        // Door Riot gevuld: onaangeraakt gebleven.
        Assert.Equal("Darius, Trifarian", card.Name);
        Assert.Equal("Common", card.Rarity);
        Assert.Equal("Overwhelm.", card.TextPlain);
    }

    // ---- regressie #150: aanvul-pass raakt herstelde namen niet meer aan ---

    [Fact]
    public async Task Auto_SupplementPassDoesNotTouchAJustRepairedName()
    {
        using var db = NewDb();
        // Het bewijs (de komma-tweeling) is weg: de reparatiestap kan deze
        // naam niet herstellen — precies de 301-namen-situatie uit #150.
        db.Cards.Add(SeedCard("ogn-010-298", "Kai'Sa - Survivor", "Evolve."));
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        RouteRiot(handler, RiotCardJson("ogn-010-298", "Kai'Sa, Survivor", text: "Evolve."));
        RouteRiftcodexSets(handler, ("OGN", "Origins"));
        RouteRiftcodexCards(handler, "OGN",
            RiftcodexCardJson("ogn-010-298", "Kai'Sa - Survivor", text: "Riftcodex-tekst."));

        var result = await WithCardSourceAsync(null, () => service.SyncAsync());

        // Vóór deze fix draaide auto-modus riftcodex-eerst en bleef de
        // schade staan; nu herstelt Riot de naam en blijft die staan.
        var card = await db.Cards.FindAsync("ogn-010-298");
        Assert.Equal("Kai'Sa, Survivor", card!.Name);
        Assert.Equal("Evolve.", card.TextPlain);
        Assert.Equal(0, result.SupplementCards);
    }

    // ---- expliciete overrides blijven exact werken -------------------------

    [Fact]
    public async Task ExplicitRiot_OnlyTalksToRiot()
    {
        using var db = NewDb();
        var (service, handler) = Service(db);
        RouteRiot(handler, RiotCardJson("ogn-002-298", "Jinx, Loose Cannon"));

        var result = await WithCardSourceAsync("riot", () => service.SyncAsync());

        Assert.Equal(("riot", null), (result.Source, result.SupplementSource));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("riftcodex"));
    }

    [Fact]
    public async Task ExplicitRiftcodex_OnlyTalksToRiftcodexWithFullUpsert()
    {
        using var db = NewDb();
        db.Cards.Add(SeedCard("ogn-001-298", "Darius, Trifarian", "Overwhelm.", rarity: "Common"));
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        RouteRiftcodexSets(handler, ("OGN", "Origins"));
        RouteRiftcodexCards(handler, "OGN",
            RiftcodexCardJson("ogn-001-298", "Darius - Trifarian", rarity: "Epic"));

        var result = await WithCardSourceAsync("riftcodex", () => service.SyncAsync());

        Assert.Equal(("riftcodex", null), (result.Source, result.SupplementSource));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("playriftbound"));
        // Volledige upsert (géén aanvul-modus): velden komen van riftcodex,
        // maar een bestaande naam wint altijd (ResolveName).
        var card = await db.Cards.FindAsync("ogn-001-298");
        Assert.Equal("Darius, Trifarian", card!.Name);
        Assert.Equal("Epic", card.Rarity);
    }

    [Fact]
    public async Task ExplicitRiftcodex_FailurePropagates_NoFallback()
    {
        using var db = NewDb();
        var (service, handler) = Service(db);
        handler.Routes[SetsUrl] = () => new(HttpStatusCode.InternalServerError);
        RouteRiot(handler, RiotCardJson("ogn-002-298", "Jinx, Loose Cannon"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => WithCardSourceAsync("riftcodex", () => service.SyncAsync()));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("playriftbound"));
    }

    // ---- embedding-invalidatie bij naamwijziging (upsert) -------------------

    [Fact]
    public async Task Upsert_NameChangeInvalidatesEmbedding()
    {
        using var db = NewDb();
        var card = SeedCard("ogn-001-298", "Darius - Trifarian", "Overwhelm.");
        card.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        card.EmbeddingModel = EmbeddingConfig.Model;
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        // Zelfde tekst, alleen de naam wijzigt — vóór #150 bleef de
        // embedding dan staan terwijl de naam wél in de embeddingtekst zit.
        RouteRiot(handler, RiotCardJson("ogn-001-298", "Darius, Trifarian"));
        await WithCardSourceAsync("riot", () => service.SyncAsync());

        var synced = await db.Cards.FindAsync("ogn-001-298");
        Assert.Equal("Darius, Trifarian", synced!.Name);
        Assert.Null(synced.Embedding);
        Assert.Null(synced.EmbeddingModel);
    }

    [Fact]
    public async Task Upsert_UnchangedNameAndTextKeepEmbedding()
    {
        using var db = NewDb();
        var card = SeedCard("ogn-001-298", "Darius, Trifarian", "Overwhelm.");
        card.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        card.EmbeddingModel = EmbeddingConfig.Model;
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var (service, handler) = Service(db);
        RouteRiot(handler, RiotCardJson("ogn-001-298", "Darius, Trifarian"));
        await WithCardSourceAsync("riot", () => service.SyncAsync());

        // Geen echte wijziging = geen churn: de embedding blijft staan.
        var synced = await db.Cards.FindAsync("ogn-001-298");
        Assert.NotNull(synced!.Embedding);
        Assert.Equal(EmbeddingConfig.Model, synced.EmbeddingModel);
    }

    // ---- opbouw -------------------------------------------------------------

    private const string GalleryUrl = "https://playriftbound.com/en-us/card-gallery/";
    private const string SetsUrl = "https://api.riftcodex.com/sets?page=1&size=100";
    private const string GalleryHtml =
        "<html><script src=\"/_next/static/test-build/_buildManifest.js\"></script></html>";

    /// <summary>CARD_SOURCE is proces-breed: per test expliciet zetten (null =
    /// auto) en in de finally herstellen.</summary>
    private static async Task<CardSyncResult> WithCardSourceAsync(
        string? mode, Func<Task<CardSyncResult>> run)
    {
        var original = Environment.GetEnvironmentVariable("CARD_SOURCE");
        Environment.SetEnvironmentVariable("CARD_SOURCE", mode);
        try { return await run(); }
        finally { Environment.SetEnvironmentVariable("CARD_SOURCE", original); }
    }

    private static (CardSyncService Service, RoutingHandler Handler) Service(RbRulesDbContext db)
    {
        var handler = new RoutingHandler();
        return (new CardSyncService(db, new HttpClient(handler)), handler);
    }

    private static void RouteRiot(RoutingHandler handler, params string[] cardJsons)
    {
        handler.Routes[GalleryUrl] = () => Response(GalleryHtml);
        handler.Routes["https://playriftbound.com/_next/data/test-build/en-us/card-gallery.json"] =
            () => Response("{\"pageProps\": {\"page\": {\"blades\": [{\"cards\": {\"items\": ["
                + string.Join(",", cardJsons) + "]}}]}}}");
    }

    private static void RouteRiftcodexSets(
        RoutingHandler handler, params (string Id, string Name)[] sets)
    {
        var items = string.Join(",", sets.Select(s =>
            $"{{\"set_id\": \"{s.Id}\", \"name\": \"{s.Name}\", " +
            "\"card_count\": 298, \"published_on\": \"2025-10-31T00:00:00\"}"));
        handler.Routes[SetsUrl] = () => Response("{\"items\": [" + items + "]}");
    }

    private static void RouteRiftcodexCards(
        RoutingHandler handler, string setId, params string[] cardJsons)
    {
        handler.Routes[$"https://api.riftcodex.com/cards?set_id={setId}&page=1&size=100"] =
            () => Response("{\"items\": [" + string.Join(",", cardJsons) + "]}");
    }

    /// <summary>Riot-gallery-kaart in de echte veld-vorm (RiotCardMapperTests).</summary>
    private static string RiotCardJson(string id, string name, string text = "Overwhelm.") => $$$"""
        {
          "id": "{{{id}}}",
          "collectorNumber": 7,
          "name": "{{{name}}}",
          "set": {"label": "Card Set", "value": {"id": "OGN", "label": "Origins"}},
          "cardType": {"label": "Card Type", "type": [{"id": "unit", "label": "Unit"}]},
          "rarity": {"label": "Rarity", "value": {"id": "common", "label": "Common"}},
          "domain": {"label": "Domain", "values": [{"id": "fury", "label": "Fury"}]},
          "energy": {"label": "Energy", "value": {"id": 2, "label": "2"}},
          "might": {"label": "Might", "value": {"id": 3, "label": "3"}},
          "text": {"label": "Ability", "richText": {"type": "html", "body": "<p>{{{text}}}</p>"}},
          "cardImage": {"type": "image", "url": "https://example.com/kaart.png"},
          "tags": {"label": "Tags", "tags": ["Noxus"]}
        }
        """;

    /// <summary>Riftcodex-kaart in de echte veld-vorm (API-snapshot-fixture).</summary>
    private static string RiftcodexCardJson(
        string riftboundId, string name, string text = "Overwhelm.",
        string setId = "OGN", string setLabel = "Origins", string rarity = "Common") => $$$"""
        {
          "riftbound_id": "{{{riftboundId}}}",
          "name": "{{{name}}}",
          "collector_number": 7,
          "attributes": {"energy": 2, "might": 3},
          "classification": {"type": "Unit", "supertype": "Champion", "rarity": "{{{rarity}}}", "domain": ["Fury"]},
          "text": {"plain": "{{{text}}}", "rich": "<p>{{{text}}}</p>"},
          "set": {"set_id": "{{{setId}}}", "label": "{{{setLabel}}}"},
          "media": {"image_url": "https://example.com/kaart.png", "artist": "Shawn Tan"},
          "orientation": "portrait",
          "new": false
        }
        """;

    private static Card SeedCard(
        string id, string name, string? text = null, string? rarity = null) => new()
    {
        RiftboundId = id, Name = name, TextPlain = text, Rarity = rarity,
        SetId = id.Split('-')[0].ToUpperInvariant(),
    };

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

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

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; de reparatiestap draait er wel
            // in (Postgres) — voor de test volstaat negeren.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }
}
