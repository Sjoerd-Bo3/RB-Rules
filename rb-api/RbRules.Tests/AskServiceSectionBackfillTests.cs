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

/// <summary>Regressietests voor de §-verwijzings-bijlading (#364): in
/// productie eindigde een /ask-antwoord op "Onzeker" met letterlijk "de
/// volledige tekst van §811 … is niet meegeleverd" — de meegeleverde
/// fragmenten verwezen naar secties die de retrieval niet ophaalde. De
/// deterministische expansie vóór de modelcall moet zulke secties bijladen
/// als gewone citatie, zonder dubbelingen, zonder niet-bestaande codes en
/// gecapt. Testopzet volgt AskServiceDegradationTests: EF InMemory, falende
/// embeddings (vector-kanalen vervallen), het FTS-kanaal vervangen door een
/// woord-match, en een rb-ai-stub die de verstuurde prompts opneemt zodat de
/// tests kunnen bewijzen dat de bijgeladen tekst écht in de context zit.
/// In dezelfde xUnit-collectie als AskServiceAgenticTests (proces-brede
/// ASK_AGENTIC-env).</summary>
[Collection("ask-service-env")]
public class AskServiceSectionBackfillTests
{
    private const string SourceId = "riot-core-rules";
    // Woorden >= 4 tekens sturen de FTS-stub: fragmenten die bijgeladen
    // moeten worden mogen deze woorden dus niet bevatten.
    private const string Question = "Hoe werkt targeting tijdens een showdown";
    private const string HiddenText =
        "Hidden: a unit with this keyword cannot be chosen by opposing effects.";

