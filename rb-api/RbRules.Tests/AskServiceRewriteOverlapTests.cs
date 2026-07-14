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

/// <summary>Servicetests voor de rewrite-overlap (#152): de query-rewrite
/// start parallel met de rewrite-onafhankelijke kanalen, maar de úitkomst
/// blijft die van de oude seriële pipeline — rewrite-uitval degradeert naar
/// de rauwe tekst (het #66-pad), en de FTS draait opnieuw op de zoekzin
/// zodra de rewrite een wezenlijk andere tekst oplevert (de bronnen zijn
/// Engels; alleen die her-run telt in de fusie). Zelfde testopzet als
/// AskServiceDegradationTests (EF InMemory, echte RbAiClient op een gestubde
/// handler, FTS vervangen — hier mét registratie van de zoekteksten);
/// zelfde xUnit-collectie vanwege de proces-brede ASK_AGENTIC-env.</summary>
[Collection("ask-service-env")]
public class AskServiceRewriteOverlapTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";
    private const string NormalizedText = "how does deflect work during a showdown";

    [Fact]
    public async Task AskAsync_RewriteFaalt_DegradeertNaarRuweTekst_ZelfdeUitkomst()
    {
        using var db = NewDb();
        var (rawChunk, _) = await SeedRulesAsync(db);
        // Rewrite-call (prompt "Vraag: …") faalt hard; de antwoordcall werkt.
        var svc = Svc(db, RoutedAi(rewriteAnswer: null));
        svc.FtsResults[Question] = [(rawChunk, SourceId)];

        var result = await svc.AskAsync(Question);

        // Zelfde uitkomst als vóór de overlap: antwoord op basis van de
        // ruwe-tekst-FTS, geen exception, rewrite geboekt als mislukt.
        Assert.True(result.Ok);
        Assert.Equal(Answer, result.Answer);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("101", citation.Section);
        Assert.Equal([Question], svc.FtsCalls);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Null(trace.RewrittenQuery);
    }

    [Fact]
    public async Task AskAsync_RewriteLevertAndereZoektekst_FtsHerRunTeltAlleen()
    {
        using var db = NewDb();
        var (rawChunk, rewriteChunk) = await SeedRulesAsync(db);
        var svc = Svc(db, RoutedAi(rewriteAnswer:
            $$"""{"normalized":"{{NormalizedText}}","queries":[],"terms":[]}"""));
        // De ruwe tekst en de zoekzin raken bewust verschillende chunks: zo
        // is bewijsbaar dat alléén de her-run op de zoekzin in de fusie telt
        // (byte-voor-byte hetzelfde resultaat als de oude seriële pipeline).
        svc.FtsResults[Question] = [(rawChunk, SourceId)];
        svc.FtsResults[NormalizedText] = [(rewriteChunk, SourceId)];

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("202", citation.Section);
        // Beide runs zijn gedaan: de ruwe alvast onder de rewrite, de
        // her-run op de zoekzin daarna.
        Assert.Equal([Question, NormalizedText], svc.FtsCalls);
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains(NormalizedText, trace.RewrittenQuery);
    }

    [Fact]
    public async Task AskAsync_RewriteZelfdeZoektekst_GeenTweedeFtsRun()
    {
        using var db = NewDb();
        var (rawChunk, _) = await SeedRulesAsync(db);
        // De rewrite levert letterlijk de ruwe vraag op: de vroege run is
        // dan al de juiste — geen dubbele query.
        var svc = Svc(db, RoutedAi(rewriteAnswer:
            $$"""{"normalized":"{{Question}}","queries":[],"terms":[]}"""));
        svc.FtsResults[Question] = [(rawChunk, SourceId)];

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("101", citation.Section);
        Assert.Equal([Question], svc.FtsCalls);
    }

    // --- testinfra (patroon AskServiceDegradationTests) -------------------

    /// <summary>FTS-seam met registratie: welke zoekteksten kreeg het kanaal,
    /// en per zoektekst een vast antwoord — zo zijn de vroege ruwe run en de
    /// her-run op de rewrite-zoekzin exact te onderscheiden.</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance)
    {
        public List<string> FtsCalls { get; } = [];
        public Dictionary<string, List<(long Id, string SourceId)>> FtsResults { get; } = [];

        protected override Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            FtsCalls.Add(searchText);
            return Task.FromResult(
                FtsResults.TryGetValue(searchText, out var hits) ? hits : []);
        }
    }

    private static TestableAskService Svc(RbRulesDbContext db, RbAiClient ai) =>
        new(db, FailingEmbeddings(), ai);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    /// <summary>Echte RbAiClient die op de prompt-inhoud routeert: de
    /// rewrite-call (prompt "Vraag: …", zonder contextblokken) krijgt
    /// <paramref name="rewriteAnswer"/> (of een 500 bij null), de
    /// antwoordcall (prompt mét "Context-fragmenten:") het vaste oordeel.
    /// Router op inhoud i.p.v. volgorde: met de overlap is de volgorde van
    /// wegsturen een implementatiedetail.</summary>
    private static RbAiClient RoutedAi(string? rewriteAnswer) => new(
        new HttpClient(new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
            if (prompt.Contains("Context-fragmenten:"))
                return Json(new { answer = Answer });
            return rewriteAnswer is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(new { answer = rewriteAnswer });
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Failing Ollama (#100-patroon): beide embed-batches (ruwe
    /// tekst én extra queries) vallen uit — de overlap moet dan exact het
    /// bestaande vector-degradatiepad geven.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError))))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AskService-tests).</summary>
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

    /// <summary>Twee chunks: één die de ruwe-tekst-FTS raakt (§101) en één
    /// voor de her-run op de rewrite-zoekzin (§202).</summary>
    private static async Task<(long RawChunk, long RewriteChunk)> SeedRulesAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var raw = new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells.",
        };
        var rewritten = new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "202",
            ChunkIndex = 1, Page = 20,
            Text = "During a showdown, players may respond in initiative order.",
        };
        db.RuleChunks.AddRange(raw, rewritten);
        await db.SaveChangesAsync();
        return (raw.Id, rewritten.Id);
    }
}
