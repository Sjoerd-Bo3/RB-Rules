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

/// <summary>Servicetests voor het misvattingen-kanaal (#125): rejected/
/// superseded claims mét weerlegging gaan als negatieve kennis mee — eigen
/// promptblok en label, cap van twee, "misvatting:"-prefix in het kennislagen-
/// trace-veld, en beide bewijzen (community-citaat + officiële weerlegging)
/// in het AskResult. Zelfde testopzet als AskServiceDegradationTests (EF
/// InMemory, echte RbAiClient op een gestubde handler, FTS vervangen); de
/// vector-orde van de kandidaten-query vertaalt niet naar InMemory, dus die
/// query is — net als het FTS-kanaal — vervangen door een seam die álle
/// claims als kandidaat aanlevert: de poort (status/weerlegging/afstand/cap,
/// ClaimRetrieval.SelectMisconceptions) blijft productiecode en wordt hier
/// dus écht geraakt. In dezelfde xUnit-collectie als de agentic-tests omdat
/// die de proces-brede ASK_AGENTIC-env zetten.</summary>
[Collection("ask-service-env")]
public class AskServiceMisconceptionTests
{
    private const string SourceId = "riot-core-rules";
    private const string Question = "Hoe werkt Deflect tijdens een showdown?";
    private const string DeflectRebuttal =
        "§466.2 zegt dat Deflect alleen gekozen targets blokkeert.";

