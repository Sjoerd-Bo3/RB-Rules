using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Invalidatie van het antwoordgeheugen vanuit de wijzigingen-feed
/// (#384): een échte content-wijziging van een bron (het "changed"-pad van
/// ScanOneAsync) trekt elk bewaard antwoord in dat die bron citeert — in
/// dezelfde SaveChanges als de Change. Een ongewijzigde scan of een andere
/// bron raakt de rij niet.</summary>
public class IngestServiceAnswerMemoryTests
{
    private const string Url = "https://example.com/regels";

    [Fact]
    public async Task EchteWijziging_TrektBewaardeAntwoordenOpDieBronIn()
    {
        using var db = NewDb();
        db.Sources.Add(Src("s1", Url));
        db.Sources.Add(Src("s2", "https://example.com/andere"));
        await db.SaveChangesAsync();
        // s1 wijzigt tussen de scans, s2 blijft stabiel.
        var content = "Eerste versie van de regeltekst.";
        var svc = NewIngest(db, req => Html(
            req.RequestUri!.ToString() == Url ? content : "Stabiele tekst van de andere bron."));
        await svc.ScanAsync(onlyDue: false); // eerste fetch: "new"

        var citesS1 = Memory("Vraag A", ",s1=hash,");
        var citesS2 = Memory("Vraag B", ",s2=hash,");
        db.AnswerMemories.AddRange(citesS1, citesS2);
        await db.SaveChangesAsync();

        content = "Tweede, inhoudelijk andere versie met een compleet nieuwe zin.";
        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("changed", results.Single(r => r.SourceId == "s1").Status);
        Assert.Equal("unchanged", results.Single(r => r.SourceId == "s2").Status);
        var a = await db.AnswerMemories.SingleAsync(m => m.Id == citesS1.Id);
        Assert.Equal("retracted", a.Trust);
        Assert.Equal("bron gewijzigd: Bron s1", a.RetractedReason);
        Assert.NotNull(a.RetractedAt);
        // De rij die alleen de stabiele bron citeert blijft dienbaar.
        Assert.Equal("confirmed", (await db.AnswerMemories.FindAsync(citesS2.Id))!.Trust);
    }

    [Fact]
    public async Task OngewijzigdeScan_OfAndereBron_LaatDeRijStaan()
    {
        using var db = NewDb();
        db.Sources.Add(Src("s1", Url));
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Stabiele regeltekst die niet wijzigt."));
        await svc.ScanAsync(onlyDue: false);

        var citesS1 = Memory("Vraag A", ",s1=hash,");
        var citesOther = Memory("Vraag B", ",s9=hash,");
        db.AnswerMemories.AddRange(citesS1, citesOther);
        await db.SaveChangesAsync();

        await svc.ScanAsync(onlyDue: false); // zelfde hash: "unchanged"

        Assert.Equal("confirmed", (await db.AnswerMemories.FindAsync(citesS1.Id))!.Trust);
        Assert.Equal("confirmed", (await db.AnswerMemories.FindAsync(citesOther.Id))!.Trust);
    }

    // --- testinfra (zelfde patroon als IngestServiceUpdatedAtTests) ---

    private static Source Src(string id, string url) => new()
    {
        Id = id, Name = $"Bron {id}", Url = url,
        Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
    };

    private static AnswerMemory Memory(string question, string snapshot) => new()
    {
        Question = question, QuestionNormalized = question, Answer = "antwoord",
        CitationsJson = "[]", SourceSnapshot = snapshot, Trust = "confirmed",
    };

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
