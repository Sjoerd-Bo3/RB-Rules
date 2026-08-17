using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Notitie → geverifieerde ruling (#124): de beheerder-notitie op een
/// claim/relatie wordt een Correction met scope claim/relation en ref =
/// BrainRef van het onderwerp, direct verified en geembed via het bestaande
/// verify-pad — en idempotent (nogmaals promoveren werkt de ruling bij).
/// EmbeddingService is de échte client op een gestubde Ollama (patroon
/// ClaimMiningServiceTests, EF InMemory).</summary>
public class ReviewNoteServiceTests
{
    private const string Note = "Deflect beschermt alléén tegen spells, niet tegen abilities.";

    [Fact]
    public async Task PromoteClaimNote_MaaktGeverifieerdeRuling_MetEmbeddingEnBrainRef()
    {
        using var db = NewDb();
        var claim = await SeedClaimAsync(db, note: Note);
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        var r = await svc.PromoteClaimNoteAsync(claim.Id);

        Assert.Equal(PromoteNoteStatus.Promoted, r.Status);
        Assert.True(r.Embedded);
        Assert.False(r.Updated);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("claim", correction.Scope);
        Assert.Equal(BrainRef.Claim(claim.Id).Format(), correction.Ref);
        Assert.Equal(Note, correction.Text);
        // Question = het onderwerp van de claim: zo matcht de embedding
        // (vraag+correctie) vragen over hetzelfde onderwerp.
        Assert.Contains(claim.Statement, correction.Question);
        Assert.Equal("verified", correction.Status);
        Assert.NotNull(correction.VerifiedAt);
        Assert.NotNull(correction.Embedding);
        Assert.Equal("review-notitie", correction.Provenance);
    }

    [Fact]
    public async Task PromoteClaimNote_MeegegevenNotitie_WintEnWordtOpgeslagen()
    {
        // De promotie-actie mag in één stap: notitie meesturen overschrijft
        // (en bewaart) de opgeslagen notitie.
        using var db = NewDb();
        var claim = await SeedClaimAsync(db, note: "oude notitie");
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        var r = await svc.PromoteClaimNoteAsync(claim.Id, "  nieuwe uitleg  ");

        Assert.Equal(PromoteNoteStatus.Promoted, r.Status);
        Assert.Equal("nieuwe uitleg", claim.ReviewNote);
        Assert.Equal("nieuwe uitleg", (await db.Corrections.SingleAsync()).Text);
    }

    [Fact]
    public async Task PromoteClaimNote_TweeKeer_WerktDezelfdeRulingBij()
    {
        // Idempotent per onderwerp: geen dubbele rulings bij een tweede klik
        // of een bijgewerkte notitie.
        using var db = NewDb();
        var claim = await SeedClaimAsync(db, note: Note);
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        await svc.PromoteClaimNoteAsync(claim.Id);
        var again = await svc.PromoteClaimNoteAsync(claim.Id, "aangescherpte uitleg");

        Assert.Equal(PromoteNoteStatus.Promoted, again.Status);
        Assert.True(again.Updated);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("aangescherpte uitleg", correction.Text);
        Assert.Equal("verified", correction.Status);
    }

    [Fact]
    public async Task PromoteClaimNote_ZonderNotitie_GeeftNoNote_EnMaaktNiets()
    {
        using var db = NewDb();
        var claim = await SeedClaimAsync(db, note: null);
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        var r = await svc.PromoteClaimNoteAsync(claim.Id, "   ");

        Assert.Equal(PromoteNoteStatus.NoNote, r.Status);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task PromoteClaimNote_OnbekendId_GeeftNotFound()
    {
        using var db = NewDb();
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        var r = await svc.PromoteClaimNoteAsync(999, Note);

        Assert.Equal(PromoteNoteStatus.NotFound, r.Status);
    }

    [Fact]
    public async Task PromoteClaimNote_EmbeddingUitval_VerifieertZonderEmbedding()
    {
        // Ollama tijdelijk weg is een verwacht pad (conventie): de promotie
        // telt — de ruling doet via het recentste-vangnet in AskService al mee;
        // nogmaals promoveren embedt opnieuw.
        using var db = NewDb();
        var claim = await SeedClaimAsync(db, note: Note);
        var svc = new ReviewNoteService(db, Embeddings(ok: false));

        var r = await svc.PromoteClaimNoteAsync(claim.Id);

        Assert.Equal(PromoteNoteStatus.Promoted, r.Status);
        Assert.False(r.Embedded);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.Embedding);

        // Herstel-run: Ollama terug → embedding komt alsnog.
        var retry = await new ReviewNoteService(db, Embeddings(ok: true))
            .PromoteClaimNoteAsync(claim.Id);
        Assert.True(retry.Embedded);
        Assert.NotNull((await db.Corrections.SingleAsync()).Embedding);
    }

    [Fact]
    public async Task PromoteRelationNote_MaaktGeverifieerdeRuling_MetRelationRef()
    {
        using var db = NewDb();
        var relation = new Relation
        {
            FromRef = "mechanic:Deflect", ToRef = "section:core-rules-pdf/7.4",
            Kind = "wordt beperkt door", Explanation = "Deflect verwijst naar §7.4.",
            Provenance = "concept:combat", Trust = 0.8,
        };
        db.Relations.Add(relation);
        await db.SaveChangesAsync();
        var svc = new ReviewNoteService(db, Embeddings(ok: true));

        var r = await svc.PromoteRelationNoteAsync(relation.Id, Note);

        Assert.Equal(PromoteNoteStatus.Promoted, r.Status);
        Assert.Equal(Note, relation.ReviewNote);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("relation", correction.Scope);
        Assert.Equal(BrainRef.Relation(relation.Id).Format(), correction.Ref);
        // Het onderwerp (van → kind → naar) is herleidbaar in de vraagtekst.
        Assert.Contains("mechanic:Deflect", correction.Question);
        Assert.Contains("wordt beperkt door", correction.Question);
        Assert.Equal("verified", correction.Status);
        Assert.NotNull(correction.Embedding);
    }

    // --- testinfra (patroon ClaimMiningServiceTests) ----------------------

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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Echte EmbeddingService op een gestubde Ollama: ok = één
    /// embedding met de juiste dimensie, anders de 500 van het model-incident.</summary>
    private static EmbeddingService Embeddings(bool ok) => new(
        new HttpClient(new StubHandler(_ => ok
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        embeddings = new[]
                        {
                            Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray(),
                        },
                    }),
                    Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static async Task<Claim> SeedClaimAsync(RbRulesDbContext db, string? note)
    {
        var claim = new Claim
        {
            TopicType = "mechanic", TopicRef = "Deflect",
            Statement = "Deflect blokkeert ook abilities.",
            TrustScore = 0.6, OfficialStatus = "unclear", ReviewNote = note,
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return claim;
    }
}