    [Fact]
    public async Task AskAsync_MisvattingenKanaal_PoortCapLabelEnBeideBewijzen()
    {
        using var db = NewDb();
        await SeedRulesAsync(db);
        // Vier claims rond de poort: twee echte misvattingen (dichtstbij),
        // één rejected zónder weerlegging (doet niet mee), één accepted
        // (hoort in het claims-kanaal, nooit in het misvattingen-kanaal) en
        // één derde misvatting die op de cap van twee moet stranden.
        db.Claims.AddRange(
            new Claim
            {
                TopicType = "mechanic", TopicRef = "Deflect",
                Statement = "Deflect blokkeert ook spells zonder targets.",
                Status = "rejected", StatusReason = DeflectRebuttal,
                TrustScore = 0.10, // seam: TrustScore = afstand → dichtstbij
            },
            new Claim
            {
                TopicType = "mechanic", TopicRef = "Hidden",
                Statement = "Hidden units kunnen nooit getarget worden.",
                Status = "superseded",
                StatusReason = "de officiële regels spreken deze claim tegen",
                TrustScore = 0.20,
            },
            new Claim
            {
                TopicType = "mechanic", TopicRef = "Accelerate",
                Statement = "Accelerate mag alleen in je eigen beurt.",
                Status = "rejected", StatusReason = null, // kale rejected: geen kennis
                TrustScore = 0.05,
            },
            new Claim
            {
                TopicType = "mechanic", TopicRef = "Shield",
                Statement = "Shield vervalt aan het einde van de beurt.",
                Status = "accepted", StatusReason = "per ongeluk gevuld",
                TrustScore = 0.01,
            },
            new Claim
            {
                TopicType = "section", TopicRef = "300",
                Statement = "Een showdown kan nooit twee keer per beurt.",
                Status = "rejected", StatusReason = "§300.4 staat dit expliciet toe.",
                TrustScore = 0.30, // derde misvatting: strandt op de cap van 2
            });
        await db.SaveChangesAsync();
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = db.Claims.Single(c => c.TopicRef == "Deflect").Id,
            SourceId = "community-forum",
            Url = "https://forum.example.com/deflect-topic",
            QuoteExcerpt = "deflect just blocks everything, even untargeted spells",
        });
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Oordeel:** Nee. [1]");
        var svc = Svc(db, ai);

        var result = await svc.AskAsync(Question);

        // Poort + cap: twee misvattingen, dichtstbij eerst; de kale rejected
        // en de accepted claim ontbreken.
        Assert.NotNull(result.Misconceptions);
        Assert.Equal(2, result.Misconceptions!.Count);
        var deflect = result.Misconceptions[0];
        Assert.Equal("Deflect", deflect.TopicRef);
        Assert.Equal("Hidden", result.Misconceptions[1].TopicRef);

        // Beide bewijzen voor de UI: de officiële weerlegging (mét §-code
        // voor de sectie-link) én het community-citaat met bron-URL.
        Assert.Equal(DeflectRebuttal, deflect.Rebuttal);
        Assert.Equal("466.2", deflect.RebuttalSection);
        var source = Assert.Single(deflect.Sources);
        Assert.Equal("Community Forum", source.SourceName);
        Assert.Equal("https://forum.example.com/deflect-topic", source.Url);
        Assert.Contains("untargeted spells", source.Quote);
        Assert.Null(result.Misconceptions[1].RebuttalSection);

        // Prompt: het misvattingen-blok met eigen label en weerleg-framing.
        var prompt = ai.AnswerPrompt();
        Assert.Contains("GEDOCUMENTEERDE MISVATTINGEN", prompt);
        Assert.Contains("[misvatting, weerlegd door §466.2] Deflect:", prompt);
        Assert.Contains("[misvatting, officieel weerlegd] Hidden:", prompt);
        Assert.Contains("een veelgemaakte lezing is X, maar [n] zegt Y", prompt);
        Assert.DoesNotContain("Accelerate mag alleen", prompt);
        Assert.DoesNotContain("Shield vervalt", prompt);

        // Trace: kennislagen-regel met het "misvatting:"-prefix in het
        // bestaande CommunityClaims-veld (bewust geen migratie).
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("misvatting:mechanic:Deflect", trace.CommunityClaims);
        Assert.Contains("misvatting:mechanic:Hidden", trace.CommunityClaims);
        Assert.DoesNotContain("misvatting:mechanic:Accelerate", trace.CommunityClaims);
    }

    [Fact]
    public async Task AskAsync_EmbeddingUitval_MisvattingenKanaalVervaltNetjes()
    {
        // Zonder query-vector (#100) vervalt het kanaal — geen crash, lege
        // lijst; dit pad loopt door de productie-query (geen seam-override).
        using var db = NewDb();
        await SeedRulesAsync(db);
        db.Claims.Add(new Claim
        {
            TopicType = "mechanic", TopicRef = "Deflect",
            Statement = "Deflect blokkeert ook spells zonder targets.",
            Status = "rejected", StatusReason = DeflectRebuttal,
        });
        await db.SaveChangesAsync();

        var ai = new RecordingAi("**Oordeel:** Nee. [1]");
        var svc = new ProductionPathAskService(db, FailingEmbeddings(), ai.Client);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.NotNull(result.Misconceptions);
        Assert.Empty(result.Misconceptions!);
        Assert.DoesNotContain("GEDOCUMENTEERDE MISVATTINGEN", ai.AnswerPrompt());
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>FTS-seam (zelfde reden als AskServiceDegradationTests) plus de
    /// misvattingen-kandidaten-seam: de vector-orde vertaalt niet naar EF
    /// InMemory, dus de seam levert álle claims als kandidaat aan met
    /// TrustScore als gesimuleerde afstand — de poort in productiecode
    /// (ClaimRetrieval.SelectMisconceptions) doet daarna het echte werk.</summary>
    private class ProductionPathAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : AskService(db, embeddings, ai,
            new AgenticRelationService(db, new BrainService(
                db, embeddings, new CardResolver(db), NullLogger<BrainService>.Instance)),
            new RequestUserContext(), NullLogger<AskService>.Instance)
    {
        private readonly RbRulesDbContext _db = db;

        protected override async Task<List<(long Id, string SourceId)>> FullTextChunksAsync(
            string searchText, CancellationToken ct)
        {
            var words = searchText.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .ToList();
            var rows = await _db.RuleChunks.AsNoTracking()
                .Select(c => new { c.Id, c.SourceId, c.Text })
                .ToListAsync(ct);
            return [.. rows
                .Where(r => words.Any(w => r.Text.ToLowerInvariant().Contains(w)))
                .Select(r => (r.Id, r.SourceId))];
        }
    }

    private sealed class TestableAskService(
        RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
        : ProductionPathAskService(db, embeddings, ai)
    {
        private readonly RbRulesDbContext _db = db;

        protected override async Task<List<MisconceptionCandidate>> MisconceptionCandidatesAsync(
            Vector? qv, CancellationToken ct)
        {
            // Bewust ongefilterd én zonder qv-eis (embeddings falen in deze
            // opzet, #100-patroon): juist zo bewijst de test dat de poort in
            // productiecode accepted/kale-rejected/te-verre kandidaten weert.
            var rows = await _db.Claims.AsNoTracking().ToListAsync(ct);
            return [.. rows.Select(c => new MisconceptionCandidate(
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Status, c.StatusReason,
                c.TrustScore))];
        }
    }

    private static TestableAskService Svc(RbRulesDbContext db, RecordingAi ai) =>
        new(db, FailingEmbeddings(), ai.Client);

    /// <summary>Echte RbAiClient op een handler die alle request-bodies
    /// vastlegt, zodat de test de daadwerkelijk verstuurde prompt (mét
    /// misvattingen-blok) kan inspecteren. Antwoord bevat bewust geen
    /// accolades: de rewrite-parse levert dan null (rauwe-vraag-pad).</summary>
    private sealed class RecordingAi(string answer)
    {
        private readonly List<string> _bodies = [];

        public RbAiClient Client => new(
            new HttpClient(new Handler(this, answer)) { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);

        /// <summary>De prompt van de antwoord-call (de call mét context-
        /// fragmenten; de eerdere /ask-call is de query-rewrite).</summary>
        public string AnswerPrompt()
        {
            foreach (var body in _bodies)
            {
                using var doc = JsonDocument.Parse(body);
                var prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
                if (prompt.Contains("Context-fragmenten:")) return prompt;
            }
            throw new InvalidOperationException("geen antwoord-call gezien");
        }

        private sealed class Handler(RecordingAi owner, string answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Content is not null)
                    owner._bodies.Add(await request.Content.ReadAsStringAsync(ct));
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { answer }),
                        Encoding.UTF8, "application/json"),
                };
            }
        }
    }

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => throw exception();
    }

    /// <summary>Failing Ollama (#100-patroon): qv blijft null — de overige
    /// vector-kanalen vervallen, precies zoals in de degradatietests.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new ThrowingHandler(() => new HttpRequestException("ollama plat")))
        { BaseAddress = new Uri("http://ollama.test") });

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

    private static async Task SeedRulesAsync(RbRulesDbContext db)
    {
        db.Sources.AddRange(
            new Source
            {
                Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
                Type = "official", TrustTier = 1, Rank = 1, Parser = "pdf", Cadence = "weekly",
            },
            new Source
            {
                Id = "community-forum", Name = "Community Forum",
                Url = "https://forum.example.com", Type = "community",
                TrustTier = 3, Rank = 5, Parser = "html", Cadence = "daily",
            });
        var doc = new Document
        {
            SourceId = SourceId, Content = "pdf-tekst", ContentHash = "hash",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = "466.2",
            ChunkIndex = 0, Page = 12,
            Text = "Deflect: a unit with Deflect cannot be chosen by opposing spells during a showdown.",
        });
        await db.SaveChangesAsync();
    }
}
