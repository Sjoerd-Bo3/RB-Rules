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
/// IngestServiceUpdatedAtTests).
///
/// #185: patch notes zijn UIT ClarificationSources.IsMatch gehaald, dus dit
/// sjabloon vuurt sindsdien niet meer op hun eerste scan — hun echte duiding
/// komt vanaf de tweede scan gewoon via de normale ingest-diff (voor/na),
/// zie ScanAsync_EersteScanVanPatchNotesBron_MaaktGeenClarificationChange en
/// ScanAsync_TweedeScanVanPatchNotesBron_MaaktGewoneDiffChange hieronder.</summary>
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
    public async Task ScanAsync_EersteScanVanPatchNotesBron_MaaktGeenClarificationChange()
    {
        // #185: patch notes matchen niet meer op ClarificationSources.IsMatch,
        // dus deze tak (het #177-sjabloon) vuurt niet meer op hun eerste
        // scan — een patch-notes-bron gedraagt zich nu als elke andere
        // gewone bron: "new" zonder Change (er is nog niets om te diffen).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-patch-notes", Name = "Core Rules Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion is a dependent keyword."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        Assert.Empty(await db.Changes.ToListAsync());
        var src = await db.Sources.SingleAsync();
        Assert.Null(src.UpdatedAt); // net ontdekt, geen wijziging gezien
    }

    [Fact]
    public async Task ScanAsync_TweedeScanVanPatchNotesBron_MaaktGewoneDiffChange()
    {
        // De echte duiding van een patch-notes-wijziging komt sinds #185
        // alleen nog via de normale ingest-diff (voor/na) — niet als losse
        // ruling. Deze test bewijst dat het pad wérkt: content-wijziging op
        // een patch-notes-bron levert een gewone Change op, exact zoals elke
        // andere bron (IngestServiceUpdatedAtTests.
        // ScanAsync_RealContentChange_SetsUpdatedAt).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-patch-notes", Name = "Core Rules Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var content = "Legion is a dependent keyword.";
        var svc = NewIngest(db, _ => Html(content));
        await svc.ScanAsync(onlyDue: false); // eerste fetch: "new", geen change-item

        content = "Legion is a dependent keyword. CLARIFIED: activated abilities with Legion trigger only once per turn.";
        var results = await svc.ScanAsync(onlyDue: false); // échte wijziging

        Assert.Equal("changed", Assert.Single(results).Status);
        var change = await db.Changes.SingleAsync();
        Assert.NotEqual("clarification", change.ChangeType); // geen #177-sjabloon meer
        Assert.NotNull(change.Diff);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt);
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
