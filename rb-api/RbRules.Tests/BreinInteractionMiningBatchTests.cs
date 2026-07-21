using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Batch-mining (#323): K kaarten per rb-ai-sessie, met partial
/// salvage, kaartcode-gebonden narekening, rij-provenance (model + positie) en
/// de eerlijke telling (ADR-20: nooit K als resultaat melden). De stub speelt
/// rb-ai's NDJSON-batchcontract na (heartbeat-frames + done-frame).</summary>
public class BreinInteractionMiningBatchTests
{
    // ── Verificatiepunt 3: partial salvage + watermark alleen op geslaagd ────

    [Fact]
    public async Task Batch_SessieSterftNa2Van3_TweeGewatermarkt_DerdeKomtTerug()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        await SeedCardAsync(db, "ogn-003", "Gamma", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var requests = new List<(string Path, string Body)>();
        var progress = new List<string>();
        var svc = Service(db, requests, batchK: 3, alias: "fable", _ => Ndjson(
            """{"type":"card","code":"ogn-001","done":1,"total":3}""",
            """{"type":"card","code":"ogn-002","done":2,"total":3}""",
            """
            {"type":"done","results":[
              {"code":"ogn-001","ok":true,"interactions":[{"from":"mechanic:Deflect","to":"mechanic:Assault","kind":"COUNTERS","interacts":true}]},
              {"code":"ogn-002","ok":true,"interactions":[]},
              {"code":"ogn-003","ok":false,"reason":"timeout"}
            ],"unknownCode":1,"usage":{"inputTokens":5200,"outputTokens":830}}
            """));

        var r = await svc.RunAsync(maxFocusCards: 3, maxMechanicSubjects: 0,
            progress: progress.Add);

        // Eén batch-sessie, drie kaarten aangeboden — en de TELLING is eerlijk
        // (ADR-20): 1 gefaalde kaart, niet "3 verwerkt dus 3 gelukt".
        var (path, body) = Assert.Single(requests);
        Assert.Equal("/extract/interactions/batch", path);
        Assert.Equal(1, r.BatchSessions);
        Assert.Equal(3, r.BatchK);
        Assert.Equal("fable", r.ModelAlias);
        Assert.Equal(1, r.Failed);
        Assert.Equal("timeout×1", r.FailureDetail);
        Assert.Equal(1, r.UnknownCode);
        Assert.Contains("model fable, K=3", r.Summary);

        // De payload draagt de ALIAS (rb-ai vertaalt) en per kaart een eigen
        // refs-vocabulaire.
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("fable", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("cards").GetArrayLength());

        // Watermark ALLEEN op de geslaagde kaarten — de derde komt terug
        // (mutatie-anker: wie de hele groep watermarkt, maakt dit rood).
        Assert.NotNull((await Card(db, "ogn-001")).InteractionsMinedAt);
        Assert.NotNull((await Card(db, "ogn-002")).InteractionsMinedAt);
        Assert.Null((await Card(db, "ogn-003")).InteractionsMinedAt);

        // Rij-provenance (#299-les: op de RIJ): LETTERLIJK model-ID + positie.
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("claude-fable-5", ix.ExtractModel);
        Assert.Equal(1, ix.ExtractBatchPosition);

        // Run-metering: model-ID en de sessie-usage op de run-rij.
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal("claude-fable-5", run.LlmModel);
        Assert.Equal(5200, run.InputTokens);
        Assert.Equal(830, run.OutputTokens);
        // Uitgeschreven literal (#286-les): de v5-bump is een WAARNEMING van
        // deze PR — een latere versiebump hoort deze regel bewust rood te maken.
        Assert.Equal("breinmine-interactions-v5", run.PromptVersion);

        // Heartbeat → job-voortgang: per binnengekomen kaart een levensteken.
        Assert.Contains(progress, p => p.Contains("ogn-001") && p.Contains("(1/3)"));
        Assert.Contains(progress, p => p.Contains("ogn-002") && p.Contains("(2/3)"));
    }

    // ── Verificatiepunt 2 (rb-api-muur): per-kaart-narekening, geen unie ─────

    [Fact]
    public async Task Batch_VocabVanKaartB_BijKaartA_WordtDoorDeTweedeMuurGeweigerd()
    {
        using var db = NewDb();
        // Twee kaarten met DISJUNCTE vocabulaires (geen gedeelde mechaniek, dus
        // ook geen partner-relatie): Tank/Snipe hoort alléén bij ogn-002.
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit",
            "Tank reduces Snipe damage.", ["Tank", "Snipe"]);

        var requests = new List<(string Path, string Body)>();
        var svc = Service(db, requests, batchK: 2, alias: "fable", _ => Ndjson(
            """
            {"type":"done","results":[
              {"code":"ogn-001","ok":true,"interactions":[{"from":"mechanic:Tank","to":"mechanic:Snipe","kind":"COUNTERS","interacts":true}]},
              {"code":"ogn-002","ok":true,"interactions":[{"from":"mechanic:Tank","to":"mechanic:Snipe","kind":"COUNTERS","interacts":true}]}
            ]}
            """));

        var r = await svc.RunAsync(maxFocusCards: 2, maxMechanicSubjects: 0);

        // Het besmette item bij kaart A valt weg (refs buiten háár vocabulaire);
        // hetzelfde paar bij kaart B overleeft — de poort rekent PER KAART, niet
        // tegen de unie. Eén rij, afgeleid van ogn-002, positie 2.
        Assert.Equal(1, r.Extracted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("mechanic:Tank", ix.AgentRef);
        Assert.Equal(2, ix.ExtractBatchPosition);
        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal("card:ogn-002", assertion.DerivedFromRef);
        // Beide kaarten gaven een geldig antwoord → beide gewatermarkt.
        Assert.NotNull((await Card(db, "ogn-001")).InteractionsMinedAt);
        Assert.NotNull((await Card(db, "ogn-002")).InteractionsMinedAt);
    }

    // ── Hele sessie dood: K kaarten uitval, nul watermarks ───────────────────

    [Fact]
    public async Task Batch_HeleSessie5xx_AlleKaartenUitval_GeenWatermark()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var requests = new List<(string Path, string Body)>();
        var svc = Service(db, requests, batchK: 2, alias: "fable",
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    """{"error":"extractie mislukt","reason":"max_turns"}""",
                    Encoding.UTF8, "application/json"),
            });

        var r = await svc.RunAsync(maxFocusCards: 2, maxMechanicSubjects: 0);

        // De blast radius is K kaarten — expliciet geteld, met de reden.
        Assert.Equal(2, r.Failed);
        Assert.Equal("5xx×2 (max_turns×2)", r.FailureDetail);
        Assert.All(await db.Cards.ToListAsync(), c => Assert.Null(c.InteractionsMinedAt));
        Assert.Empty(await db.Interactions.ToListAsync());
    }

    // ── Kapotte batch-envelop = uitval, geen leeg resultaat ──────────────────

    [Fact]
    public async Task Batch_KapotteEnvelop_TeltAlsOnleesbaar_GeenWatermark()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var requests = new List<(string Path, string Body)>();
        var svc = Service(db, requests, batchK: 2, alias: "fable",
            _ => Ndjson("""{"type":"done","results":"none"}"""));

        var r = await svc.RunAsync(maxFocusCards: 2, maxMechanicSubjects: 0);

        Assert.Equal(2, r.Failed);
        Assert.Equal("onleesbaar antwoord×2", r.FailureDetail);
        Assert.All(await db.Cards.ToListAsync(), c => Assert.Null(c.InteractionsMinedAt));
    }

    // ── K=1: exact het losse pad, mét model-alias en provenance ─────────────

    [Fact]
    public async Task K1_GebruiktHetLosseEndpoint_MetAlias_EnSchrijftProvenance()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var requests = new List<(string Path, string Body)>();
        var svc = Service(db, requests, batchK: 1, alias: "opus", _ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                interactions = new[]
                {
                    new { from = "mechanic:Deflect", to = "mechanic:Assault",
                          kind = "COUNTERS", interacts = true },
                },
            }), Encoding.UTF8, "application/json"),
        });

        var r = await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 0);

        // K=1 = het losse endpoint (geen batch), met de alias in de payload.
        var (path, body) = Assert.Single(requests);
        Assert.Equal("/extract/interactions", path);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("opus", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, r.BatchSessions);
        Assert.Equal(1, r.BatchK);

        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("claude-opus-4-8", ix.ExtractModel);
        Assert.Equal(1, ix.ExtractBatchPosition);
        Assert.Equal("claude-opus-4-8", (await db.MiningRuns.SingleAsync()).LlmModel);
    }

    // ── Legacy-pad: zonder settings-service verandert er NIETS aan de payload ─

    [Fact]
    public async Task ZonderSettingsService_GeenModelVeld_EnLegacyProvenance()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var requests = new List<(string Path, string Body)>();
        var svc = new BreinInteractionMiningService(
            db, CapturingAi(requests, _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    interactions = new[]
                    {
                        new { from = "mechanic:Deflect", to = "mechanic:Assault",
                              kind = "COUNTERS", interacts = true },
                    },
                }), Encoding.UTF8, "application/json"),
            }),
            new EntityResolutionService(db), new InteractionPromotionService(db));

        var r = await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 0);

        var (path, body) = Assert.Single(requests);
        Assert.Equal("/extract/interactions", path);
        // Geen alias: het veld serialiseert als null — rb-ai leest dat als
        // "geen override" (cheap-default), het gedrag van vóór #323.
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("model").ValueKind);
        Assert.Null(r.ModelAlias);
        Assert.DoesNotContain("model", r.Summary);

        // Provenance blijft eerlijk: dit pad draait rb-ai's cheap-default.
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("claude-sonnet-4-6", ix.ExtractModel);
        Assert.Equal("claude-sonnet-4-6", (await db.MiningRuns.SingleAsync()).LlmModel);
    }

    // ── testinfra ─────────────────────────────────────────────────────────────

    /// <summary>Service met beheerde extract-instellingen (#323): alias + K uit
    /// een geseede <see cref="ManagedSettingsService"/> (geen DB — het
    /// dbFactory-loze pad), en een stub-rb-ai die elke request vastlegt.</summary>
    private static BreinInteractionMiningService Service(
        RbRulesDbContext db, List<(string, string)> requests, int batchK, string alias,
        Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(db, CapturingAi(requests, respond),
            new EntityResolutionService(db), new InteractionPromotionService(db),
            managedSettings: new ManagedSettingsService(
                extractBase: new BreinExtractSettings(alias, batchK, 90_000, 180_000)));

    private static RbAiClient CapturingAi(
        List<(string, string)> requests,
        Func<HttpRequestMessage, HttpResponseMessage> respond) => new(
        new HttpClient(new StubHandler(req =>
        {
            lock (requests)
                requests.Add((req.RequestUri!.AbsolutePath,
                    req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return respond(req);
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Een 200 met NDJSON-regels — het batch-streamcontract van rb-ai.</summary>
    private static HttpResponseMessage Ndjson(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            string.Join("\n", lines.Select(l => l.ReplaceLineEndings(" "))) + "\n",
            Encoding.UTF8, "application/x-ndjson"),
    };

    private static async Task<Card> Card(RbRulesDbContext db, string id) =>
        await db.Cards.SingleAsync(c => c.RiftboundId == id);

    private static async Task SeedCardAsync(
        RbRulesDbContext db, string id, string name, string type, string text,
        string[] mechanics)
    {
        db.Cards.Add(new Card
        {
            RiftboundId = id, Name = name, Type = type, TextPlain = text, Mechanics = mechanics,
        });
        await db.SaveChangesAsync();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

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
