using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Kosten-grootboek (#328): schaduwkost-som, tariefstempel op de rij,
/// user-attributie op het ask-pad en de paneel-aggregatie. De geld-asserts
/// staan met UITGESCHREVEN literals — nooit tegen de productie-seed
/// (AiTariffSeed) aan, anders schuift de test mee met een prijswijziging en
/// bewijst hij niets (#286/#293-les).</summary>
[Collection("ask-service-env")]
public class AiUsageMeteringTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";

    // ── Pure schaduwkost-som ────────────────────────────────────────────

    [Fact]
    public void ComputeUsd_TokensMaalTarief_MetLiterals()
    {
        // 2.000.000 in × $3/MTok + 400.000 uit × $15/MTok = 6 + 6 = $12.
        Assert.Equal(12m, ShadowCost.ComputeUsd(2_000_000, 400_000, 3m, 15m));
        // Kleine aantallen blijven exact (decimal, geen float-drift):
        // 1.234 × $5/MTok = $0,00617.
        Assert.Equal(0.00617m, ShadowCost.ComputeUsd(1_234, 0, 5m, 25m));
    }

    [Fact]
    public void ComputeUsd_OnbekendBlijftOnbekend()
    {
        // Geen tarief → geen bedrag (nooit een verzonnen nul).
        Assert.Null(ShadowCost.ComputeUsd(1_000, 1_000, null, null));
        // Geen enkele token-meting → geen bedrag.
        Assert.Null(ShadowCost.ComputeUsd(null, null, 3m, 15m));
        // Halve meting telt wél: alleen input bekend.
        Assert.Equal(3m, ShadowCost.ComputeUsd(1_000_000, null, 3m, 15m));
    }

    // ── Tariefstempel op de rij ─────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_StempeltDeGeldendeTariefversie()
    {
        using var db = NewDb();
        var oud = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 2m, OutputUsdPerMTok = 10m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-30),
        };
        var actueel = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var toekomst = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 99m, OutputUsdPerMTok = 99m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.AiTariffs.AddRange(oud, actueel, toekomst);
        await db.SaveChangesAsync();

        var evt = await AiUsageMeter.CreateEventAsync(
            db, AiUsageEvent.OriginUser, "ask", "claude-sonnet-4-6",
            userId: 7, inputTokens: 10, outputTokens: 2, durationMs: 100, ok: true);

        // Mutatie-bewijs: wordt de stempel niet geschreven (of pakt hij de
        // verkeerde rij), dan faalt precies deze assert.
        Assert.Equal(actueel.Id, evt.TariffVersion);
    }

    [Fact]
    public async Task CreateEvent_OnbekendModel_GeenTariefversie()
    {
        using var db = NewDb();
        db.AiTariffs.Add(new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var evt = await AiUsageMeter.CreateEventAsync(
            db, AiUsageEvent.OriginPlatform, "mining", "onbekend-model",
            userId: null, inputTokens: 5, outputTokens: 5, durationMs: 10, ok: true);

        Assert.Null(evt.TariffVersion); // geen gok, het paneel toont "geen tarief"
    }

    // ── Pad → model-spiegel van rb-ai's MODEL-map ───────────────────────

    [Fact]
    public void AskPathModels_SpiegeltDeRbAiMap_MetLiterals()
    {
        // UITGESCHREVEN literals, geen import van de map in de assert: dit is
        // de drift-wacht op de spiegel van rb-ai/src/ai.ts (MODEL). Wijzigt
        // rb-ai zijn map, dan hoort deze test bewust rood te gaan bij het
        // meebewegen van AskPathModels — en betrapt hij elke stille wijziging.
        Assert.Equal("claude-sonnet-4-6", AskPathModels.Resolve("cheap"));
        Assert.Equal("claude-opus-4-8", AskPathModels.Resolve("hard"));
        Assert.Equal("claude-sonnet-4-6", AskPathModels.Resolve("agentic"));
        // Onbekend/legacy pad valt op het default-model terug, nooit een gok.
        Assert.Equal("claude-sonnet-4-6", AskPathModels.Resolve(null));
        Assert.Equal("claude-sonnet-4-6", AskPathModels.Resolve("iets-nieuws"));
    }

    // ── Resolve & explain: gebruikers-veroorzaakte LLM-paden boeken ─────

    [Fact]
    public async Task ResolveAsync_BoektUsageEventMetUserAttributie()
    {
        using var db = NewDb();
        var tariff = new AiTariff
        {
            Model = "claude-opus-4-8", InputUsdPerMTok = 5m, OutputUsdPerMTok = 25m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.AiTariffs.Add(tariff);
        var user = new AppUser { Email = "speler@example.com" };
        db.Users.Add(user);
        db.Cards.AddRange(
            new Card { RiftboundId = "ogn-1", Name = "Deflector", TextPlain = "Deflect." },
            new Card { RiftboundId = "ogn-2", Name = "Sniper", TextPlain = "Choose a unit." });
        await db.SaveChangesAsync();

        var svc = new InteractionService(
            db, SequenceAi(new { answer = "Stap 1: …", usage = new { inputTokens = 900, outputTokens = 120 } }),
            WorkingEmbeddings(), new RecordingDriver(),
            new RequestUserContext { User = user });

        var result = await svc.ResolveAsync(["ogn-1", "ogn-2"]);

        Assert.NotNull(result);
        var evt = await db.AiUsageEvents.SingleAsync();
        Assert.Equal(AiUsageEvent.OriginUser, evt.Origin);
        Assert.Equal("resolve", evt.Kind);
        // task "hard" → het opus-model uit de spiegel-map.
        Assert.Equal("claude-opus-4-8", evt.Model);
        Assert.Equal(user.Id, evt.UserId);
        Assert.Equal(900, evt.InputTokens);
        Assert.Equal(120, evt.OutputTokens);
        Assert.Equal(tariff.Id, evt.TariffVersion);
    }

    [Fact]
    public async Task ExplainAsync_BoektDeCacheVulling_EnDeCacheHitNiet()
    {
        using var db = NewDb();
        var user = new AppUser { Email = "speler@example.com" };
        db.Users.Add(user);
        db.Cards.AddRange(
            new Card { RiftboundId = "ogn-1", Name = "Deflector" },
            new Card { RiftboundId = "ogn-2", Name = "Sniper" });
        await db.SaveChangesAsync();

        var svc = new SimilarityExplainService(
            db, SequenceAi(new { answer = "Beide beschermen units.", usage = new { inputTokens = 300, outputTokens = 40 } }),
            new RequestUserContext { User = user });

        var first = await svc.ExplainAsync("ogn-1", "ogn-2");
        Assert.False(first.Cached);
        var evt = await db.AiUsageEvents.SingleAsync();
        Assert.Equal("explain", evt.Kind);
        Assert.Equal(AiUsageEvent.OriginUser, evt.Origin);
        Assert.Equal("claude-sonnet-4-6", evt.Model); // task cheap
        Assert.Equal(user.Id, evt.UserId);
        Assert.Equal(300, evt.InputTokens);

        // Cache-hit is geen LLM-call en boekt dus níets extra.
        var second = await svc.ExplainAsync("ogn-1", "ogn-2");
        Assert.True(second.Cached);
        Assert.Equal(1, await db.AiUsageEvents.CountAsync());
    }

    // ── Ask-pad: user-attributie + model + stempel ──────────────────────

    [Fact]
    public async Task AskAsync_BoektUsageEventMetUserIdModelEnTariefversie()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var tariff = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.AiTariffs.Add(tariff);
        var user = new AppUser { Email = "speler@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var svc = Svc(db, SequenceAi(
            new { answer = Answer, usage = new { inputTokens = 100, outputTokens = 10 } },
            new { answer = Answer, usage = new { inputTokens = 2_000, outputTokens = 300 } }),
            new RequestUserContext { User = user });

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var evt = await db.AiUsageEvents.SingleAsync();
        // Mutatie-bewijs "user-id niet doorgeven" ⇒ rood:
        Assert.Equal(user.Id, evt.UserId);
        Assert.Equal(AiUsageEvent.OriginUser, evt.Origin);
        Assert.Equal("ask", evt.Kind);
        // Cheap-pad → het model-ID uit de spiegel-map, niet de padnaam.
        Assert.Equal("claude-sonnet-4-6", evt.Model);
        // Zelfde som als de metric (rewrite + antwoord).
        Assert.Equal(2_100, evt.InputTokens);
        Assert.Equal(310, evt.OutputTokens);
        // Mutatie-bewijs "tariefversie niet schrijven" ⇒ rood:
        Assert.Equal(tariff.Id, evt.TariffVersion);
        Assert.True(evt.Ok);
    }

    [Fact]
    public async Task AskAsync_ZonderUsage_EventTokensBlijvenNull()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        var svc = Svc(db, SequenceAi(new { answer = Answer }), new RequestUserContext());

        await svc.AskAsync(Question);

        var evt = await db.AiUsageEvents.SingleAsync();
        Assert.Null(evt.InputTokens);   // onbekend ≠ 0
        Assert.Null(evt.OutputTokens);
        Assert.Null(evt.UserId);        // anonieme (historische) vraagvorm
    }

    // ── Paneel-aggregatie ───────────────────────────────────────────────

    [Fact]
    public async Task Overview_TeltPerHerkomstEnRekentSchaduwbedragUitRijMaalTarief()
    {
        using var db = NewDb();
        var sonnet = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-10),
        };
        var fable = new AiTariff
        {
            Model = "claude-fable-5", InputUsdPerMTok = 10m, OutputUsdPerMTok = 50m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-10),
        };
        db.AiTariffs.AddRange(sonnet, fable);
        db.Users.Add(new AppUser { Id = 7, Email = "speler@example.com" });
        await db.SaveChangesAsync();
        db.AiUsageEvents.AddRange(
            // Gebruiker: 1M in + 100k uit op sonnet = $3 + $1,50 = $4,50.
            new AiUsageEvent
            {
                Origin = "user", Kind = "ask", Model = "claude-sonnet-4-6", UserId = 7,
                InputTokens = 1_000_000, OutputTokens = 100_000,
                DurationMs = 1000, TariffVersion = sonnet.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            // Platform-mining: 2M in + 200k uit op fable = $20 + $10 = $30.
            new AiUsageEvent
            {
                Origin = "platform", Kind = "mining", Model = "claude-fable-5",
                InputTokens = 2_000_000, OutputTokens = 200_000,
                DurationMs = 5000, TariffVersion = fable.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            },
            // Platform-audit zonder token-meting: telt als call, nooit als $0.
            new AiUsageEvent
            {
                Origin = "platform", Kind = "audit", Model = "claude-opus-4-8",
                InputTokens = null, OutputTokens = null,
                DurationMs = 700, TariffVersion = null,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            });
        await db.SaveChangesAsync();

        var overview = await new AiUsageReportService(db).OverviewAsync("7d");

        Assert.Equal(4.5m, overview.UserCaused.Usd);
        Assert.Equal(0, overview.UserCaused.UnpricedCalls);
        Assert.Equal(30m, overview.PlatformCaused.Usd);
        Assert.Equal(1, overview.PlatformCaused.UnpricedCalls); // de audit-run
        Assert.Equal(34.5m, overview.Days7.Usd);

        var user = Assert.Single(overview.TopUsers);
        Assert.Equal("speler@example.com", user.Email);
        Assert.Equal(4.5m, user.Usd);

        Assert.Contains(overview.PlatformPerKind, r => r.Kind == "mining" && r.Usd == 30m);
        Assert.Contains(overview.PlatformPerKind,
            r => r.Kind == "audit" && r.Usd == 0m && r.UnpricedCalls == 1);
        // Embeddings zijn lokaal: expliciet benoemd, geen stiekeme nul.
        Assert.Contains("lokaal", overview.EmbeddingsNote);
        // Het paneel zegt ZELF dat het een ondergrens is (review #328) en
        // welke paden boeken — content-literals, niet de constante zelf.
        Assert.Contains("ondergrens", overview.MeteredNote);
        Assert.Contains("resolve", overview.MeteredNote);
        Assert.Contains("explain", overview.MeteredNote);
    }

    [Fact]
    public async Task Overview_HistorischBedragVolgtDeGestempeldeVersie_NietHetNieuwsteTarief()
    {
        using var db = NewDb();
        var oud = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-20),
        };
        var nieuw = new AiTariff
        {
            Model = "claude-sonnet-4-6", InputUsdPerMTok = 6m, OutputUsdPerMTok = 30m,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.AiTariffs.AddRange(oud, nieuw);
        await db.SaveChangesAsync();
        // Rij geboekt vóór de prijswijziging draagt de oude versie: 1M in op
        // $3/MTok = $3 — en dat MOET zo blijven na de prijswijziging.
        db.AiUsageEvents.Add(new AiUsageEvent
        {
            Origin = "user", Kind = "ask", Model = "claude-sonnet-4-6", UserId = 1,
            InputTokens = 1_000_000, OutputTokens = 0,
            DurationMs = 100, TariffVersion = oud.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();

        var overview = await new AiUsageReportService(db).OverviewAsync("7d");

        Assert.Equal(3m, overview.Days7.Usd); // niet 6: de rij × zíjn tarief
    }

    // ── testinfra (patroon AskServiceUsageTests) ────────────────────────

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RequestUserContext userContext)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            userContext, NullLogger<AskService>.Instance)
    {
        private readonly RbRulesDbContext _db = db;

        protected override async Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            var words = searchText.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .ToList();
            var rows = await _db.RuleChunks.AsNoTracking()
                .Select(c => new { c.Id, c.SourceId, c.Text })
                .ToListAsync(ct);
            return [.. rows
                .Where(r => words.Any(w => r.Text.ToLowerInvariant().Contains(w)))
                .Select(r => (r.Id, r.SourceId))];
        }
    }

    private static TestableAskService Svc(
        RbRulesDbContext db, RbAiClient ai, RequestUserContext userContext) =>
        new(db, FailingEmbeddings(), ai, userContext);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static RbAiClient SequenceAi(params object[] payloads)
    {
        var call = 0;
        return new RbAiClient(
            new HttpClient(new StubHandler(_ =>
                JsonMessage(payloads[Math.Min(call++, payloads.Length - 1)])))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static HttpResponseMessage JsonMessage(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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

    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    /// <summary>Embed-stub die één nul-vector per tekst teruggeeft — het
    /// resolve-pad embedt de zoektekst vóór de LLM-call en mag daar niet op
    /// stranden (de vector-query zelf blijft leeg: geen chunks geseed).</summary>
    private static EmbeddingService WorkingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => JsonMessage(new
        {
            embeddings = new[] { Enumerable.Repeat(0f, 1024).ToArray() },
        })))
        { BaseAddress = new Uri("http://ollama.test") });

    private static async Task SeedRulesAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash",
            FileUrl = "https://example.com/core-rules.pdf",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
        });
        await db.SaveChangesAsync();
    }
}
