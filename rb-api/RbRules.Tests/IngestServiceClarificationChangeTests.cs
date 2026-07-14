using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#177: een FAQ-/clarificatie-artikel had bij zijn allereerste scan
/// (isNew — er is nog geen vorige versie om te diffen) geen Change-item, dus
/// de aankomst bleef onzichtbaar in de wijzigingen-feed (productie-bug: "0
/// changes" voor de Unleashed Rules FAQ). Deze tests dekken de sjabloon-
/// change die ScanOneAsync nu bij aankomst toevoegt, alleen voor officiële
/// (TrustTier 1) bronnen die matchen op ClarificationSources.IsMatch — een
/// gewone nieuwe bron blijft ongemoeid (bestaand gedrag,
/// IngestServiceUpdatedAtTests).</summary>
public class IngestServiceClarificationChangeTests
{
    private const string FaqUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    [Fact]
    public async Task ScanAsync_EersteScanVanFaqBron_MaaktClarificationChange()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "playriftbound-com-unleashed-rules-faq-and-clarifications",
            Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen op de chain."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var change = await db.Changes.SingleAsync();
        Assert.Equal("clarification", change.ChangeType);
        Assert.Contains("FAQ-/clarificatie-artikel", change.Summary);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt); // zichtbaar als "laatst bijgewerkt"
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanGewoneBron_MaaktGeenChange()
    {
        // Bestaand gedrag (IngestServiceUpdatedAtTests) blijft ongemoeid: een
        // bron zonder faq/clarification-signaal krijgt bij isNew nog steeds
        // geen Change — er is immers niets om te diffen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = "https://example.com/regels",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Eerste versie van de regeltekst."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        Assert.Empty(await db.Changes.ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanCommunityFaqMirror_MaaktGeenChange()
    {
        // Autoriteitsmodel #166: alleen TrustTier == 1 krijgt de automatische
        // sjabloon-change — een community-mirror (trust 3) niet, ook al matcht
        // de naam op "faq".
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "community-faq-mirror", Name = "Community FAQ mirror",
            Url = "https://example.com/faq", Type = "community", TrustTier = 3,
            Rank = 10, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Uitleg."));

        await svc.ScanAsync(onlyDue: false);

        Assert.Empty(await db.Changes.ToListAsync());
    }

    // --- testinfra (zelfde patroon als IngestServiceUpdatedAtTests) -------

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
