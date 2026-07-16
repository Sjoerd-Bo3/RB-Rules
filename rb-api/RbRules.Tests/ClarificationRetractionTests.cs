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

/// <summary>Opruimstap (#185): vóór deze scheiding matchte
/// <see cref="ClarificationSources.IsMatch"/> ook patch-notes-bronnen, dus
/// bestaande installaties kunnen al clarify-mining-<see cref="Correction"/>s
/// dragen wier Provenance naar een patch-notes-bron wijst (bv. de lege
/// Legion-"ruling" uit issue #185). <see cref="ClarificationMiningService.
/// RunAsync"/> trekt die bij elke run terug — verified én pending, idempotent
/// (na de eerste keer is er niets meer om te retracten).</summary>
public class ClarificationRetractionTests
{
    private const string PatchNotesSourceId = "core-rules-patch-notes";
    private const string PatchNotesUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/";
    private const string FaqSourceId = "playriftbound-com-unleashed-rules-faq-and-clarifications";
    private const string FaqUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    [Fact]
    public async Task RunAsync_VerifiedPatchNotesRuling_WordtIngetrokken()
    {
        using var db = NewDb();
        AddPatchNotesSource(db);
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion's gedrag is in deze update verduidelijkt.",
            Provenance = $"clarify-mining:{PatchNotesSourceId}", SourceRef = PatchNotesUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Retracted);
        Assert.Contains("patch-notes-ruling(en) ingetrokken", r.Message);
        Assert.Empty(await db.Corrections.ToListAsync());
        var log = await db.RunLogs.SingleAsync(l => l.Ref == "cleanup-patch-notes");
        Assert.Equal("clarify", log.Kind);
        Assert.Contains("1 patch-notes-clarify-ruling(en)", log.Detail);
    }

    [Fact]
    public async Task RunAsync_PendingPatchNotesRuling_WordtOokIngetrokken()
    {
        // Sjoerd-eis: de opruiming geldt zowel voor verified als voor
        // unverified/pending items — geen statusfilter.
        using var db = NewDb();
        AddPatchNotesSource(db);
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion's gedrag is in deze update verduidelijkt.",
            Provenance = $"clarify-mining:{PatchNotesSourceId}", SourceRef = PatchNotesUrl,
            Status = "unverified", StatusReason = "onderwerp niet herkend",
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Retracted);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_FaqRuling_BlijftStaan()
    {
        // Een legitieme FAQ-ruling (geen patch-notes-bron) hoort niet geraakt
        // te worden door de opruiming.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = FaqSourceId, Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion betekent dat je een item op de chain finalizet.",
            Provenance = $"clarify-mining:{FaqSourceId}", SourceRef = FaqUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Retracted);
        Assert.Single(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_LlmGeklassificeerdAlsPatchNotes_WordtIngetrokken_OokMetFaqInDeNaam()
    {
        // Het gemengde-bron-scenario (#185-review, "Rules FAQ and Patch
        // Notes"): de naam ziet er FAQ-achtig uit (de heuristiek zou "faq"
        // zeggen), maar de LLM heeft deze bron expliciet als "patch-notes"
        // geclassificeerd (Source.ContentKind) — de retractie moet die
        // gepersisteerde kind gebruiken, niet de naam-heuristiek, en de oude
        // ruling alsnog opruimen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = FaqSourceId, Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.PatchNotes, ContentKindSource = SourceContentKind.LlmOrigin,
        });
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion's gedrag is in deze update verduidelijkt.",
            Provenance = $"clarify-mining:{FaqSourceId}", SourceRef = FaqUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Retracted);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_BronRijVerwijderd_ValtTerugOpSourceIdInProvenance()
    {
        // Ook zonder (meer) een Source-rij herkent de opruiming een
        // patch-notes-herkomst aan de sourceId zelf in Provenance — de
        // seed-Id's dragen het "-patch-notes"-signaal al.
        using var db = NewDb();
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion's gedrag is in deze update verduidelijkt.",
            Provenance = $"clarify-mining:{PatchNotesSourceId}",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Retracted);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_TweedeRun_IsIdempotent_NietsMeerTeRetracten()
    {
        using var db = NewDb();
        AddPatchNotesSource(db);
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion's gedrag is in deze update verduidelijkt.",
            Provenance = $"clarify-mining:{PatchNotesSourceId}", SourceRef = PatchNotesUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: false));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        Assert.Equal(1, first.Retracted);
        Assert.Equal(0, second.Retracted);
        Assert.DoesNotContain("ingetrokken", second.Message);
        Assert.Single(await db.RunLogs.Where(l => l.Ref == "cleanup-patch-notes").ToListAsync());
    }

    // --- testinfra (zelfde patroon als ClarificationMiningServiceTests) --

    private static void AddPatchNotesSource(RbRulesDbContext db) => db.Sources.Add(new Source
    {
        Id = PatchNotesSourceId, Name = "Core Rules Patch Notes (officieel)", Url = PatchNotesUrl,
        Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
    });

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

    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static EmbeddingService Embeddings(bool ok) => new(
        new HttpClient(new StubHandler(_ => ok
            ? Json(new { embeddings = new[] { Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray() } })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}
