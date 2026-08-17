using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Strip-versionering + stille rebaseline (#205-review, findings
/// 1/3): elke wijziging aan TextUtils.StripBoilerplate verandert de gestripte
/// tekst — en dus de hash — van élke bron tegelijk. Zonder versionering zou
/// dat één golf junk-"changes" over het hele register geven (de diff toont
/// alleen de weggevallen boilerplate) die bovendien de #205-Vendetta-backfill
/// permanent kon blokkeren (junk-Change → "unknown" → telt als
/// niet-editorial → guard weigert voorgoed). Een bron met een verouderde
/// (of null) Source.StripVersion rebaselinet stil: nieuwe baseline zonder
/// diff/Change, met de one-shot-candidacy (#205) er direct achteraan.</summary>
public class IngestServiceRebaselineTests
{
    private const string Url = "https://playriftbound.com/en-us/news/announcements/artikel";

    // De "oude" baseline simuleert een v1-strip: de Related Articles-carousel
    // zat toen nog in de gestripte tekst, dus de tekst (en hash) wijken af
    // van wat de actuele strip (v2) oplevert voor dezelfde rauwe HTML.
    private const string ArticleText = "Legion is a dependent keyword.";
    private const string OldStripText =
        ArticleText + " Related Articles July Ban List Updates";

    [Fact]
    public async Task ScanAsync_VerouderdeStripVersie_RebaselinetZonderChange()
    {
        using var db = NewDb();
        var oldHash = TextUtils.Sha256(OldStripText);
        db.Sources.Add(Source("s1", lastHash: oldHash, stripVersion: null));
        db.Documents.Add(new Document
        {
            SourceId = "s1", Content = OldStripText, ContentHash = oldHash,
            ClaimsMinedAt = DateTimeOffset.UtcNow.AddDays(-3),
            ClarifiedAt = DateTimeOffset.UtcNow.AddDays(-3),
            RetrievedAt = DateTimeOffset.UtcNow.AddDays(-3),
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => HtmlWithCarousel(ArticleText));

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("unchanged", result.Status);
        Assert.Contains($"boilerplate-rebaseline v{TextUtils.BoilerplateVersion}", result.Detail);
        Assert.Empty(await db.Changes.ToListAsync()); // stil: geen junk-Change
        var src = await db.Sources.SingleAsync();
        Assert.Equal(TextUtils.BoilerplateVersion, src.StripVersion);
        Assert.NotEqual(oldHash, src.LastHash); // nieuwe baseline-hash
        Assert.Null(src.UpdatedAt); // geen echte contentwijziging gezien

        // Verse baseline-rij, mét meegereisde mining-markers: inhoudelijk is
        // dit hetzelfde artikel (alleen boilerplate weg) — een verse
        // claims-/clarify-mine zou alleen dezelfde LLM-kosten herhalen.
        var docs = await db.Documents.OrderBy(d => d.RetrievedAt).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.DoesNotContain("Related Articles", docs[1].Content);
        Assert.NotNull(docs[1].ClaimsMinedAt);
        Assert.NotNull(docs[1].ClarifiedAt);
    }

    [Fact]
    public async Task ScanAsync_RebaselineVanPatchNotesBronZonderChange_MintOneShotInDezelfdeScan()
    {
        // De Vendetta-backfill mag niet op de rebaseline stranden: haar
        // allereerste post-deploy-scan is meteen ook haar rebaseline, en de
        // one-shot-candidacy (#205) draait op dat pad gewoon door.
        using var db = NewDb();
        var oldHash = TextUtils.Sha256(OldStripText);
        db.Sources.Add(Source("core-rules-vendetta-patch-notes",
            lastHash: oldHash, stripVersion: null, contentKind: SourceContentKind.PatchNotes));
        db.Documents.Add(new Document
        {
            SourceId = "core-rules-vendetta-patch-notes",
            Content = OldStripText, ContentHash = oldHash,
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => HtmlWithCarousel(ArticleText));

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("changed", result.Status);
        Assert.Contains("boilerplate-rebaseline", result.Detail);
        Assert.Contains("one-shot", result.Detail);
        var change = await db.Changes.SingleAsync();
        Assert.Contains("Legion is a dependent keyword", change.Diff!);
        // De one-shot-delta is de SCHONE tekst — geen carousel-junk erin.
        Assert.DoesNotContain("Related Articles", change.Diff);
    }

    [Fact]
    public async Task ScanAsync_RebaselineVanGewoneBron_AlleenRebaseline()
    {
        // Een niet-patch-notes-bron krijgt op het rebaseline-pad nooit een
        // Change — ook niet via de one-shot-tak (kind-poort).
        using var db = NewDb();
        var oldHash = TextUtils.Sha256(OldStripText);
        db.Sources.Add(Source("s1", lastHash: oldHash, stripVersion: null,
            contentKind: SourceContentKind.Other));
        db.Documents.Add(new Document { SourceId = "s1", Content = OldStripText, ContentHash = oldHash });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => HtmlWithCarousel(ArticleText));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("unchanged", Assert.Single(results).Status);
        Assert.Empty(await db.Changes.ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_StripOngewijzigdVoorDezeBron_AlleenVersieBijgewerkt()
    {
        // De strip-wijziging raakt niet elke bron (geen carousel in de HTML):
        // hash blijft gelijk — geen nieuwe Document-rij nodig, alleen de
        // versie-administratie bijwerken.
        using var db = NewDb();
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(Html(ArticleText).RawContent));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(Source("s1", lastHash: hash, stripVersion: null,
            contentKind: SourceContentKind.Other));
        db.Documents.Add(new Document { SourceId = "s1", Content = text, ContentHash = hash });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html(ArticleText).Response);

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("unchanged", result.Status);
        Assert.Contains("boilerplate-rebaseline", result.Detail);
        var src = await db.Sources.SingleAsync();
        Assert.Equal(TextUtils.BoilerplateVersion, src.StripVersion);
        Assert.Equal(hash, src.LastHash);
        Assert.Single(await db.Documents.ToListAsync()); // geen dubbele rij
    }

    [Fact]
    public async Task ScanAsync_EchteWijzigingNaRebaseline_GeeftGewoonChangeBijVolgendeScan()
    {
        // Gedocumenteerd twee-scans-gedrag (#205-review): een échte
        // inhoudelijke wijziging rond de versie-bump komt via "rebaseline
        // eerst, diff daarna" binnen. (Een wijziging die exact in het ene
        // rebaseline-venster valt wordt in de baseline geabsorbeerd —
        // hash-only kan strip-ruis niet van echte delta scheiden binnen één
        // scan; dat venster is één scan-tick.)
        using var db = NewDb();
        var oldHash = TextUtils.Sha256(OldStripText);
        db.Sources.Add(Source("s1", lastHash: oldHash, stripVersion: null,
            contentKind: SourceContentKind.Other));
        db.Documents.Add(new Document { SourceId = "s1", Content = OldStripText, ContentHash = oldHash });
        await db.SaveChangesAsync();
        var content = ArticleText;
        var svc = NewIngest(db, _ => HtmlWithCarousel(content));

        var first = await svc.ScanAsync(onlyDue: false); // rebaseline, geen Change
        Assert.Equal("unchanged", Assert.Single(first).Status);
        Assert.Empty(await db.Changes.ToListAsync());

        content = ArticleText + " NEW: Legion now also triggers on tokens.";
        var second = await svc.ScanAsync(onlyDue: false); // reguliere diff-scan

        Assert.Equal("changed", Assert.Single(second).Status);
        var change = await db.Changes.SingleAsync();
        Assert.Contains("triggers on tokens", change.Diff!);
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanNieuweBron_ZetStripVersie()
    {
        // Een gloednieuwe bron is geen rebaseline (er is nog geen baseline) —
        // de normale eerste scan zet de actuele versie meteen goed.
        using var db = NewDb();
        db.Sources.Add(Source("s1", lastHash: null, stripVersion: null,
            contentKind: SourceContentKind.Other));
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html(ArticleText).Response);

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var src = await db.Sources.SingleAsync();
        Assert.Equal(TextUtils.BoilerplateVersion, src.StripVersion);
    }

    [Fact]
    public async Task ScanAsync_ActueleStripVersie_GeenRebaseline()
    {
        // Al op de actuele versie ⇒ het gewone pad (hier: unchanged), geen
        // rebaseline-detail en geen extra Document-rij.
        using var db = NewDb();
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(Html(ArticleText).RawContent));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(Source("s1", lastHash: hash, stripVersion: TextUtils.BoilerplateVersion,
            contentKind: SourceContentKind.Other));
        db.Documents.Add(new Document { SourceId = "s1", Content = text, ContentHash = hash });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html(ArticleText).Response);

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("unchanged", result.Status);
        Assert.Null(result.Detail);
        Assert.Single(await db.Documents.ToListAsync());
    }

    // --- testinfra (IngestServiceUpdatedAtTests-patroon) ------------------

    private static Source Source(
        string id, string? lastHash, int? stripVersion,
        string contentKind = SourceContentKind.Other) => new()
    {
        Id = id, Name = id, Url = Url,
        Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        ContentKind = contentKind, ContentKindSource = SourceContentKind.LlmOrigin,
        LastHash = lastHash, StripVersion = stripVersion,
    };

    private static (HttpResponseMessage Response, string RawContent) Html(string text)
    {
        var raw = $"<html><body><p>{text}</p></body></html>";
        return (new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) }, raw);
    }

    /// <summary>Rauwe HTML mét de Related Articles-carousel — de actuele
    /// strip (v2) haalt die weg; de gesimuleerde v1-baseline
    /// (<see cref="OldStripText"/>) had de carousel-tekst nog wél.</summary>
    private static HttpResponseMessage HtmlWithCarousel(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<html><body><p>{text}</p>"
                + "<section data-testid=\"article-card-carousel\" id=\"related-articles\" class=\"blade\">"
                + "<h2>Related Articles</h2>"
                + "<a href=\"/en-us/news/announcements/july-ban-list-updates\">July Ban List Updates</a>"
                + "</section></body></html>"),
        };

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
            new KnowledgeRecheckService(db, new ClaimMiningService(db, ai, embeddings)),
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
