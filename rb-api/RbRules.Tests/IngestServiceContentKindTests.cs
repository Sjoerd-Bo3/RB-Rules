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

/// <summary>#188 increment 2: bron-type-classificatie (FAQ/patch-notes/other)
/// als LLM-BESLISSING i.p.v. de oude keyword-heuristiek, gezet bij de scan
/// van een trust-1-bron (IngestService.ClassifyContentKindAsync) en
/// gepersisteerd op Source.ContentKind/ContentKindSource. Degradeert bij
/// AI-uitval/onbruikbaar antwoord naar SourceContentKind.HeuristicKind —
/// nooit een 500. Zelfde testinfra als IngestServiceClarificationChangeTests,
/// maar met een stuurbare RbAiClient-stub (net als
/// ClarificationMiningServiceTests.Ai) i.p.v. een vaste 500-stub.</summary>
public class IngestServiceContentKindTests
{
    private const string FaqUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    [Fact]
    public async Task ScanAsync_AiUitval_ZetHeuristischeKindEnLogt()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "faq-source", Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => null));

        await svc.ScanAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.Faq, src.ContentKind); // "faq" zit in de URL
        Assert.Equal(SourceContentKind.HeuristicOrigin, src.ContentKindSource);
        var log = await db.RunLogs.SingleAsync(l => l.Kind == "content-kind");
        Assert.Equal("faq-source", log.Ref);
        Assert.Contains("heuristische", log.Detail);
    }

    [Fact]
    public async Task ScanAsync_LlmClassificeert_ZetLlmKind_GeenHeuristiekLog()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            // Bewust GEEN "faq"/"patch-notes"-woord in id/url/naam — dit is
            // precies het scenario dat de oude keyword-heuristiek miste en
            // waarvoor #188 increment 2 bestaat: de LLM herkent het bron-type
            // zonder magisch woord in de slug.
            Id = "legion-explainer", Name = "Legion uitgelegd",
            Url = "https://playriftbound.com/en-us/news/legion-explainer/",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => """{"kind": "faq"}"""));

        await svc.ScanAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.Faq, src.ContentKind);
        Assert.Equal(SourceContentKind.LlmOrigin, src.ContentKindSource);
        Assert.Empty(await db.RunLogs.Where(l => l.Kind == "content-kind").ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_AlLlmGeklassificeerd_WordtNietOpnieuwGeclassificeerd()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "faq-source", Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.Faq, ContentKindSource = SourceContentKind.LlmOrigin,
        });
        await db.SaveChangesAsync();
        // Zou de classificatie toch (opnieuw) draaien, dan zou dit antwoord
        // de kind naar "other" veranderen — de guard in ScanOneAsync moet dat
        // voorkomen zodra ContentKindSource al "llm" is.
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => """{"kind": "other"}"""));

        await svc.ScanAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.Faq, src.ContentKind);
        Assert.Equal(SourceContentKind.LlmOrigin, src.ContentKindSource);
    }

    [Fact]
    public async Task ScanAsync_HeuristischeKind_WordtBijVolgendeRunGeupgraded()
    {
        // Upgrade-pad (#188-eis): een eerdere run degradeerde naar de
        // heuristiek (AI was toen weg); een latere run met werkende AI mag
        // dat alsnog naar een echt LLM-oordeel optillen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "faq-source", Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.Other, ContentKindSource = SourceContentKind.HeuristicOrigin,
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => """{"kind": "faq"}"""));

        await svc.ScanAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.Faq, src.ContentKind);
        Assert.Equal(SourceContentKind.LlmOrigin, src.ContentKindSource);
    }

    [Fact]
    public async Task ScanAsync_CommunityBron_TrustTier3_WordtNietGeclassificeerd()
    {
        // Classificatie geldt alleen voor officiële (trust-1) bronnen —
        // zelfde trust-gate als de rest van de clarify-mining-pijplijn
        // (#166-autoriteitsmodel).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "community-mirror", Name = "Community FAQ mirror", Url = "https://example.com/faq",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Uitleg."), Ai(() => """{"kind": "faq"}"""));

        await svc.ScanAsync(onlyDue: false);

        var src = await db.Sources.SingleAsync();
        Assert.Null(src.ContentKind);
        Assert.Null(src.ContentKindSource);
    }

    [Fact]
    public async Task ScanAsync_ArrayVormigLlmAntwoord_DegradeertNaarHeuristiekZonderCrash()
    {
        // Review-regressie (#188 increment 1): een array-kandidaat
        // ("[true]") mag de scan nooit met een 500 laten stranden — de
        // objectvorm-guard in SourceContentKind.Parse degradeert naar de
        // heuristiek.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "faq-source", Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => "[true]"));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status); // geen error/crash
        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.Faq, src.ContentKind);
        Assert.Equal(SourceContentKind.HeuristicOrigin, src.ContentKindSource);
    }

    [Fact]
    public async Task ScanAsync_GemengdeBron_LlmZegtPatchNotes_GeenTemplatedChangeOpEersteScan()
    {
        // Het gemengde-bron-scenario (#185-review, "Rules FAQ and Patch
        // Notes"): de naam/URL zien er FAQ-achtig uit (de oude heuristiek zou
        // "faq" zeggen), maar de LLM oordeelt "patch-notes" — dan hoort de
        // #177-sjabloon-Change NIET te vuren op de eerste scan (die is
        // voorbehouden aan echte FAQ-bronnen).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "rules-faq-and-patch-notes", Name = "Rules FAQ and Patch Notes",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/rules-faq-and-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion is a dependent keyword."), Ai(() => """{"kind": "patch-notes"}"""));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        Assert.Empty(await db.Changes.ToListAsync());
        var src = await db.Sources.SingleAsync();
        Assert.Equal(SourceContentKind.PatchNotes, src.ContentKind);
        Assert.Equal(SourceContentKind.LlmOrigin, src.ContentKindSource);
    }

    [Fact]
    public async Task ScanAsync_LlmHerkentFaqZonderTrefwoordInSlug_MaaktTochTemplatedChange()
    {
        // De kern van #188 increment 2: een bron zonder "faq"/"clarification"
        // in id/url/naam (de heuristiek zou hier "other" zeggen) wordt door
        // de LLM als "faq" herkend — de #177-sjabloon-Change moet dan alsnog
        // vuren op de eerste scan.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "legion-explainer", Name = "Legion uitgelegd",
            Url = "https://playriftbound.com/en-us/news/legion-explainer/",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen."), Ai(() => """{"kind": "faq"}"""));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var change = await db.Changes.SingleAsync();
        Assert.Equal("clarification", change.ChangeType);
    }

    // --- testinfra (zelfde patroon als IngestServiceClarificationChangeTests) --

    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<html><body><p>{text}</p></body></html>"),
    };

    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static IngestService NewIngest(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond, RbAiClient ai)
    {
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
