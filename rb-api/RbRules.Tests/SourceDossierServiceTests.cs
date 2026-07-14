using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Bron-dossier (#171, spiegelbeeld van #167): herkomst (FeedId →
/// feednaam of handmatig), opbrengst via de twee koppelvormen (SourceId voor
/// Document/RuleChunk/Change, SourceUrl — genormaliseerd — voor
/// BanEntry/Erratum/Correction, plus Claim via de directe ClaimSource-FK) en
/// het compleetheidssignaal uit run_log. Database is EF InMemory
/// (FeedCrawlServiceTests-patroon).</summary>
public class SourceDossierServiceTests
{
    private const string SourceUrl = "https://playriftbound.com/en-us/news/errata/patch-1";

    [Fact]
    public async Task GetAsync_OnbekendeBron_GeeftNull()
    {
        using var db = NewDb();
        var dossier = await new SourceDossierService(db).GetAsync("onbekend");
        Assert.Null(dossier);
    }

    [Fact]
    public async Task GetAsync_Herkomst_MetFeedId_ToontFeednaam()
    {
        using var db = NewDb();
        db.SourceFeeds.Add(new SourceFeed
        {
            Id = "f1", Name = "Rules & Releases", Url = "https://playriftbound.com/news/", Cadence = "daily",
        });
        db.Sources.Add(Source("s1", feedId: "f1"));
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.NotNull(dossier);
        Assert.Equal("f1", dossier!.Origin.FeedId);
        Assert.Equal("Rules & Releases", dossier.Origin.FeedName);
    }

    [Fact]
    public async Task GetAsync_Herkomst_ZonderFeedId_Handmatig()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Null(dossier!.Origin.FeedId);
        Assert.Null(dossier.Origin.FeedName);
    }

    [Fact]
    public async Task GetAsync_Opbrengst_SourceIdKoppelvorm_TeltDocumentenChunksEnChanges()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.Documents.Add(new Document
        {
            SourceId = "s1", Content = "tekst", ContentHash = "h1",
            RetrievedAt = DateTimeOffset.UtcNow,
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = "s1", ChunkIndex = 0, Text = "§ 1.1 ...",
        });
        db.Changes.Add(new Change
        {
            SourceId = "s1", ChangeType = "errata", Severity = "medium", Summary = "kaart X aangepast",
        });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(1, dossier!.Yield.Documents);
        Assert.NotNull(dossier.Yield.LastDocumentAt);
        Assert.Equal(1, dossier.Yield.RuleChunks);
        Assert.Equal(1, dossier.Yield.ChangesTotal);
        Assert.Equal("kaart X aangepast", Assert.Single(dossier.Yield.Changes).Summary);
    }

    [Fact]
    public async Task GetAsync_Opbrengst_SourceUrlKoppelvorm_MatchtGenormaliseerdeUrl()
    {
        using var db = NewDb();
        // Bron-URL zonder trailing slash; de opgeslagen ban/erratum/ruling
        // dragen 'm mét — de normalisatie (#171: "match op Source.Url,
        // genormaliseerd") moet dat verschil overbruggen.
        db.Sources.Add(Source("s1", url: SourceUrl));
        db.BanEntries.Add(new BanEntry
        {
            Name = "Verboden Kaart", Kind = "card", SourceUrl = SourceUrl + "/",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Aangepaste Kaart", NewText = "nieuwe tekst", SourceUrl = SourceUrl + "/",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "card", Ref = "Aangepaste Kaart", Text = "geverifieerde uitleg",
            SourceRef = SourceUrl, Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        // Andere bron/URL: mag niet meetellen.
        db.Sources.Add(Source("s2", url: "https://playriftbound.com/news/anders"));
        db.BanEntries.Add(new BanEntry
        {
            Name = "Andere Kaart", Kind = "card",
            SourceUrl = "https://playriftbound.com/news/anders",
        });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(1, dossier!.Yield.BansTotal);
        Assert.Equal("Verboden Kaart", Assert.Single(dossier.Yield.Bans).Name);
        Assert.Equal(1, dossier.Yield.ErrataTotal);
        Assert.Equal(1, dossier.Yield.RulingsTotal);
        Assert.Equal("geverifieerde uitleg", Assert.Single(dossier.Yield.Rulings).Text);
    }

    [Fact]
    public async Task GetAsync_Opbrengst_Claims_ViaClaimSourceFk_NietViaUrl()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1", trustTier: 3));
        var claim = new Claim
        {
            TopicType = "card", TopicRef = "Testkaart", Statement = "werkt zo",
            Status = "accepted",
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = "s1", Url = "https://community.example/thread",
        });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(1, dossier!.Yield.ClaimsTotal);
        Assert.Equal("werkt zo", Assert.Single(dossier.Yield.Claims).Statement);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_NooitGescand_ZonderScanRegel()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Null(dossier!.Processing.LastScan);
        Assert.Equal(SourceDossierCompleteness.NooitGescand, dossier.Processing.CompletenessStatus);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_ScanOkMetOpbrengst_Volledig()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.Changes.Add(new Change { SourceId = "s1", ChangeType = "errata", Severity = "low" });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "changed" });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal("changed", dossier!.Processing.LastScan!.Status);
        Assert.Equal(SourceDossierCompleteness.Volledig, dossier.Processing.CompletenessStatus);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_LaatsteScanMislukt_Onvolledig()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "error", Detail = "HTTP 500" });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(SourceDossierCompleteness.Onvolledig, dossier!.Processing.CompletenessStatus);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_ClassifyFoutOpChangeVanDezeBron_Onvolledig()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        var change = new Change { SourceId = "s1", ChangeType = "unknown", Severity = "medium" };
        db.Changes.Add(change);
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "changed" });
        await db.SaveChangesAsync();
        db.RunLogs.Add(new RunLog
        {
            Kind = "classify", Ref = $"change:{change.Id}", Status = "error",
            Detail = "rb-ai niet beschikbaar",
        });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(SourceDossierCompleteness.Onvolledig, dossier!.Processing.CompletenessStatus);
        var step = Assert.Single(dossier.Processing.FollowUps, s => s.Kind == "classify");
        Assert.Equal("error", step.Status);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_CommunityBronDocumentNogNietGemined_Onvolledig()
    {
        using var db = NewDb();
        // Claims-mining geldt alleen voor trust ≥ 3 (ClaimMiningService-poort)
        // — deze bron heeft een document dat nog niet gemined is: "bezig".
        db.Sources.Add(Source("s1", trustTier: 3));
        db.Documents.Add(new Document
        {
            SourceId = "s1", Content = "tekst", ContentHash = "h1", ClaimsMinedAt = null,
        });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(SourceDossierCompleteness.Onvolledig, dossier!.Processing.CompletenessStatus);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_OfficieleBronZonderClaimsMining_TeltNietAlsPending()
    {
        using var db = NewDb();
        // Trust 1 (officieel): claims-mining is hier per ontwerp nooit aan de
        // orde (ClaimMiningService filtert op trust ≥ 3) — geen document
        // zonder ClaimsMinedAt mag dus "onvolledig" opleveren.
        db.Sources.Add(Source("s1", trustTier: 1));
        db.Documents.Add(new Document
        {
            SourceId = "s1", Content = "regels", ContentHash = "h1", ClaimsMinedAt = null,
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = "s1", ChunkIndex = 0, Text = "§ 1.1 ...",
        });
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(SourceDossierCompleteness.Volledig, dossier!.Processing.CompletenessStatus);
    }

    [Fact]
    public async Task GetAsync_Verwerkingsstatus_ScanOkNietsOpgeleverd_Leeg()
    {
        using var db = NewDb();
        db.Sources.Add(Source("s1"));
        db.RunLogs.Add(new RunLog { Kind = "scan", Ref = "s1", Status = "unchanged" });
        await db.SaveChangesAsync();

        var dossier = await new SourceDossierService(db).GetAsync("s1");

        Assert.Equal(SourceDossierCompleteness.Leeg, dossier!.Processing.CompletenessStatus);
    }

    // --- testinfra -------------------------------------------------------

    private static Source Source(
        string id, string? feedId = null, string? url = null, short trustTier = 1) => new()
    {
        Id = id, Name = id, Url = url ?? $"https://playriftbound.com/news/{id}",
        Type = "official", TrustTier = trustTier, Parser = "html", Cadence = "daily",
        FeedId = feedId,
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de AskService-tests).</summary>
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
