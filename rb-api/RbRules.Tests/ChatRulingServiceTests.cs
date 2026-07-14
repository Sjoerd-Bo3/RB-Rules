using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>In-chat ruling vastleggen vanuit /ask (#166). De veiligheidskern:
/// autoriteit bepaalt de route — beheerder verifieert direct (verify-pad),
/// een ingelogde gebruiker legt alleen een voorstel vast (nooit verified,
/// nooit geëmbed hier). De centrale bewijs-test (AntiVergiftiging_*) toont
/// dat een gebruikersvoorstel het bestaande verified-filter van RulingsService
/// (het /rulings- en /ask-retrievalpad) niet passeert vóór goedkeuring.</summary>
public class ChatRulingServiceTests
{
    private const string Statement = "Deflect beschermt alléén tegen spells, niet tegen abilities.";
    private const string Source = "https://discord.com/channels/123/456/789";

    [Fact]
    public async Task Admin_MaaktDirectGeverifieerdeRulingMetEmbeddingEnBron()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "card", "Viktor", Source, "Wat doet Deflect tegen abilities?"),
            RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.Verified, r.Status);
        Assert.True(r.Embedded);
        Assert.False(r.Updated);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("card", correction.Scope);
        Assert.Equal("Viktor", correction.Ref);
        Assert.Equal(Statement, correction.Text);
        Assert.Equal(Source, correction.SourceRef);
        Assert.Equal("verified", correction.Status);
        Assert.NotNull(correction.VerifiedAt);
        Assert.NotNull(correction.Embedding);
        Assert.Equal("chat-ruling:admin", correction.Provenance);
    }

    [Fact]
    public async Task Gebruiker_LegtAlleenPendingVoorstelVast_NooitVerifiedOfGeembed()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "Wat doet Deflect?"),
            RulingAuthority.User);

        Assert.Equal(RulingSubmitStatus.Pending, r.Status);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("unverified", correction.Status);
        Assert.Null(correction.Embedding);
        Assert.Null(correction.VerifiedAt);
        Assert.Equal("chat-ruling:user", correction.Provenance);
        Assert.Equal(Source, correction.SourceRef);
    }

    [Fact]
    public async Task AntiVergiftiging_GebruikersVoorstelPasseertHetVerifiedFilterNiet()
    {
        // De kern-eis van #166: een niet-admin-inzending mag het /ask- en
        // /rulings-retrievalpad nooit vóór goedkeuring raken. RulingsService
        // (dezelfde productie-filter als /api/rulings en de rulings-channel
        // in AskService) filtert altijd op Status == "verified" — dit bewijst
        // dat een pending voorstel daar domweg niet doorheen komt.
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "Wat doet Deflect?"),
            RulingAuthority.User);

        Assert.Equal(0, await db.Corrections.CountAsync(c => c.Status == "verified"));
        var rulings = new RulingsService(db, Embeddings(ok: true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RulingsService>.Instance);
        var result = await rulings.QueryAsync(null, null, 1);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Admin_PromoveertBestaandPendingVoorstel_ZelfdeTekst_ÉénRij()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));
        await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "vraag"), RulingAuthority.User);

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "vraag"), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.Verified, r.Status);
        Assert.True(r.Updated);
        Assert.Single(await db.Corrections.ToListAsync());
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("verified", correction.Status);
        Assert.Equal("chat-ruling:admin", correction.Provenance);
    }

    [Fact]
    public async Task Gebruiker_DientIdentiekeAlGeverifieerdeTekstNogEensIn_GeenDubbeleRij()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));
        await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "vraag"), RulingAuthority.Admin);

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, "https://andere-bron.example/x", "vraag"),
            RulingAuthority.User);

        Assert.Equal(RulingSubmitStatus.Pending, r.Status);
        Assert.True(r.Updated);
        Assert.Single(await db.Corrections.ToListAsync());
        var correction = await db.Corrections.SingleAsync();
        // Blijft de geverifieerde ruling — een gebruiker mag een al-geldige
        // ruling niet stiekem terugzetten naar pending of de bron wijzigen.
        Assert.Equal("verified", correction.Status);
        Assert.Equal(Source, correction.SourceRef);
    }

    [Fact]
    public async Task TweeKeerIndienen_MetNieuweBron_WerktDezelfdeRulingBij()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));
        await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, "vraag"), RulingAuthority.Admin);

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, "https://tweede-bron.example/y", "nieuwe context"),
            RulingAuthority.Admin);

        Assert.True(r.Updated);
        Assert.Single(await db.Corrections.ToListAsync());
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("https://tweede-bron.example/y", correction.SourceRef);
        Assert.Equal("nieuwe context", correction.Question);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LegeUitspraak_GeeftInvalidInput(string statement)
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(statement, "answer", null, Source, null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.InvalidInput, r.Status);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task OntbrekendeBron_WordtGeweigerd_EenRulingZonderHerkomstIsGeenRuling()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, "  ", null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.InvalidInput, r.Status);
        Assert.Contains("bronverwijzing", r.Error);
    }

    [Fact]
    public async Task VerplichtVeldExplicietNull_GeeftInvalidInput_GeenException()
    {
        // System.Text.Json handhaaft de non-nullable string-velden van
        // RulingSubmit niet: een ontbrekend of expliciet-null JSON-veld bindt
        // naar C#-null. Zonder guard gooit de eerste .Trim() een
        // NullReferenceException → kale 500 i.p.v. de nette InvalidInput. Elk
        // van de drie verplichte velden apart null (niet alleen ""/whitespace).
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var scopeNull = await svc.SubmitAsync(
            new RulingSubmit(Statement, null!, null, Source, null), RulingAuthority.Admin);
        Assert.Equal(RulingSubmitStatus.InvalidInput, scopeNull.Status);

        var statementNull = await svc.SubmitAsync(
            new RulingSubmit(null!, "answer", null, Source, null), RulingAuthority.Admin);
        Assert.Equal(RulingSubmitStatus.InvalidInput, statementNull.Status);

        var sourceNull = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, null!, null), RulingAuthority.Admin);
        Assert.Equal(RulingSubmitStatus.InvalidInput, sourceNull.Status);
        Assert.Contains("bronverwijzing", sourceNull.Error);

        // Geen enkele null-inzending mag een rij hebben aangemaakt.
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task OnbekendeScope_GeeftInvalidInput()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "mechanic", "Deflect", Source, null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.InvalidInput, r.Status);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("rule_section")]
    public async Task KaartOfSectieScope_ZonderTopicRef_GeeftInvalidInput(string scope)
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, scope, null, Source, null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.InvalidInput, r.Status);
    }

    [Theory]
    [InlineData("http://insecure.example/x")]        // geen https
    [InlineData("https://192.168.1.10/x")]            // letterlijk IP, privé
    [InlineData("https://localhost/x")]               // localhost
    [InlineData("https://rb-v2-postgres/x")]          // interne netwerknaam
    public async Task GeblokkeerdeUrlAlsBron_WordtGeweigerd_UrlGuard(string url)
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, url, null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.InvalidInput, r.Status);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task VrijeCitatieAlsBron_GeenUrl_WordtGeaccepteerd()
    {
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: true));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null,
                "Discord #rulings, bevestigd door een moderator op 2026-05-01", null),
            RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.Verified, r.Status);
    }

    [Fact]
    public async Task Admin_EmbeddingUitval_VerifieertZonderEmbedding()
    {
        // Ollama tijdelijk weg (#100) is een verwacht pad: verificatie telt,
        // embedding komt bij een volgende her-indiening/verify.
        using var db = NewDb();
        var svc = new ChatRulingService(db, Embeddings(ok: false));

        var r = await svc.SubmitAsync(
            new RulingSubmit(Statement, "answer", null, Source, null), RulingAuthority.Admin);

        Assert.Equal(RulingSubmitStatus.Verified, r.Status);
        Assert.False(r.Embedded);
        var correction = await db.Corrections.SingleAsync();
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.Embedding);
    }

    // --- testinfra (patroon ReviewNoteServiceTests) ------------------------

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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

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
}
