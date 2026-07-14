using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Errata-sync (#168, review-fix): EffectiveFrom komt van de bron
/// (UpdatedAt ?? PublishedAt, nooit geraden), en de reconciliatie behoudt
/// DetectedAt + Id voor ongewijzigde errata — DetectedAt betekent "voor het
/// eerst gezien", niet "laatste sync-moment". Zonder hub-document blijft de
/// bans-tak (met ExecuteDelete) uit, dus de errata-reconciliatie draait puur
/// getrackt op EF InMemory (transacties genegeerd).</summary>
public class BanErrataSyncServiceTests
{
    private const string SourceId = "origins-errata";
    private const string Url = "https://example.com/origins-errata";
    private const string CardName = "Adaptatron";

    private static string ErrataJson(string newText) =>
        $$"""[{"cardName": "{{CardName}}", "newText": "{{newText}}"}]""";

    [Fact]
    public async Task SyncAsync_SecondSyncUnchangedText_PreservesDetectedAtAndId()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var answer = ErrataJson("Vaste tekst.");
        var svc = new BanErrataSyncService(db, Ai(() => answer));

        await svc.SyncAsync();

        // Zet DetectedAt op een duidelijk-oude waarde zodat "onveranderd" hard
        // te bewijzen is (een churn zou hem naar ~nu resetten).
        var tracked = await db.Errata.SingleAsync();
        var firstId = tracked.Id;
        tracked.DetectedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        await svc.SyncAsync(); // exact dezelfde extractie

        var only = Assert.Single(await db.Errata.AsNoTracking().ToListAsync());
        Assert.Equal(firstId, only.Id); // geen churn: dezelfde rij
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), only.DetectedAt);
    }

    [Fact]
    public async Task SyncAsync_ChangedText_ReplacesRow()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var answer = ErrataJson("Eerste tekst.");
        var svc = new BanErrataSyncService(db, Ai(() => answer));

        await svc.SyncAsync();
        answer = ErrataJson("Herziene tekst.");
        await svc.SyncAsync();

        var only = Assert.Single(await db.Errata.AsNoTracking().ToListAsync());
        Assert.Equal("Herziene tekst.", only.NewText);
    }

    [Fact]
    public async Task SyncAsync_OrphanFromRemovedSource_IsDeleted()
    {
        using var db = NewDb();
        Seed(db);
        // Rest van een bron die niet meer geconfigureerd is (oude mirror).
        db.Errata.Add(new Erratum
        {
            CardName = "Oud", NewText = "Wees.", SourceUrl = "https://old-mirror.example/errata",
        });
        await db.SaveChangesAsync();
        var svc = new BanErrataSyncService(db, Ai(() => ErrataJson("Actueel.")));

        await svc.SyncAsync();

        var rows = await db.Errata.AsNoTracking().ToListAsync();
        Assert.Equal(Url, Assert.Single(rows).SourceUrl); // wees weg, actuele blijft
    }

    [Fact]
    public async Task SyncAsync_SourceHasUpdatedAt_ErratumGetsThatEffectiveFrom()
    {
        using var db = NewDb();
        Seed(db,
            publishedAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            updatedAt: new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var svc = new BanErrataSyncService(db, Ai(() => ErrataJson("Tekst.")));

        await svc.SyncAsync();

        Assert.Equal(new DateOnly(2026, 3, 15), (await db.Errata.SingleAsync()).EffectiveFrom);
    }

    [Fact]
    public async Task SyncAsync_SourceOnlyHasPublishedAt_FallsBack()
    {
        using var db = NewDb();
        Seed(db, publishedAt: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var svc = new BanErrataSyncService(db, Ai(() => ErrataJson("Tekst.")));

        await svc.SyncAsync();

        Assert.Equal(new DateOnly(2025, 6, 1), (await db.Errata.SingleAsync()).EffectiveFrom);
    }

    [Fact]
    public async Task SyncAsync_SourceHasNoDates_EffectiveFromStaysNull()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var svc = new BanErrataSyncService(db, Ai(() => ErrataJson("Tekst.")));

        await svc.SyncAsync();

        Assert.Null((await db.Errata.SingleAsync()).EffectiveFrom); // nooit geraden
    }

    // --- testinfra ---------------------------------------------------------

    private static void Seed(
        RbRulesDbContext db, DateTimeOffset? publishedAt = null, DateTimeOffset? updatedAt = null)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Origins Errata", Url = Url, Type = "official",
            TrustTier = 1, Rank = 100, Parser = "html", Cadence = "weekly",
            PublishedAt = publishedAt, UpdatedAt = updatedAt,
        });
        db.Documents.Add(new Document
        {
            SourceId = SourceId, Content = "pagina-inhoud", ContentHash = "hash1",
        });
    }

    private static RbAiClient Ai(Func<string> answer) => new(
        new HttpClient(new StubHandler(_ => Json(new { answer = answer() })))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(payload),
    };

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
            // InMemory kent geen transacties; SyncAsync draait er wel in
            // (Postgres) — negeren volstaat (CardSyncRepairTests-patroon).
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als de andere servicetests).</summary>
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
