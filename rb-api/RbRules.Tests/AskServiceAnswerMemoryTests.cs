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

/// <summary>Het antwoordgeheugen (#384) door de echte /ask-flow: een vers
/// antwoord wordt kandidaat, een bevestigde rij dient zonder één LLM-call,
/// hits promoveren, een gewijzigde bron trekt in, en doorvragen/uit-schakelaar
/// laten het geheugen ongemoeid. Embedding-stub: elke tekst krijgt een vaste
/// eenheidsvector op grond van een sleutelwoord, zodat "dezelfde vraag" en
/// "andere vraag" deterministisch zijn; de nearest-neighbour rekent de
/// cosinus in-memory uit (InMemory kent geen pgvector-operators).</summary>
public class AskServiceAnswerMemoryTests
{
    private const string SourceId = "riot-core-rules";
    private const string RewriteJson = """{"normalized":"Viktor legal","queries":[],"terms":[]}""";
    private const string FreshAnswer = "**Oordeel:** Vers antwoord uit het model. [1]";

    [Fact]
    public async Task VersAntwoord_WordtKandidaatMetCitatiesEnBronMomentopname()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        var r = await svc.AskAsync("Is Viktor legaal in constructed?");

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls); // rewrite + antwoord
        var row = await db.AnswerMemories.SingleAsync();
        Assert.Equal("candidate", row.Trust);
        Assert.Equal("Legaliteit", row.QuestionType);
        Assert.Equal(FreshAnswer, row.Answer);
        Assert.Equal("cheap", row.Model);
        Assert.Equal(AskService.PromptVersion, row.PromptVersion);
        Assert.NotNull(row.Embedding);
        Assert.Contains(",riot-core-rules=hash-v1,", row.SourceSnapshot);
        var cits = AnswerMemoryService.Citations(row);
        Assert.Single(cits);
        Assert.Equal("101", cits[0].Section);
        // Terugmelding: de rij waar feedback op slaat, niet gediend.
        Assert.NotNull(r.Memory);
        Assert.Equal(row.Id, r.Memory!.Id);
        Assert.False(r.Memory.Served);
    }

    [Fact]
    public async Task BevestigdeRij_DientZonderEnigeLlmCall()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var stored = await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]");
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));
        var deltas = new List<string>();
        AskStreamMeta? meta = null;

        var r = await svc.AskStreamingAsync("is viktor legaal in constructed", null, null,
            onMeta: m => { meta = m; return Task.CompletedTask; },
            onDelta: d => { deltas.Add(d); return Task.CompletedTask; });

        Assert.Equal(0, calls); // geen rewrite, geen antwoord-call
        Assert.Equal("**Oordeel:** Bewaard antwoord. [1]", r.Answer);
        Assert.Equal(["**Oordeel:** Bewaard antwoord. [1]"], deltas);
        Assert.NotNull(meta);
        Assert.Single(meta!.Citations);
        Assert.Equal("101", Assert.Single(r.Citations).Section);
        Assert.True(r.Memory!.Served);
        Assert.Equal(stored.Id, r.Memory.Id);
        Assert.Equal(1.0, r.Memory.Similarity!.Value, 3);

        var row = await db.AnswerMemories.SingleAsync();
        Assert.Equal(1, row.HitCount);
        Assert.NotNull(row.LastHitAt);
        var metric = await db.AskMetrics.SingleAsync();
        Assert.Equal("memory", metric.Model);
        Assert.True(metric.Ok);
        Assert.Empty(await db.AiUsageEvents.ToListAsync()); // geen kostenrij
        var trace = await db.AskTraces.SingleAsync();
        Assert.Equal("memory", trace.Model);
        Assert.Contains("geheugen", trace.BrainSteps);
        Assert.Contains("§101", trace.Sections);
    }

    [Fact]
    public async Task Kandidaat_WordtNaDrieHitsBevestigd_ZonderDubbeleRij()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var stored = await StoreAsync(db, "Is Viktor legaal in constructed?", "candidate",
            "**Oordeel:** Bewaard antwoord. [1]", hitCount: 2);
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        var r = await svc.AskAsync("Is Viktor legaal in constructed?");

        // Een kandidaat dient nog niet: vers antwoord, gewone twee calls.
        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        Assert.False(r.Memory!.Served);
        Assert.Equal(stored.Id, r.Memory.Id); // feedback slaat op de bestaande kandidaat
        var row = await db.AnswerMemories.SingleAsync(); // geen tweede rij
        Assert.Equal(3, row.HitCount);
        Assert.Equal("confirmed", row.Trust);
    }

    [Fact]
    public async Task GewijzigdeBron_TrektDeRijInEnDientNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]");
        // De bron is sindsdien opnieuw gescand met andere inhoud.
        (await db.Sources.SingleAsync(s => s.Id == SourceId)).LastHash = "hash-v2";
        await db.SaveChangesAsync();
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        var r = await svc.AskAsync("Is Viktor legaal in constructed?");

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        var rows = await db.AnswerMemories.OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("retracted", rows[0].Trust);
        Assert.Contains("bron gewijzigd", rows[0].RetractedReason);
        // Het verse antwoord is een nieuwe kandidaat met de nieuwe hash.
        Assert.Equal("candidate", rows[1].Trust);
        Assert.Contains("=hash-v2,", rows[1].SourceSnapshot);
        Assert.Equal(rows[1].Id, r.Memory!.Id);
    }

    [Fact]
    public async Task AnderVraagtype_DientNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        // Zelfde tekstvector, maar als "Ruling" bewaard — de nieuwe vraag routeert Legaliteit.
        await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]", questionType: "Ruling");
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        var r = await svc.AskAsync("Is Viktor legaal in constructed?");

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        Assert.False(r.Memory!.Served);
    }

    [Fact]
    public async Task VergelijkbareVraag_KomtAlsHintNaastHetVerseAntwoord()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var stored = await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]");
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        // "ongeveer" ⇒ de stub geeft een vector met cosinus 0,85 t.o.v. de Viktor-vector.
        var r = await svc.AskAsync("Is Viktor ongeveer legaal in constructed?");

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        Assert.False(r.Memory!.Served);
        Assert.NotNull(r.Memory.Similar);
        Assert.Equal(stored.Id, r.Memory.Similar!.Id);
        Assert.Equal(0.85, r.Memory.Similar.Similarity, 2);
        // En het verse antwoord is zelf een nieuwe kandidaat.
        Assert.Equal(2, await db.AnswerMemories.CountAsync());
    }

    [Fact]
    public async Task Doorvraag_RaaktHetGeheugenNiet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]");
        var calls = 0;
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer));

        var r = await svc.AskAsync("Is Viktor legaal in constructed?",
            history: [new AskTurn("Wat is Viktor?", "Een kaart.")]);

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        Assert.Null(r.Memory);
        Assert.Equal(1, await db.AnswerMemories.CountAsync()); // niets bijgeschreven
        Assert.Equal(0, (await db.AnswerMemories.SingleAsync()).HitCount);
    }

    [Fact]
    public async Task SchakelaarUit_LeestNochSchrijft()
    {
        using var db = NewDb();
        await SeedAsync(db);
        await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed",
            "**Oordeel:** Bewaard antwoord. [1]");
        var calls = 0;
        var settings = new ManagedSettingsService(
            seed: new Dictionary<string, string> { [SettingKeys.AskMemoryEnabled] = "false" });
        var svc = NewService(db, CountingAi(() => calls++, RewriteJson, FreshAnswer), settings);

        var r = await svc.AskAsync("Is Viktor legaal in constructed?");

        Assert.Equal(FreshAnswer, r.Answer);
        Assert.Equal(2, calls);
        Assert.Null(r.Memory);
        Assert.Equal(1, await db.AnswerMemories.CountAsync());
    }

    [Fact]
    public async Task Feedback_DuimOmhoogBevestigt_DuimOmlaagTrektIn()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var a = await StoreAsync(db, "Is Viktor legaal in constructed?", "candidate", "A");
        var b = await StoreAsync(db, "Mag ik Viktor recyclen?", "confirmed", "B");
        var memory = new TestableAnswerMemoryService(db);

        Assert.True(await memory.FeedbackAsync(a.Id, thumbsUp: true, CancellationToken.None));
        Assert.True(await memory.FeedbackAsync(b.Id, thumbsUp: false, CancellationToken.None));
        Assert.False(await memory.FeedbackAsync(9999, thumbsUp: true, CancellationToken.None));

        Assert.Equal("confirmed", (await db.AnswerMemories.FindAsync(a.Id))!.Trust);
        var down = (await db.AnswerMemories.FindAsync(b.Id))!;
        Assert.Equal("retracted", down.Trust);
        Assert.Contains("onjuist", down.RetractedReason);
        Assert.NotNull(down.RetractedAt);
    }

    [Fact]
    public async Task Beheer_VerifieertOfTrektInMetReden()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var a = await StoreAsync(db, "Is Viktor legaal in constructed?", "candidate", "A");
        var memory = new TestableAnswerMemoryService(db);

        Assert.True(await memory.ReviewAsync(a.Id, verify: true, null, CancellationToken.None));
        Assert.Equal("verified", (await db.AnswerMemories.FindAsync(a.Id))!.Trust);
        Assert.True(await memory.ReviewAsync(a.Id, verify: false, "te algemeen", CancellationToken.None));
        var row = (await db.AnswerMemories.FindAsync(a.Id))!;
        Assert.Equal("retracted", row.Trust);
        Assert.Equal("te algemeen", row.RetractedReason);
        var counts = await memory.CountsAsync(CancellationToken.None);
        Assert.Equal(new AnswerMemoryCounts(0, 0, 1), counts);
    }

    [Fact]
    public async Task RetractForSource_TrektAlleenRijenDieDeBronCiteren()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var citing = await StoreAsync(db, "Is Viktor legaal in constructed?", "confirmed", "A");
        var other = new AnswerMemory
        {
            Question = "q", QuestionNormalized = "q", Answer = "B", CitationsJson = "[]",
            SourceSnapshot = AnswerMemoryPolicy.EncodeSnapshot([("riot-core", "x")]),
            Trust = "confirmed",
        };
        db.AnswerMemories.Add(other);
        await db.SaveChangesAsync();

        var n = await AnswerMemoryService.RetractForSourceAsync(db, SourceId, "bron gewijzigd: test", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(1, n);
        Assert.Equal("retracted", (await db.AnswerMemories.FindAsync(citing.Id))!.Trust);
        Assert.Equal("bron gewijzigd: test", (await db.AnswerMemories.FindAsync(citing.Id))!.RetractedReason);
        // "riot-core" is een prefix van "riot-core-rules" — mag NIET meegetrokken worden.
        Assert.Equal("confirmed", (await db.AnswerMemories.FindAsync(other.Id))!.Trust);
    }

    // ── seeding ─────────────────────────────────────────────────────────────

    private static async Task SeedAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
            LastHash = "hash-v1",
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
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101", ChunkIndex = 0, Page = 12,
            Text = "Viktor is legal in Constructed unless the card is banned.",
        });
        db.CardSets.Add(new CardSet { SetId = "OGN", Name = "Origins", PublishedOn = new DateOnly(2025, 10, 1) });
        db.Cards.Add(new Card { RiftboundId = "ogn-001", Name = "Viktor", SetId = "OGN", SetLabel = "Origins" });
        await db.SaveChangesAsync();
    }

    /// <summary>Een bewaarde rij zoals RememberAsync die zou schrijven, met de
    /// stub-vector van de vraag en één citatie op §101.</summary>
    private static async Task<AnswerMemory> StoreAsync(
        RbRulesDbContext db, string question, string trust, string answer,
        int hitCount = 0, string questionType = "Legaliteit")
    {
        var row = new AnswerMemory
        {
            Question = question, QuestionNormalized = question, QuestionType = questionType,
            Embedding = StubVector(question), EmbeddingModel = EmbeddingConfig.Model,
            Answer = answer,
            CitationsJson = JsonSerializer.Serialize(new List<Citation>
            {
                new(1, "Core Rules", "https://example.com/core", "101", 1,
                    Text: "Viktor is legal in Constructed unless the card is banned."),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            SourceSnapshot = AnswerMemoryPolicy.EncodeSnapshot([(SourceId, "hash-v1")]),
            Trust = trust, HitCount = hitCount, Model = "cheap",
        };
        db.AnswerMemories.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    // ── stubs ───────────────────────────────────────────────────────────────

    /// <summary>Deterministische eenheidsvectoren: "viktor" ⇒ e0; "recycl" ⇒ e1;
    /// "ongeveer" ⇒ 0,85·e0 + 0,527·e1 (cosinus 0,85 met e0: de "vergelijkbaar"-
    /// band); anders e2.</summary>
    private static Vector StubVector(string text)
    {
        var v = new float[EmbeddingConfig.Dimensions];
        var t = text.ToLowerInvariant();
        if (t.Contains("ongeveer")) { v[0] = 0.85f; v[1] = MathF.Sqrt(1 - 0.85f * 0.85f); }
        else if (t.Contains("viktor")) v[0] = 1f;
        else if (t.Contains("recycl")) v[1] = 1f;
        else v[2] = 1f;
        return new Vector(v);
    }

    private static EmbeddingService StubEmbeddings() => new(
        new HttpClient(new StubHandler(req =>
        {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var inputs = body.RootElement.GetProperty("input").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToList();
            var embeddings = inputs.Select(i => StubVector(i).ToArray()).ToList();
            return JsonMessage(new { embeddings });
        }))
        { BaseAddress = new Uri("http://ollama.test") });

    private static double Cosine(Vector a, Vector b)
    {
        var x = a.ToArray();
        var y = b.ToArray();
        double dot = 0, nx = 0, ny = 0;
        for (var i = 0; i < x.Length; i++) { dot += x[i] * y[i]; nx += x[i] * x[i]; ny += y[i] * y[i]; }
        return nx == 0 || ny == 0 ? 0 : dot / Math.Sqrt(nx * ny);
    }

    /// <summary>InMemory kent geen CosineDistance: nearest-neighbour hier in C#.</summary>
    private sealed class TestableAnswerMemoryService(RbRulesDbContext db, ManagedSettingsService? settings = null)
        : AnswerMemoryService(db, NullLogger<AnswerMemoryService>.Instance, settings)
    {
        private readonly RbRulesDbContext _db = db;

        protected override async Task<IReadOnlyList<MemoryMatch>> NearestAsync(
            Vector questionVector, int k, CancellationToken ct)
        {
            var rows = await _db.AnswerMemories.Where(m => m.Embedding != null).ToListAsync(ct);
            return [.. rows
                .Select(r => new MemoryMatch(r, Cosine(r.Embedding!, questionVector)))
                .OrderByDescending(m => m.Similarity)
                .Take(k)];
        }
    }

    private static AskService NewService(
        RbRulesDbContext db, RbAiClient ai, ManagedSettingsService? settings = null) =>
        new TestableAskService(db, StubEmbeddings(), ai, new RequestUserContext(),
            new TestableAnswerMemoryService(db, settings));

    /// <summary>Zelfde FTS-woordmatch als de andere AskService-tests; de
    /// vector-kanalen op InMemory vallen uit (CosineDistance) en degraderen
    /// per ontwerp naar een leeg kanaal met marker.</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        RequestUserContext userContext, AnswerMemoryService memory)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            userContext, NullLogger<AskService>.Instance, memory: memory)
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

    private static RbAiClient CountingAi(Action onCall, string rewriteJson, string answer)
    {
        var call = 0;
        return new RbAiClient(
            new HttpClient(new StubHandler(_ =>
            {
                onCall();
                var payload = call++ == 0 ? new { answer = rewriteJson } : new { answer };
                return JsonMessage(payload);
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    private static HttpResponseMessage JsonMessage(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

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
