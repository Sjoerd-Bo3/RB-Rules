using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>SSRF-guard (#45): de URL-regels (vóór de fetch) en het
/// IP-predicaat (ná DNS-resolutie, gebruikt door SafeExternalHttp).</summary>
public class UrlGuardTests
{
    [Theory]
    [InlineData("https://playriftbound.com/en-us/rules-hub/")]
    [InlineData("https://uvsgames.com/wp-content/uploads/2025/11/OGS-ProvingGrounds-HowtoPlay-EN.pdf")]
    [InlineData("https://example.com:8443/pad?x=1&y=2")]
    [InlineData("https://cmsassets.rgpub.io/sanity/files/abc123.pdf")]
    public void Check_AllowsPublicHttpsUrls(string url)
    {
        var r = UrlGuard.Check(url);
        Assert.True(r.Allowed, r.Reason);
    }

    [Fact]
    public void Check_AllowsAllSeedSources()
    {
        // De guard mag het bestaande register nooit breken.
        foreach (var s in SourceSeed.Defaults)
            Assert.True(UrlGuard.Check(s.Url).Allowed, $"{s.Id}: {UrlGuard.Check(s.Url).Reason}");
    }

    [Theory]
    [InlineData("http://playriftbound.com/")]
    [InlineData("ftp://example.com/rules.pdf")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    public void Check_RejectsNonHttpsSchemes(string url) =>
        Assert.False(UrlGuard.Check(url).Allowed);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geen url")]
    [InlineData("/relatief/pad")]
    [InlineData(null)]
    public void Check_RejectsUnparseableInput(string? url) =>
        Assert.False(UrlGuard.Check(url).Allowed);

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://192.168.1.1:8443/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")] // cloud-metadata
    [InlineData("https://8.8.8.8/")]                          // ook publieke letterlijke IP's
    [InlineData("https://[::1]/")]
    [InlineData("https://[fd00::1]/x")]
    public void Check_RejectsIpLiterals(string url) =>
        Assert.False(UrlGuard.Check(url).Allowed);

    [Theory]
    [InlineData("https://localhost/")]
    [InlineData("https://sub.localhost/")]
    [InlineData("https://rb-v2-postgres/")]  // compose-interne servicenaam
    [InlineData("https://neo4j:7474/")]
    [InlineData("https://printer.local/")]
    [InlineData("https://vault.internal/")]
    [InlineData("https://nas.home.arpa/")]
    public void Check_RejectsLocalAndInternalHostnames(string url) =>
        Assert.False(UrlGuard.Check(url).Allowed);

    [Fact]
    public void Check_RejectsUserInfo() =>
        // "https://playriftbound.com@evil.example/" — de klassieke verwarringstruc.
        Assert.False(UrlGuard.Check("https://playriftbound.com@evil.example/").Allowed);

    [Fact]
    public void Check_GivesReadableReason()
    {
        var r = UrlGuard.Check("http://example.com/");
        Assert.False(r.Allowed);
        Assert.Contains("https", r.Reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]         // loopback
    [InlineData("10.1.2.3")]          // privé /8
    [InlineData("172.16.0.1")]        // privé /12 ondergrens (docker)
    [InlineData("172.31.255.255")]    // privé /12 bovengrens
    [InlineData("192.168.1.1")]       // privé /16
    [InlineData("169.254.169.254")]   // link-local / cloud-metadata
    [InlineData("100.64.0.1")]        // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("198.18.0.1")]        // benchmark
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("255.255.255.255")]   // broadcast
    [InlineData("::1")]               // IPv6 loopback
    [InlineData("::")]                // IPv6 unspecified
    [InlineData("fe80::1")]           // IPv6 link-local
    [InlineData("fd12:3456::1")]      // IPv6 ULA
    [InlineData("ff02::1")]           // IPv6 multicast
    [InlineData("64:ff9b::a00:1")]    // NAT64 rond 10.0.0.1
    [InlineData("::ffff:10.0.0.1")]   // IPv4-mapped privé
    public void IsBlockedIp_BlocksPrivateAndSpecialRanges(string ip) =>
        Assert.True(UrlGuard.IsBlockedIp(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]        // nét buiten 172.16/12
    [InlineData("172.15.255.255")]    // nét onder 172.16/12
    [InlineData("100.128.0.1")]       // nét buiten CGNAT /10
    [InlineData("2606:4700::1111")]   // publiek IPv6 (Cloudflare)
    [InlineData("::ffff:8.8.8.8")]    // IPv4-mapped publiek
    public void IsBlockedIp_AllowsPublicAddresses(string ip) =>
        Assert.False(UrlGuard.IsBlockedIp(IPAddress.Parse(ip)));
}

/// <summary>Fetch-randen van de scan (#45): een geweigerde URL wordt een
/// gewone error-IngestResult — géén HTTP-verkeer, en de rest van de run
/// draait door (fouten zijn data).</summary>
public class IngestSsrfGuardTests
{
    [Fact]
    public async Task ScanAsync_BlockedSourceUrl_GeeftError_ZonderFetch()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "kwaadaardig", Name = "Interne dienst", Url = "https://rb-v2-postgres/",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();

        var fetched = false;
        var svc = NewIngest(db, _ => { fetched = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var results = await svc.ScanAsync(onlyDue: false);

        var r = Assert.Single(results);
        Assert.Equal("error", r.Status);
        Assert.Contains("SSRF-guard", r.Detail);
        Assert.False(fetched, "een geblokkeerde URL mag nooit gefetcht worden");
    }

    [Fact]
    public async Task ScanAsync_BlockedDiscoveredPdfUrl_GeeftError_EnDownloadtNiet()
    {
        // De PDF-link komt uit opgehaalde hub-HTML en is dus externe invoer:
        // een (gemanipuleerde) link naar een intern adres mag de ontdek-stap
        // wél loggen maar nooit een download starten.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-pdf", Name = "Core Rules PDF", Url = "https://example.com/hub/",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "daily",
        });
        await db.SaveChangesAsync();

        var pdfFetched = false;
        var svc = NewIngest(db, req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".pdf")) pdfFetched = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<a href="https://10.0.0.7/core-rules.pdf">Core Rules</a>""",
                    System.Text.Encoding.UTF8, "text/html"),
            };
        });

        var results = await svc.ScanAsync(onlyDue: false);

        var r = Assert.Single(results);
        Assert.Equal("error", r.Status);
        Assert.Contains("SSRF-guard", r.Detail);
        Assert.False(pdfFetched, "een geblokkeerde PDF-URL mag nooit gedownload worden");
    }

    private static IngestService NewIngest(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var ai = new RbAiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
        var embeddings = new EmbeddingService(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("http://ollama.test") });
        return new IngestService(
            db, new HttpClient(new StubHandler(respond)), ai,
            new ChangeClassificationService(db, ai),
            // Kennis-hertoets (#119): zonder changes in het venster doet de
            // scan-afronding niets — deze tests kijken alleen naar de guard.
            new KnowledgeRecheckService(db, new ClaimMiningService(db, ai, embeddings)),
            // Feed-crawl (#167): geen SourceFeeds in deze db ⇒ "geen feeds aan
            // de beurt" zonder een enkele HTTP-call — de stub-respons wordt
            // dus nooit aangesproken, alleen het type moet kloppen.
            new FeedCrawlService(db, new HttpClient(new StubHandler(respond))));
    }

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

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als ClaimMiningServiceTests).</summary>
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
