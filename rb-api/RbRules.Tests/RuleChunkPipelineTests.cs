using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#282-review — `RuleChunkPipeline` had nul testdekking, terwijl juist zijn
/// duurste faalmodus hier hangt: de oud-weg/nieuw-erin-swap vervangt de héle
/// regelindex van een bron. Embeddt die maar half (Ollama omgevallen) en je ruilt een
/// complete index in voor een gatenkaas — stiller en schadelijker dan geen index.
///
/// LET OP de reikwijdte: EF InMemory kent geen transacties en geen
/// <c>ExecuteDeleteAsync</c>, dus het GESLAAGDE swap-pad is hier niet te draaien. Dat
/// is geen gemis voor deze tests: de claim die bewaakt moet worden is juist dat er bij
/// uitval NIET geswapt wordt, en dat pad keert terug vóór de transactie.</summary>
public class RuleChunkPipelineTests
{
    [Fact]
    public async Task Index_OllamaValtOm_LaatDeBestaandeIndexIntact()
    {
        await using var db = NewDb();
        db.Sources.Add(Src("core"));
        db.Documents.Add(Doc("core", 1));
        // Een complete, bestaande index — precies wat een halve swap zou slopen.
        db.RuleChunks.AddRange(
            Chunk("core", 0, "oude sectie een"),
            Chunk("core", 1, "oude sectie twee"));
        await db.SaveChangesAsync();

        var results = await Pipeline(db,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).RunAsync(force: true);

        var r = Assert.Single(results);
        Assert.True(r.Failed);
        Assert.Equal(0, r.Chunks);
        Assert.Contains("5xx", r.FailureSummary);
        // De oude index staat er nog, onaangeroerd.
        var chunks = await db.RuleChunks.OrderBy(c => c.ChunkIndex).ToListAsync();
        Assert.Equal(2, chunks.Count);
        Assert.Equal("oude sectie een", chunks[0].Text);
        Assert.All(chunks, c => Assert.Null(c.Embedding));
    }

    [Fact]
    public async Task Index_OllamaValtOm_MeldtHetInRunLog_NietAlleenInDeContainerlog()
    {
        await using var db = NewDb();
        db.Sources.Add(Src("core"));
        db.Documents.Add(Doc("core", 1));
        await db.SaveChangesAsync();

        await Pipeline(db, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .RunAsync(force: true);

        var log = Assert.Single(db.RunLogs.Where(l => l.Kind == "embed" && l.Ref == "rules"));
        Assert.Equal("error", log.Status);
        Assert.Contains("core", log.Detail);
        Assert.Contains("blijft staan", log.Detail);
    }

    [Fact]
    public async Task Index_EenGevallenBron_StoptDeRestVanDeRunNiet()
    {
        // Best-effort per bron: bron 1 valt om, bron 2 wordt gewoon nog geprobeerd.
        await using var db = NewDb();
        db.Sources.AddRange(Src("core"), Src("faq"));
        db.Documents.AddRange(Doc("core", 1), Doc("faq", 2));
        await db.SaveChangesAsync();

        var results = await Pipeline(db,
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)).RunAsync(force: true);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Failed));
        Assert.Contains(results, r => r.SourceId == "faq");
    }

    [Fact]
    public async Task Index_HalveBronEmbeddeerd_SwaptAlsnogNiet()
    {
        // De gemene variant: de eerste batch lukt, de tweede niet. Zonder de
        // alles-of-niets-poort zou hier een deels-geëmbedde index ingeswapt worden.
        await using var db = NewDb();
        db.Sources.Add(Src("core"));
        db.Documents.Add(Doc("core", 1, sections: 20));
        db.RuleChunks.Add(Chunk("core", 0, "oude index"));
        await db.SaveChangesAsync();

        var calls = 0;
        var results = await Pipeline(db, req => ++calls == 1
            ? OkEmbeddings(BatchTexts(req))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)).RunAsync(force: true);

        Assert.True(Assert.Single(results).Failed);
        var chunk = Assert.Single(await db.RuleChunks.ToListAsync());
        Assert.Equal("oude index", chunk.Text);
    }

    [Fact]
    public async Task Index_GenegeerdeBron_BlijftOngemoeid()
    {
        // #180-afspraak, meegenomen nu er dekking is: een genegeerde bron levert per
        // beoordeling niets meer op — ook geen embed-verzoeken.
        await using var db = NewDb();
        var ignored = Src("oud");
        ignored.IgnoredAt = DateTimeOffset.UtcNow;
        db.Sources.Add(ignored);
        db.Documents.Add(Doc("oud", 1));
        await db.SaveChangesAsync();

        var calls = 0;
        var results = await Pipeline(db, req => { calls++; return OkEmbeddings(BatchTexts(req)); })
            .RunAsync(force: true);

        Assert.Empty(results);
        Assert.Equal(0, calls);
        Assert.Empty(db.RunLogs);
    }

    // ── Samenvatting voor de aanroepers ──────────────────────────────────────

    [Fact]
    public void Summarize_TeltGefaaldeBronnenNietAlsGeindexeerd()
    {
        // #282-review: de job meldde "0 sectie-chunks over 6 bronnen (herbouwd)" met
        // status ok terwijl alle zes omgevallen waren — Count telde ze mee.
        var results = new List<RuleIndexResult>
        {
            new("core", 0, "5xx (model-runner omgevallen?)×1"),
            new("faq", 0, "onbereikbaar×1"),
        };

        var summary = results.Summarize(rebuilt: true);

        Assert.Contains("0 sectie-chunks over 0 bronnen", summary);
        Assert.Contains("2 bron(nen) overgeslagen", summary);
        Assert.Contains("core", summary);
    }

    [Fact]
    public void Summarize_ZonderUitval_NoemtGeenOvergeslagenBronnen()
    {
        var results = new List<RuleIndexResult> { new("core", 42), new("faq", 8) };

        var summary = results.Summarize();

        Assert.Equal("50 sectie-chunks over 2 bronnen", summary);
    }

    // ── testinfra ────────────────────────────────────────────────────────────

    private static Source Src(string id) => new()
    {
        Id = id, Name = id, Url = $"https://playriftbound.com/{id}", Type = "official",
        TrustTier = 1, Rank = 100, Parser = "html", Cadence = "weekly", Enabled = true,
    };

    /// <summary>Document met <paramref name="sections"/> genummerde secties, zodat
    /// RuleSectionParser er echte chunks van maakt.</summary>
    private static Document Doc(string sourceId, long id, int sections = 3) => new()
    {
        Id = id, SourceId = sourceId, ContentHash = $"h{id}",
        Content = string.Join("\n", Enumerable.Range(1, sections)
            .Select(i => $"{100 + i}. Sectie {i} met genoeg tekst om bewaard te blijven.")),
    };

    private static RuleChunk Chunk(string sourceId, int index, string text) => new()
    {
        DocumentId = 99, SourceId = sourceId, ChunkIndex = index, Text = text,
    };

    private static RuleChunkPipeline Pipeline(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(db,
            new EmbeddingService(new HttpClient(new StubHandler(respond))
            { BaseAddress = new Uri("http://ollama.test") }),
            EmbeddingSettings.Default);

    private static int BatchTexts(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("input").GetArrayLength();
    }

    private static HttpResponseMessage OkEmbeddings(int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"embeddings":[{{string.Join(",",
                Enumerable.Repeat(
                    $"[{string.Join(",", Enumerable.Repeat("0.1", EmbeddingConfig.Dimensions))}]",
                    count))}}]}""",
            Encoding.UTF8, "application/json"),
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
