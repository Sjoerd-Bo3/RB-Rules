using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Temporele precedentie (#168): Source.UpdatedAt weerspiegelt het
/// detectiemoment van een échte content-wijziging (het "changed"-pad van
/// ScanOneAsync) — niet elke LastChecked, en niet de eerste ontdekking van een
/// nieuwe bron. Zelfde detectiemoment-aanname als Change.DetectedAt elders in
/// de scan-pipeline (de officiële pagina publiceert zelf geen update-datum).</summary>
public class IngestServiceUpdatedAtTests
{
    private const string Url = "https://example.com/regels";

    [Fact]
    public async Task ScanAsync_FirstSuccessfulFetch_UpdatedAtStaysNull()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Eerste versie van de regeltekst."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var src = await db.Sources.SingleAsync();
        Assert.Null(src.UpdatedAt); // net ontdekt — nog geen wijziging gezien
    }

    [Fact]
    public async Task ScanAsync_RealContentChange_SetsUpdatedAt()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var content = "Eerste versie van de regeltekst.";
        var svc = NewIngest(db, _ => Html(content));
        await svc.ScanAsync(onlyDue: false); // eerste fetch: "new", geen change-item

        content = "Tweede, inhoudelijk andere versie met een compleet nieuwe zin.";
        var results = await svc.ScanAsync(onlyDue: false); // echte wijziging

        Assert.Equal("changed", Assert.Single(results).Status);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt);
        Assert.Single(await db.Changes.ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_UnchangedContent_UpdatedAtStaysNull()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Stabiele regeltekst die niet wijzigt."));
        await svc.ScanAsync(onlyDue: false);

        var results = await svc.ScanAsync(onlyDue: false); // zelfde hash, geen echte wijziging

        Assert.Equal("unchanged", Assert.Single(results).Status);
        var src = await db.Sources.SingleAsync();
        Assert.Null(src.UpdatedAt);
    }

    // --- testinfra (zelfde patroon als IngestSsrfGuardTests/UrlGuardTests) ---

    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<html><body><p>{text}</p></body></html>"),
    };

    private static IngestService NewIngest(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var ai = new RbAiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
        var embeddings = new EmbeddingService(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://ollama.test") });
        return new IngestService(
            db, new HttpClient(new StubHandler(respond)), ai,
            new ChangeClassificationService(db, ai),
            // Kennis-hertoets (#119): zonder changes in het venster (of bij
            // rb-ai-uitval) doet de scan-afronding niets extra's relevants hier.
            new KnowledgeRecheckService(db, new ClaimMiningService(db, ai, embeddings)),
            // Feed-crawl (#167): geen SourceFeeds in deze db ⇒ "geen feeds aan
            // de beurt" zonder een enkele HTTP-call.
            new FeedCrawlService(db, new HttpClient(new StubHandler(respond))));
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
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als FeedCrawlServiceTests).</summary>
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
