using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Near-duplicaat-samenvoeging van bronnen (#175):
/// FeedCrawlService.MergeNearDuplicateSourcesAsync vangt bronnen die vóór
/// deze fix als aparte rijen bestonden maar alleen in URL-VORM verschillen
/// (trailing slash, http/https, www), hangt alle verwijzingen om naar de
/// winnaar (#144-patroon) en is idempotent. Bronnen met een LETTERLIJK
/// gelijke URL (zoals de Rules Hub-PDF/HTML-drieling in SourceSeed) blijven
/// bewust ongemoeid. Database is EF InMemory (servicetest-patroon);
/// transacties negeert die provider.</summary>
public class SourceNearDuplicateMergeTests
{
    [Fact]
    public async Task Merge_FormVariantsOfSameUrl_AreCollapsedIntoOneRow()
    {
        using var db = NewDb();
        db.Sources.Add(Src("http-vorm", "http://playriftbound.com/en-us/news/x", rank: 50));
        db.Sources.Add(Src("https-vorm", "https://playriftbound.com/en-us/news/x", rank: 50));
        await db.SaveChangesAsync();

        var merged = await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal(1, merged);
        Assert.Single(await db.Sources.ToListAsync());
        var log = await db.RunLogs.SingleAsync(l => l.Kind == FeedCrawlService.LedgerKind);
        Assert.Equal("ok", log.Status);
        Assert.Contains("1 near-duplicaat-bron(nen) samengevoegd", log.Detail);
    }

    [Fact]
    public async Task Merge_WinnerIsTheRowWithFeedId_RegardlessOfRank()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Feed", Url = "https://playriftbound.com/en-us/news/", Cadence = "daily",
        });
        // De handmatige rij heeft de hogere Rank, maar de feed-rij kent haar
        // herkomst al correct — die moet winnen (geen herkomst-info weggooien).
        db.Sources.Add(Src("handmatig", "https://www.playriftbound.com/en-us/news/x", rank: 100));
        db.Sources.Add(Src("via-feed", "https://playriftbound.com/en-us/news/x", rank: 10, feedId: "f1"));
        await db.SaveChangesAsync();

        await Service(db).MergeNearDuplicateSourcesAsync();

        var survivor = await db.Sources.SingleAsync();
        Assert.Equal("via-feed", survivor.Id);
        Assert.Equal("f1", survivor.FeedId);
    }

    [Fact]
    public async Task Merge_NoFeedIdEitherSide_HighestRankWins()
    {
        using var db = NewDb();
        db.Sources.Add(Src("laag", "https://www.riftbound.gg/judge-faq", rank: 40));
        db.Sources.Add(Src("hoog", "https://riftbound.gg/judge-faq", rank: 90));
        await db.SaveChangesAsync();

        await Service(db).MergeNearDuplicateSourcesAsync();

        var survivor = await db.Sources.SingleAsync();
        Assert.Equal("hoog", survivor.Id);
    }

    [Fact]
    public async Task Merge_RepointsDocumentsChangesAndRuleChunksBySourceId()
    {
        using var db = NewDb();
        var winner = Src("winnaar", "https://riftbound.gg/judge-faq", rank: 90);
        var loser = Src("verliezer", "https://www.riftbound.gg/judge-faq/", rank: 10);
        db.Sources.AddRange(winner, loser);
        var document = new Document { SourceId = "verliezer", Content = "tekst", ContentHash = "hash1" };
        db.Documents.Add(document);
        db.Changes.Add(new Change { SourceId = "verliezer", ChangeType = "editorial" });
        await db.SaveChangesAsync();
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = document.Id, SourceId = "verliezer", ChunkIndex = 0, Text = "chunk",
        });
        await db.SaveChangesAsync();

        var merged = await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal(1, merged);
        Assert.Equal("winnaar", (await db.Documents.SingleAsync()).SourceId);
        Assert.Equal("winnaar", (await db.Changes.SingleAsync()).SourceId);
        Assert.Equal("winnaar", (await db.RuleChunks.SingleAsync()).SourceId);
    }

    [Fact]
    public async Task Merge_RepointsClaimSourceAndConflictReferences()
    {
        using var db = NewDb();
        db.Sources.AddRange(
            Src("winnaar", "https://riftbound.gg/judge-faq", rank: 90),
            Src("verliezer", "https://www.riftbound.gg/judge-faq", rank: 10));
        var claim = new Claim { TopicType = "concept", TopicRef = "timing", Statement = "een claim" };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = "verliezer", Url = "https://www.riftbound.gg/judge-faq",
        });
        db.Conflicts.Add(new Conflict
        {
            Topic = "claim:concept:timing", Kind = "contradiction",
            SourceAId = "verliezer", SourceBId = "winnaar", WinnerSourceId = "verliezer",
        });
        await db.SaveChangesAsync();

        await Service(db).MergeNearDuplicateSourcesAsync();

        var cs = await db.ClaimSources.SingleAsync();
        Assert.Equal("winnaar", cs.SourceId);
        Assert.Equal("https://riftbound.gg/judge-faq", cs.Url);
        var conflict = await db.Conflicts.SingleAsync();
        Assert.Equal("winnaar", conflict.SourceAId);
        Assert.Equal("winnaar", conflict.SourceBId);
        Assert.Equal("winnaar", conflict.WinnerSourceId);
    }

    [Fact]
    public async Task Merge_ClaimSourceCollision_DropsDuplicateInsteadOfViolatingUniqueness()
    {
        using var db = NewDb();
        db.Sources.AddRange(
            Src("winnaar", "https://riftbound.gg/judge-faq", rank: 90),
            Src("verliezer", "https://www.riftbound.gg/judge-faq", rank: 10));
        var claim = new Claim { TopicType = "concept", TopicRef = "timing", Statement = "een claim" };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        // Beide bronnen droegen al bewijs voor dezelfde claim — na de merge
        // zou dat een dubbele (ClaimId, SourceId)-rij zijn.
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = "winnaar", Url = "https://riftbound.gg/judge-faq",
        });
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = "verliezer", Url = "https://www.riftbound.gg/judge-faq",
        });
        await db.SaveChangesAsync();

        await Service(db).MergeNearDuplicateSourcesAsync();

        var remaining = Assert.Single(await db.ClaimSources.ToListAsync());
        Assert.Equal("winnaar", remaining.SourceId);
    }

    [Fact]
    public async Task Merge_RepointsBanEntryErratumAndCorrectionByUrl()
    {
        using var db = NewDb();
        db.Sources.AddRange(
            Src("winnaar", "https://playriftbound.com/en-us/news/errata", rank: 90),
            Src("verliezer", "https://www.playriftbound.com/en-us/news/errata", rank: 10));
        db.BanEntries.Add(new BanEntry
        {
            Name = "Kaart X", Kind = "card",
            SourceUrl = "https://www.playriftbound.com/en-us/news/errata",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Kaart X", NewText = "Nieuwe tekst.",
            SourceUrl = "https://www.playriftbound.com/en-us/news/errata/", // trailing-slash-vorm
        });
        db.Corrections.Add(new Correction
        {
            Scope = "rule_section", Ref = "1.2", Text = "Ruling.",
            SourceRef = "https://www.playriftbound.com/en-us/news/errata",
        });
        await db.SaveChangesAsync();

        await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal("https://playriftbound.com/en-us/news/errata",
            (await db.BanEntries.SingleAsync()).SourceUrl);
        Assert.Equal("https://playriftbound.com/en-us/news/errata",
            (await db.Errata.SingleAsync()).SourceUrl);
        Assert.Equal("https://playriftbound.com/en-us/news/errata",
            (await db.Corrections.SingleAsync()).SourceRef);
    }

    [Fact]
    public async Task Merge_LiterallyIdenticalUrls_AreNotTouched()
    {
        // Regressietest: de Rules Hub-PDF/HTML-drieling in SourceSeed deelt
        // bewust dezelfde ontdek-pagina, elk met een eigen Parser — dat is
        // GEEN near-duplicaat en mag nooit samengevoegd worden.
        using var db = NewDb();
        const string hub = "https://playriftbound.com/en-us/rules-hub/";
        db.Sources.Add(Src("core-rules-pdf", hub, rank: 110, parser: "pdf"));
        db.Sources.Add(Src("tournament-rules-pdf", hub, rank: 105, parser: "pdf"));
        db.Sources.Add(Src("rules-hub", hub, rank: 100, parser: "html"));
        await db.SaveChangesAsync();

        var merged = await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal(0, merged);
        Assert.Equal(3, await db.Sources.CountAsync());
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    [Fact]
    public async Task Merge_IdenticalUrlPairAlongsideARealVariant_LeavesTheWholeGroupUntouched()
    {
        // Als een groep óók twee letterlijk-gelijke rijen bevat (het bewuste
        // ontwerp hierboven), mag een toevallige vorm-variant in dezelfde
        // groep die twee legitieme rijen niet alsnog laten samensmelten.
        using var db = NewDb();
        const string hub = "https://playriftbound.com/en-us/rules-hub/";
        db.Sources.Add(Src("core-rules-pdf", hub, rank: 110, parser: "pdf"));
        db.Sources.Add(Src("rules-hub", hub, rank: 100, parser: "html"));
        db.Sources.Add(Src("per-ongeluk-www", "https://www.playriftbound.com/en-us/rules-hub/", rank: 5));
        await db.SaveChangesAsync();

        var merged = await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal(0, merged);
        Assert.Equal(3, await db.Sources.CountAsync());
    }

    [Fact]
    public async Task Merge_NoNearDuplicates_ReturnsZeroAndLogsNothing()
    {
        using var db = NewDb();
        db.Sources.Add(Src("a", "https://riftbound.gg/judge-faq", rank: 50));
        db.Sources.Add(Src("b", "https://mobalytics.gg/riftbound/guides/banned-cards", rank: 40));
        await db.SaveChangesAsync();

        var merged = await Service(db).MergeNearDuplicateSourcesAsync();

        Assert.Equal(0, merged);
        Assert.Equal(2, await db.Sources.CountAsync());
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    [Fact]
    public async Task Merge_SecondRunMergesNothing()
    {
        using var db = NewDb();
        db.Sources.Add(Src("http-vorm", "http://playriftbound.com/en-us/news/x", rank: 50));
        db.Sources.Add(Src("https-vorm", "https://playriftbound.com/en-us/news/x", rank: 50));
        await db.SaveChangesAsync();

        var service = Service(db);
        var first = await service.MergeNearDuplicateSourcesAsync();
        var second = await service.MergeNearDuplicateSourcesAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(await db.RunLogs.ToListAsync()); // alleen de eerste run schrijft
    }

    private static Source Src(
        string id, string url, int rank, string? feedId = null, string parser = "html") => new()
    {
        Id = id, Name = id, Url = url, Type = "official", TrustTier = 1,
        Rank = rank, Parser = parser, Cadence = "weekly", FeedId = feedId,
    };

    private static FeedCrawlService Service(RbRulesDbContext db) =>
        new(db, new HttpClient()); // de merge doet geen netwerk-I/O

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
}