    [Fact]
    public async Task AskAsync_FragmentVerwijstNaarOntbrekendeSectie_LaadtDieBij()
    {
        // Het §811-scenario: het opgehaalde fragment (§355) noemt §811, de
        // index bevat 811, maar de retrieval haalde die niet op.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting is restricted; see §811 for Hidden."),
            Chunk("811", HiddenText));
        var (svc, prompts) = Svc(db);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Citations.Count);
        var backfilledCitation = Assert.Single(result.Citations, c => c.Section == "811");
        Assert.Equal(HiddenText, backfilledCitation.Text);
        // De kern van #364: de sectietekst zit ná expansie in de context die
        // naar het model gaat — niet alleen in de citatielijst.
        Assert.Contains(prompts, p =>
            p.Contains("Context-fragmenten") && p.Contains(HiddenText));
        // Teller in de trace, herkenbaar voor de beheerder.
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("[§-bijgeladen: 1]", trace.Sections);
        Assert.Contains("§811", trace.Sections);
    }

    [Fact]
    public async Task AskAsync_SectieAlInContext_NietDubbel()
    {
        // §355 verwijst naar §205, maar §205 is óók al door de retrieval
        // opgehaald (de tekst matcht de vraag) — niets bijladen, geen marker.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting works as in §205."),
            Chunk("205", "The showdown steps determine targeting order."));
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync(Question);

        Assert.Equal(2, result.Citations.Count);
        Assert.Single(result.Citations, c => c.Section == "205");
        var trace = await db.AskTraces.SingleAsync();
        Assert.DoesNotContain("§-bijgeladen", trace.Sections);
    }

    [Fact]
    public async Task AskAsync_OnbestaandeSectie_Overgeslagen()
    {
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting is restricted; see §999."));
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync(Question);

        Assert.True(result.Ok);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("355", citation.Section);
        // Geen ruis: niet bestaan is geen uitval en geen bijlading.
        var trace = await db.AskTraces.SingleAsync();
        Assert.DoesNotContain("§-bijgeladen", trace.Sections);
        Assert.DoesNotContain("kanaal-uitval", trace.Sections);
    }

    [Fact]
    public async Task AskAsync_SubcodeValtTerugOpOuder()
    {
        // Het fragment noemt §811.1; alleen §811 bestaat — de ouder wordt
        // bijgeladen in plaats van de verwijzing te laten vallen.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting follows §811.1 exactly."),
            Chunk("811", HiddenText));
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync(Question);

        var backfilledCitation = Assert.Single(result.Citations, c => c.Section == "811");
        Assert.Equal(HiddenText, backfilledCitation.Text);
    }

    [Fact]
    public async Task AskAsync_BijgeladenSectie_KrijgtOuderketen()
    {
        // Bijgeladen citaties zijn volwaardig (#39): §811.1 komt mét de
        // tekst van ouder §811 binnen, net als gewone citaties.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting follows §811.1 exactly."),
            Chunk("811", "Rules for the Hidden keyword."),
            Chunk("811.1", HiddenText));
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync(Question);

        var backfilledCitation = Assert.Single(result.Citations, c => c.Section == "811.1");
        Assert.NotNull(backfilledCitation.Parents);
        var parent = Assert.Single(backfilledCitation.Parents!);
        Assert.Equal("811", parent.Code);
        Assert.Equal("Rules for the Hidden keyword.", parent.Text);
    }

    [Fact]
    public async Task AskAsync_VerwijzingInDeVraagZelf_LaadtBij()
    {
        // Ook de vraag wordt gescand: "§811" in de vraag is genoeg, ook als
        // geen enkel fragment ernaar verwijst.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting is restricted."),
            Chunk("811", HiddenText));
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync($"Wat zegt §811 over {Question.ToLowerInvariant()}");

        Assert.Single(result.Citations, c => c.Section == "811");
    }

    [Fact]
    public async Task AskAsync_CapBegrenstDeBijlading()
    {
        // Eén fragment noemt acht bestaande, niet-opgehaalde secties — méér
        // dan de cap, anders kan deze test de grens niet raken (#286-les).
        var codes = new[] { "801", "802", "803", "804", "805", "806", "807", "808" };
        Assert.Equal(8, codes.Length);
        using var db = NewDb();
        await SeedAsync(db, [
            Chunk("355", "During a showdown, targeting spans " +
                string.Join(", ", codes.Select(c => $"§{c}")) + "."),
            .. codes.Select(c => Chunk(c, $"Rule text for {c}: no overlap words here.")),
        ]);
        var (svc, _) = Svc(db);

        var result = await svc.AskAsync(Question);

        // Uitgeschreven literals (#286): 1 opgehaald fragment + 6 bijgeladen;
        // de laatste twee genoemde codes vallen af.
        Assert.Equal(7, result.Citations.Count);
        Assert.DoesNotContain(result.Citations, c => c.Section == "807");
        Assert.DoesNotContain(result.Citations, c => c.Section == "808");
        var trace = await db.AskTraces.SingleAsync();
        Assert.Contains("[§-bijgeladen: 6]", trace.Sections);
    }

    [Fact]
    public async Task AskStreamingAsync_BijgeladenSectie_InMetaCitaties()
    {
        // Streaming deelt AskCoreAsync: de expansie zit vóór de modelcall,
        // dus het meta-frame bevat de bijgeladen sectie al.
        using var db = NewDb();
        await SeedAsync(db,
            Chunk("355", "During a showdown, targeting is restricted; see §811 for Hidden."),
            Chunk("811", HiddenText));
        var (svc, _) = Svc(db);

        AskStreamMeta? meta = null;
        var result = await svc.AskStreamingAsync(
            Question, images: null, history: null,
            onMeta: m => { meta = m; return Task.CompletedTask; },
            onDelta: _ => Task.CompletedTask);

        Assert.True(result.Ok);
        Assert.NotNull(meta);
        Assert.Single(meta!.Citations, c => c.Section == "811");
        Assert.Single(result.Citations, c => c.Section == "811");
    }

    // --- testinfra -------------------------------------------------------

    /// <summary>AskService met alléén het FTS-kanaal vervangen door een
    /// simpele woord-match (tsvector vertaalt niet naar EF InMemory) — zelfde
    /// seam als AskServiceDegradationTests; de bijlading zelf draait op de
    /// echte code.</summary>
    private sealed class TestableAskService(
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

    /// <summary>Service plus de lijst waarin de rb-ai-stub elke verstuurde
    /// request-body opneemt — de regressietest bewijst daarmee dat de
    /// bijgeladen sectietekst in de daadwerkelijke prompt zit.</summary>
    private static (TestableAskService Svc, List<string> Prompts) Svc(RbRulesDbContext db)
    {
        var prompts = new List<string>();
        return (new TestableAskService(db, FailingEmbeddings(), Ai(prompts)), prompts);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond, List<string>? bodies = null)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (bodies is not null && request.Content is not null)
                bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
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

    /// <summary>Echte RbAiClient op een gestubde handler (patroon
    /// AskServiceDegradationTests); het antwoord bevat bewust geen accolades,
    /// zodat de rewrite-parse null oplevert (rauwe-vraag-pad).</summary>
    private static RbAiClient Ai(List<string> prompts)
    {
        const string answer = "**Oordeel:** Zie de regels. [1]";
        return new(
            new HttpClient(new StubHandler(req => req.RequestUri!.AbsolutePath == "/ask/stream"
                ? Ndjson(
                    JsonSerializer.Serialize(new { type = "delta", text = answer }),
                    JsonSerializer.Serialize(new { type = "done", answer }))
                : Json(new { answer }), prompts))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
    }

    /// <summary>Echte EmbeddingService op een gestubde Ollama die altijd 500
    /// geeft — de vector-kanalen vervallen; de FTS-stub stuurt de retrieval,
    /// zodat de fixture exact bepaalt wat er wél en níet opgehaald is.</summary>
    private static EmbeddingService FailingEmbeddings() => new(
        new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Ndjson(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson"),
    };

    private static (string Code, string Text) Chunk(string code, string text) => (code, text);

    private static async Task SeedAsync(
        RbRulesDbContext db, params (string Code, string Text)[] chunks)
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
        db.RuleChunks.AddRange(chunks.Select((c, i) => new RuleChunk
        {
            DocumentId = doc.Id, SourceId = SourceId, SectionCode = c.Code,
            ChunkIndex = i, Page = i + 1, Text = c.Text,
        }));
        await db.SaveChangesAsync();
    }
}
