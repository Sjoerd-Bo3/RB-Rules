using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Negeren met reden (#180): een genegeerde bron (IgnoredAt gezet)
/// slaat de geplande scan-lus over — net als Enabled=false, maar een apart,
/// bewust signaal ("dit levert niets op") in plaats van "tijdelijk uit". Een
/// gerichte handmatige rescan via sourceId bypasst dit filter net zoals hij
/// Enabled al bypaste (IngestServiceUpdatedAtTests-patroon).</summary>
public class IngestServiceIgnoreTests
{
    private const string Url = "https://example.com/regels";

    [Fact]
    public async Task ScanAsync_GenegeerdeBron_WordtOvergeslagen()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
            IgnoredAt = DateTimeOffset.UtcNow, IgnoreReason = "levert niets op",
        });
        await db.SaveChangesAsync();
        var called = false;
        var svc = NewIngest(db, _ => { called = true; return Html("tekst"); });

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Empty(results);
        Assert.False(called, "een genegeerde bron mag geen HTTP-fetch triggeren (geen LLM-kosten)");
    }

    [Fact]
    public async Task ScanAsync_NietGenegeerdeBronNaastGenegeerde_WordtWelGescand()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "genegeerd", Name = "Genegeerd", Url = "https://example.com/merch",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
            IgnoredAt = DateTimeOffset.UtcNow,
        });
        db.Sources.Add(new Source
        {
            Id = "actief", Name = "Actief", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("tekst"));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("actief", Assert.Single(results).SourceId);
    }

    [Fact]
    public async Task ScanAsync_GerichteRescanMetSourceId_NegeertHetIgnoreFilter()
    {
        // Zelfde bypass-gedrag als Enabled=false vandaag al heeft: een
        // beheerder mag een genegeerde bron gericht opnieuw bekijken via de
        // dossier-"Opnieuw scannen"-knop (sourceId-parameter).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = Url,
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
            IgnoredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("tekst"));

        var results = await svc.ScanAsync(onlyDue: false, sourceId: "s1");

        Assert.Equal("new", Assert.Single(results).Status);
    }

    // --- testinfra (IngestServiceUpdatedAtTests-patroon) ------------------

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
            new KnowledgeRecheckService(db, new ClaimMiningService(db, ai, embeddings)),
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
