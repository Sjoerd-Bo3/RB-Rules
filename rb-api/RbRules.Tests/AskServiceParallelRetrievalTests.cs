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

/// <summary>Servicetests voor de parallelle retrieval-kanalen (#152):
/// kanaal-uitval-isolatie (één gooiend kanaal ⇒ antwoord zonder dat kanaal,
/// mét marker in de trace — nooit een 500), gelijkheid van de uitkomst mét
/// en zónder contextfactory (parallel vs. sequentieel), en determinisme van
/// de promptopbouw (zelfde input ⇒ byte-voor-byte dezelfde prompt). Zelfde
/// testopzet als AskServiceDegradationTests (EF InMemory, echte RbAiClient
/// op een gestubde handler, FTS vervangen); de factory-variant maakt verse
/// InMemory-contexten op dezelfde store — precies wat de productie-factory
/// met Npgsql doet. Zelfde xUnit-collectie vanwege de proces-brede
/// ASK_AGENTIC-env van de agentic-tests.</summary>
[Collection("ask-service-env")]
public class AskServiceParallelRetrievalTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string Answer = "**Oordeel:** Ja, Deflect werkt ook in een showdown. [1]";
    private const string RewriteJson =
        """{"normalized":"how does deflect work during a showdown","queries":["deflect showdown"],"terms":[]}""";

    [Fact]
    public async Task AskAsync_MisvattingenKanaalGooit_AntwoordKomtZonderDatKanaal()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new RecordingAi(rewriteAnswer: "geen json");
        // Parallel-modus (mét factory): juist daar moet één gooiend kanaal
        // geïsoleerd blijven.
        var svc = new MisconceptionThrowingAskService(db, FailingEmbeddings(), ai.Client,
            Factory(db));

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.Equal(Answer, result.Answer);
        // Het kanaal is leeg, niet de vraag stuk.
        Assert.NotNull(result.Misconceptions);
        Assert.Empty(result.Misconceptions!);
        Assert.DoesNotContain("GEDOCUMENTEERDE MISVATTINGEN", ai.AnswerPrompts.Single());
        // Marker voor de beheerder: wélk kanaal viel uit (reden in de logs).
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("kanaal-uitval: misvattingen", trace.Sections);
    }

    [Fact]
    public async Task AskAsync_FtsKanaalGooit_GeenExceptieMaarEerlijkeDegradatie()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new RecordingAi(rewriteAnswer: "geen json");
        // FTS gooit en de embeddings zijn ook plat: er is dan geen enkel
        // regel-kanaal meer over. Vóór #152 was een gooiend kanaal een kale
        // 500; nu is het de bestaande eerlijke lege-retrieval-melding.
        var svc = new FtsThrowingAskService(db, FailingEmbeddings(), ai.Client);

        var result = await svc.AskAsync(Question);

        Assert.False(result.Ok);
        Assert.Contains("tijdelijk beperkt", result.Answer);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task AskAsync_MetEnZonderFactory_IdentiekeUitkomst()
    {
        // Twee identiek geseede databases: één service sequentieel (zonder
        // factory, zoals de tests op InMemory) en één parallel (mét factory).
        // De kanalen leveren aan vaste slots, dus antwoord, citaties én de
        // exacte prompt horen gelijk te zijn.
        using var dbSeq = NewDb();
        await SeedAsync(dbSeq);
        using var dbPar = NewDb();
        await SeedAsync(dbPar);
        var aiSeq = new RecordingAi(RewriteJson);
        var aiPar = new RecordingAi(RewriteJson);
        var seq = new TestableAskService(dbSeq, FailingEmbeddings(), aiSeq.Client, factory: null);
        var par = new TestableAskService(dbPar, FailingEmbeddings(), aiPar.Client, Factory(dbPar));

        var resultSeq = await seq.AskAsync(Question);
        var resultPar = await par.AskAsync(Question);

        Assert.Equal(resultSeq.Answer, resultPar.Answer);
        Assert.Equal(
            resultSeq.Citations.Select(c => (c.N, c.Section, c.Text)),
            resultPar.Citations.Select(c => (c.N, c.Section, c.Text)));
        Assert.Equal(aiSeq.AnswerPrompts.Single(), aiPar.AnswerPrompts.Single());
        // Geen kanaal-uitval in de parallelle variant.
        var trace = await dbPar.AskTraces.SingleAsync();
        Assert.DoesNotContain("kanaal-uitval", trace.Sections);
    }

    [Fact]
    public async Task AskAsync_TweeKeerDezelfdeVraag_ByteVoorByteDezelfdePrompt()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ai = new RecordingAi(RewriteJson);
        var svc = new TestableAskService(db, FailingEmbeddings(), ai.Client, Factory(db));

        await svc.AskAsync(Question);
        await svc.AskAsync(Question);

        Assert.Equal(2, ai.AnswerPrompts.Count);
        Assert.Equal(ai.AnswerPrompts[0], ai.AnswerPrompts[1]);
    }

    // --- testinfra (patroon AskServiceDegradationTests) -------------------

    /// <summary>Factory die verse InMemory-contexten op dezelfde store maakt
    /// (zelfde options-instantie ⇒ zelfde store) — het testequivalent van de
    /// productie-IDbContextFactory op Npgsql.</summary>
    private static IDbContextFactory<RbRulesDbContext> Factory(RbRulesDbContext db) =>
        new TestDbFactory((DbContextOptions<RbRulesDbContext>)db.GetService<IDbContextOptions>());

    private sealed class TestDbFactory(DbContextOptions<RbRulesDbContext> options)
        : IDbContextFactory<RbRulesDbContext>
    {
        public RbRulesDbContext CreateDbContext() => new InMemoryDbContext(options);
    }

    /// <summary>FTS-seam (tsvector vertaalt niet naar EF InMemory) die zijn
    /// eigen kanaal-context pakt — in parallel-modus draait dit kanaal naast
    /// de andere en mag het de scoped context niet aanraken.</summary>
    private class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        IDbContextFactory<RbRulesDbContext>? factory)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance, factory)
    {
        private readonly RbRulesDbContext _db = db;
        private readonly IDbContextFactory<RbRulesDbContext>? _factory = factory;

        protected override async Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            await using var owned = _factory?.CreateDbContext();
            var ctx = owned ?? _db;
            var words = searchText.ToLowerInvariant()
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

    /// <summary>Het misvattingen-kanaal gooit — de isolatie (#152) moet het
    /// antwoord overeind houden en het kanaal als uitgevallen markeren.</summary>
    private sealed class MisconceptionThrowingAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
        IDbContextFactory<RbRulesDbContext>? factory)
        : TestableAskService(db, embeddings, ai, factory)
    {
        protected override Task<List<MisconceptionCandidate>> MisconceptionCandidatesAsync(
            Vector? qv, CancellationToken ct) =>
            throw new InvalidOperationException("misvattingen-kanaal plat (test)");
    }

    /// <summary>Het FTS-kanaal gooit — samen met platte embeddings blijft er
    /// geen regel-kanaal over; dat moet de eerlijke degradatie-melding geven,
    /// geen 500.</summary>
    private sealed class FtsThrowingAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance)
    {
        protected override Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct) =>
            throw new InvalidOperationException("fts-kanaal plat (test)");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    /// <summary>Echte RbAiClient die op prompt-inhoud routeert (rewrite vs.
    /// antwoordcall) en alle antwoord-prompts vastlegt — het bewijsmateriaal
    /// voor de determinisme- en gelijkheids-asserties.</summary>
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

    /// <summary>Failing Ollama (#100-patroon): qv blijft null — de
    /// vector-kanalen vervallen; deze tests draaien om orkestratie.</summary>
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

    /// <summary>Rijke seed: regels (FTS), een kaart mét mechaniek (naam- en
    /// mechaniek-kanaal), een geverifieerde ruling (recentste-fallback) en een
    /// misvatting-claim — zoveel mogelijk kanalen doen echt mee in de
    /// gelijkheids- en determinisme-vergelijking.</summary>
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
        db.RuleChunks.AddRange(
            new RuleChunk
            {
                DocumentId = doc.Id, SourceId = SourceId, SectionCode = "101",
                ChunkIndex = 0, Page = 12,
                Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
            },
            new RuleChunk
            {
                DocumentId = doc.Id, SourceId = SourceId, SectionCode = "300",
                ChunkIndex = 1, Page = 30,
                Text = "A showdown begins when a unit attacks a contested battlefield.",
            });
        db.Cards.Add(new Card
        {
            RiftboundId = "OGN-042", Name = "Yasuo, Unforgiven",
            Type = "Unit", Domains = ["Storm"], Energy = 3, Might = 4,
            TextPlain = "Deflect. When Yasuo blocks, exhaust target unit.",
            Mechanics = ["Deflect"],
        });
        db.Corrections.Add(new Correction
        {
            Scope = "answer", Ref = "up",
            Text = "Deflect werkt ook tijdens een showdown.",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        db.Claims.Add(new Claim
        {
            TopicType = "mechanic", TopicRef = "Deflect",
            Statement = "Deflect blokkeert ook spells zonder targets.",
            Status = "rejected", StatusReason = "§466.2 weerlegt dit.",
        });
        await db.SaveChangesAsync();
    }
}
