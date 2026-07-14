using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Regressietest voor het reuseRaw-pad (#152, review-opvolging): als
/// de rewrite slaagt, de zoekzin letterlijk gelijk is aan de ruwe
/// retrievaltekst (ordinal) én er extra SearchQueries zijn, hergebruikt de
/// pipeline de onder de rewrite al gemaakte ruwe-vraag-embedding als
/// queryVectors[0] i.p.v. die tekst opnieuw te embedden — alleen de extra
/// queries gaan in een tweede batch. Determinisme is de harde eis: dit pad
/// (dat een GESLAAGDE embedding vereist en dus door de FailingEmbeddings-
/// tests niet geraakt wordt) moet byte-voor-byte dezelfde prompt + citaties
/// geven als de niet-hergebruik-variant die searchText wél opnieuw embedt.
///
/// Opzet: de kaart/regel-entiteiten worden bewust zónder opgeslagen embedding
/// geseed, zodat de vector-kanalen (die op EF InMemory geen CosineDistance
/// kunnen vertalen) op de null-guard leeg blijven — qv is niettemin non-null,
/// dus het reuseRaw-pad en de vector-afhankelijke takken worden écht
/// doorlopen. Het FTS-kanaal is een woord-match op een vaste vraag, zodat het
/// invariant is voor de zoektekst en de twee varianten alleen in het
/// embed-gedrag verschillen.</summary>
[Collection("ask-service-env")]
public class AskServiceReuseRawTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";
    private const string Query = "deflect showdown";

    // reuseRaw: normalized == de ruwe vraag (ordinal gelijk) + een extra query.
    private static readonly string ReuseRewrite =
        $$"""{"normalized":{{JsonStr(Question)}},"queries":[{{JsonStr(Query)}}],"terms":[]}""";
    // Niet-hergebruik: een andere zoekzin (bevat "Deflect", dus dezelfde
    // mechaniek-match) → searchText != retrievalText → searchText wordt wél
    // opnieuw geëmbed.
    private const string OtherSearch = "Deflect interaction during a showdown";
    private static readonly string ReembedRewrite =
        $$"""{"normalized":{{JsonStr(OtherSearch)}},"queries":[{{JsonStr(Query)}}],"terms":[]}""";

    [Fact]
    public async Task AskAsync_ReuseRaw_HergebruiktRuweEmbedding_ZonderSearchTextOpnieuwTeEmbedden()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var embeddings = new RecordingEmbeddings();
        var ai = new RecordingAi(ReuseRewrite);
        var svc = new TestableAskService(db, embeddings.Service, ai.Client, Factory(db));

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        // De ruwe tekst is één keer geëmbed (onder de rewrite); de tweede
        // batch bevat alléén de extra query, niet searchText — het bewijs dat
        // de ruwe-vraag-embedding is hergebruikt i.p.v. opnieuw gemaakt.
        Assert.Equal([Question], embeddings.Batches[0]);
        Assert.Equal([Query], embeddings.Batches[1]);
        Assert.Equal(2, embeddings.Batches.Count);
        Assert.DoesNotContain(embeddings.Batches.Skip(1), b => b.Contains(Question));
        // qv was non-null: geen embedding-uitval-marker in de trace.
        var trace = await db.AskTraces.SingleAsync();
        Assert.DoesNotContain("embedding-uitval", trace.Sections ?? "");
    }

    [Fact]
    public async Task AskAsync_ReuseRaw_ByteVoorByteZelfdePromptEnCitaties_AlsHeruitEmbedden()
    {
        using var dbReuse = NewDb();
        await SeedAsync(dbReuse);
        using var dbReembed = NewDb();
        await SeedAsync(dbReembed);
        var aiReuse = new RecordingAi(ReuseRewrite);
        var aiReembed = new RecordingAi(ReembedRewrite);
        var reuse = new TestableAskService(
            dbReuse, new RecordingEmbeddings().Service, aiReuse.Client, Factory(dbReuse));
        var reembed = new TestableAskService(
            dbReembed, new RecordingEmbeddings().Service, aiReembed.Client, Factory(dbReembed));

        var resultReuse = await reuse.AskAsync(Question);
        var resultReembed = await reembed.AskAsync(Question);

        // De kern-eis: het reuseRaw-pad levert exact dezelfde prompt als de
        // variant die searchText opnieuw embedt — determinisme bewezen met
        // een geslaagde embedding.
        Assert.Equal(aiReembed.AnswerPrompts.Single(), aiReuse.AnswerPrompts.Single());
        Assert.Equal(resultReembed.Answer, resultReuse.Answer);
        Assert.Equal(
            resultReembed.Citations.Select(c => (c.N, c.Section, c.Text)),
            resultReuse.Citations.Select(c => (c.N, c.Section, c.Text)));
    }

    // --- testinfra (patroon AskServiceParallelRetrievalTests) -------------

    private static string JsonStr(string s) => JsonSerializer.Serialize(s);

    private static IDbContextFactory<RbRulesDbContext> Factory(RbRulesDbContext db) =>
        new TestDbFactory((DbContextOptions<RbRulesDbContext>)db.GetService<IDbContextOptions>());

    private sealed class TestDbFactory(DbContextOptions<RbRulesDbContext> options)
        : IDbContextFactory<RbRulesDbContext>
    {
        public RbRulesDbContext CreateDbContext() => new InMemoryDbContext(options);
    }

    /// <summary>FTS-seam die de zoektekst bewust negeert en altijd tegen de
    /// vaste vraag woord-matcht: zo is het FTS-kanaal invariant en verschillen
    /// de twee varianten uitsluitend in het embed-gedrag (reuse vs re-embed).</summary>
    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        IDbContextFactory<RbRulesDbContext> factory)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance, factory)
    {
        private readonly IDbContextFactory<RbRulesDbContext> _factory = factory;

        protected override async Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            await using var ctx = _factory.CreateDbContext();
            var words = Question.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .ToList();
            var rows = await ctx.RuleChunks.AsNoTracking()
                .Select(c => new { c.Id, c.SourceId, c.Text })
                .ToListAsync(ct);
            return [.. rows
                .Where(r => words.Any(w => r.Text.ToLowerInvariant().Contains(w)))
                .OrderBy(r => r.Id)
                .Select(r => (r.Id, r.SourceId))];
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    /// <summary>Echte EmbeddingService op een gestubde Ollama die geldige
    /// 1024-dim vectoren teruggeeft en elke input-batch vastlegt — het bewijs
    /// welke teksten wél en niet opnieuw geëmbed werden.</summary>
    private sealed class RecordingEmbeddings
    {
        public List<string[]> Batches { get; } = [];

        public EmbeddingService Service => new(
            new HttpClient(new StubHandler(async req =>
            {
                var body = await req.Content!.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var inputs = doc.RootElement.GetProperty("input")
                    .EnumerateArray().Select(x => x.GetString()!).ToArray();
                lock (Batches) Batches.Add(inputs);
                var vectors = inputs.Select(_ => Enumerable.Repeat(0.1f, 1024).ToArray()).ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { embeddings = vectors }),
                        Encoding.UTF8, "application/json"),
                };
            }))
            { BaseAddress = new Uri("http://ollama.test") });
    }

    /// <summary>Echte RbAiClient die op prompt-inhoud routeert (rewrite vs.
    /// antwoordcall) en de antwoord-prompts vastlegt.</summary>
    private sealed class RecordingAi(string rewriteAnswer)
    {
        public List<string> AnswerPrompts { get; } = [];

        public RbAiClient Client => new(
            new HttpClient(new StubHandler(async req =>
            {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
                if (prompt.Contains("Context-fragmenten:"))
                {
                    lock (AnswerPrompts) AnswerPrompts.Add(prompt);
                    return Json(new { answer = Answer });
                }
                return Json(new { answer = rewriteAnswer });
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

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

    /// <summary>Zónder opgeslagen embeddings (zie klasse-samenvatting): de
    /// vector-kanalen blijven op de null-guard leeg terwijl qv wél non-null is,
    /// dus het reuseRaw-pad wordt écht doorlopen.</summary>
    private static async Task SeedAsync(RbRulesDbContext db)
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
        db.Cards.Add(new Card
        {
            RiftboundId = "OGN-042", Name = "Yasuo, Unforgiven",
            Type = "Unit", Domains = ["Storm"], Energy = 3, Might = 4,
            TextPlain = "Deflect. When Yasuo blocks, exhaust target unit.",
            Mechanics = ["Deflect"],
        });
        await db.SaveChangesAsync();
    }
}
