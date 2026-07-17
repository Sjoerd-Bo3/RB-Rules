using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#177: een FAQ-/clarificatie-artikel had bij zijn allereerste scan
/// (isNew — er is nog geen vorige versie om te diffen) geen Change-item, dus
/// de aankomst bleef onzichtbaar in de wijzigingen-feed (productie-bug: "0
/// changes" voor de Unleashed Rules FAQ). Deze tests dekken de sjabloon-
/// change die ScanOneAsync nu bij aankomst toevoegt, alleen voor officiële
/// (TrustTier 1) bronnen die matchen op ClarificationSources.IsMatch — een
/// gewone nieuwe bron blijft ongemoeid (bestaand gedrag,
/// IngestServiceUpdatedAtTests).
///
/// #185 gaf patch-notes-bronnen bij hun eerste scan bewust GEEN Change (hun
/// duiding kwam via de normale tweede-scan-diff). #205 herzag dat: een
/// per-set patch-notes-artikel (Vendetta) is ONE-SHOT — het verandert na
/// publicatie nooit meer, dus die tweede-scan-diff komt er nooit. Zie
/// ScanAsync_EersteScanVanPatchNotesBron_MaaktInhoudelijkeChange en de
/// backfill-/guard-tests onderaan dit bestand.</summary>
public class IngestServiceClarificationChangeTests
{
    private const string FaqUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    [Fact]
    public async Task ScanAsync_EersteScanVanFaqBron_MaaktClarificationChange()
    {
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "playriftbound-com-unleashed-rules-faq-and-clarifications",
            Name = "Unleashed Rules FAQ and Clarifications", Url = FaqUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion betekent finalizen op de chain."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var change = await db.Changes.SingleAsync();
        Assert.Equal("clarification", change.ChangeType);
        Assert.Contains("FAQ-/clarificatie-artikel", change.Summary);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt); // zichtbaar als "laatst bijgewerkt"
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanGewoneBron_MaaktGeenChange()
    {
        // Bestaand gedrag (IngestServiceUpdatedAtTests) blijft ongemoeid: een
        // bron zonder faq/clarification-signaal krijgt bij isNew nog steeds
        // geen Change — er is immers niets om te diffen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "s1", Name = "Bron", Url = "https://example.com/regels",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Eerste versie van de regeltekst."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        Assert.Empty(await db.Changes.ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanPatchNotesBron_MaaktInhoudelijkeChange()
    {
        // #205: een patch-notes-bron is vaak one-shot (een per-set-artikel
        // zoals Vendetta verandert na publicatie nooit meer) — de #185-
        // beslissing om de eerste scan zonder Change te laten, liet zo'n
        // artikel PERMANENT zonder duiding. De volledige inhoud is nu de
        // delta (lege "voor"-versie): dezelfde classificatiepijplijn als een
        // echte diff, dus ChangeType komt uit de classifier (hier "unknown"
        // — de AI-stub in dit testbestand faalt bewust, #58-degradatiepad),
        // niet het oude hardcoded "clarification"-sjabloon.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion is a dependent keyword."));

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("new", Assert.Single(results).Status);
        var change = await db.Changes.SingleAsync();
        Assert.NotEqual("clarification", change.ChangeType); // geen #177-sjabloon
        Assert.Equal("unknown", change.ChangeType); // AI down in de stub → #58-fallback
        Assert.NotNull(change.Diff);
        Assert.Contains("Legion is a dependent keyword", change.Diff);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt);
    }

    [Fact]
    public async Task ScanAsync_TweedeScanVanPatchNotesBron_MaaktGewoneDiffChange()
    {
        // Een TERUGKERENDE patch-notes-pagina (core-rules-patch-notes, i.t.t.
        // een one-shot per-set-artikel) krijgt sinds #205 ook bij haar eerste
        // scan al een Change (de one-shot-tak) — daarna blijft haar gedrag
        // exact zoals vóór #205: de tweede scan levert een gewone diff-Change
        // op via de normale ingest-diff (IngestServiceUpdatedAtTests.
        // ScanAsync_RealContentChange_SetsUpdatedAt), en de one-shot-guard
        // vuurt niet opnieuw omdat de bron dan al een niet-editoriale Change
        // heeft (de eerste-scan-Change zelf).
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-patch-notes", Name = "Core Rules Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var content = "Legion is a dependent keyword.";
        var svc = NewIngest(db, _ => Html(content));
        await svc.ScanAsync(onlyDue: false); // eerste fetch: "new" + one-shot Change (#205)

        content = "Legion is a dependent keyword. CLARIFIED: activated abilities with Legion trigger only once per turn.";
        var results = await svc.ScanAsync(onlyDue: false); // échte wijziging

        Assert.Equal("changed", Assert.Single(results).Status);
        // Twee Changes nu: de one-shot uit de eerste scan + de diff uit de tweede.
        var changes = await db.Changes.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, changes.Count);
        var diffChange = changes[1];
        Assert.NotEqual("clarification", diffChange.ChangeType);
        Assert.NotNull(diffChange.Diff);
        Assert.Contains("CLARIFIED", diffChange.Diff);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt);
    }

    [Fact]
    public async Task ScanAsync_BackfillVanBestaandeVendettaBron_MaaktAlsnogChange()
    {
        // #205 backfill-scenario zoals het in productie ligt: de bestaande
        // Vendetta-bron werd vóór deze fix al gescand (een Document staat er
        // al, ContentKind is al "patch-notes" via een eerdere
        // LLM-classificatie, StripVersion is nog null — van vóór de
        // strip-versionering) maar heeft nog GEEN inhoudelijke Change. De
        // eerstvolgende post-deploy-scan is dan meteen ook haar rebaseline
        // (#205-review): de one-shot-candidacy draait óók op dat pad, dus de
        // backfill vuurt gewoon in dezelfde scan.
        using var db = NewDb();
        var raw = RawHtml("Legion is a dependent keyword.");
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.PatchNotes, ContentKindSource = SourceContentKind.LlmOrigin,
            LastHash = hash, LastChecked = DateTimeOffset.UtcNow.AddDays(-7),
            StripVersion = null, // productie-stand: rij van vóór de strip-versionering
        });
        db.Documents.Add(new Document
        {
            SourceId = "core-rules-vendetta-patch-notes", Content = text, ContentHash = hash,
            RetrievedAt = DateTimeOffset.UtcNow.AddDays(-7),
        });
        await db.SaveChangesAsync();
        // Geen Change op deze bron — dat is precies het bug-scenario.
        Assert.Empty(await db.Changes.ToListAsync());
        var svc = NewIngest(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) });

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("changed", result.Status);
        Assert.Contains("backfill", result.Detail);
        var change = await db.Changes.SingleAsync();
        Assert.NotNull(change.Diff);
        Assert.Contains("Legion is a dependent keyword", change.Diff);
        var src = await db.Sources.SingleAsync();
        Assert.NotNull(src.UpdatedAt);
        // De hash blijft ongewijzigd (de inhoud IS ongewijzigd) — alleen de
        // Change is nieuw, en de rebaseline heeft de strip-versie gezet.
        Assert.Equal(hash, src.LastHash);
        Assert.Equal(TextUtils.BoilerplateVersion, src.StripVersion);
        // #205-review: het memo is geschreven — nooit een tweede poging.
        Assert.Single(await db.RunLogs
            .Where(l => l.Kind == PatchNotesOneShotChange.LedgerKind && l.Ref == src.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_BackfillViaUnchangedPad_MaaktAlsnogChange()
    {
        // Zelfde backfill, maar dan bij een bron die al op de actuele
        // strip-versie staat (bv. na een content-kind-correctie naar
        // patch-notes ná de rebaseline): de hash blijft exact gelijk aan
        // LastHash (vroege "unchanged"-return) — de candidacy-check op dat
        // pad vangt hem alsnog.
        using var db = NewDb();
        var raw = RawHtml("Legion is a dependent keyword.");
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.PatchNotes, ContentKindSource = SourceContentKind.LlmOrigin,
            LastHash = hash, StripVersion = TextUtils.BoilerplateVersion,
        });
        db.Documents.Add(new Document
        {
            SourceId = "core-rules-vendetta-patch-notes", Content = text, ContentHash = hash,
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) });

        var results = await svc.ScanAsync(onlyDue: false);

        var result = Assert.Single(results);
        Assert.Equal("changed", result.Status);
        Assert.Contains("backfill", result.Detail);
        var change = await db.Changes.SingleAsync();
        Assert.Contains("Legion is a dependent keyword", change.Diff!);
    }

    [Fact]
    public async Task ScanAsync_PatchNotesBronMetBestaandeNietEditorialeChange_HerhaaltOneShotNiet()
    {
        // Guard tegen dubbelwerk: heeft de bron al EEN niet-editoriale
        // Change (ongeacht of die uit een normale diff of een eerdere
        // one-shot kwam), dan vuurt de one-shot-tak niet opnieuw — ook niet
        // als de hash toevallig weer exact hetzelfde is. StripVersion staat
        // hier al op de actuele versie zodat de test het unchanged-pad
        // isoleert (de rebaseline-variant test hetzelfde via haar eigen
        // candidacy-aanroep).
        using var db = NewDb();
        var raw = RawHtml("Legion is a dependent keyword.");
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.PatchNotes, ContentKindSource = SourceContentKind.LlmOrigin,
            LastHash = hash, StripVersion = TextUtils.BoilerplateVersion,
        });
        db.Documents.Add(new Document
        {
            SourceId = "core-rules-vendetta-patch-notes", Content = text, ContentHash = hash,
        });
        db.Changes.Add(new Change
        {
            SourceId = "core-rules-vendetta-patch-notes", ChangeType = "core-rule", Severity = "high",
            Summary = "al eerder verwerkt", Diff = "…",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) });

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("unchanged", Assert.Single(results).Status);
        Assert.Single(await db.Changes.ToListAsync()); // nog steeds maar 1 — geen dubbele
    }

    [Fact]
    public async Task ScanAsync_OneShotAlsEditorialGeclassificeerd_MemoBlokkeertTweedePoging()
    {
        // #205-review (findings 4/5/9): de geminte one-shot-Change kan (nu of
        // via de #58-naclassificatie later) als "editorial" gelabeld worden —
        // dan telt hij niet meer als "niet-editoriale Change" en zou de guard
        // zonder memo élke scan opnieuw minten. Het run_log-memo sluit dat af.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Legion is a dependent keyword."));

        await svc.ScanAsync(onlyDue: false); // eerste scan: mint one-shot + memo
        var minted = await db.Changes.SingleAsync();
        // Simuleer een (her)classificatie naar "editorial" (#58-pad).
        minted.ChangeType = "editorial";
        await db.SaveChangesAsync();

        var results = await svc.ScanAsync(onlyDue: false); // hash ongewijzigd

        Assert.Equal("unchanged", Assert.Single(results).Status);
        Assert.Single(await db.Changes.ToListAsync()); // géén tweede one-shot
    }

    [Fact]
    public async Task ScanAsync_PatchNotesBronMetAlleenEditorialeChange_MaaktAlsnogOneShotChange()
    {
        // #205: de editorial sidebar-ruis ("volgorde van aankondigingen
        // gewijzigd") is precies het scenario uit de bug — een editorial
        // Change telt NIET als "al verwerkt", dus de one-shot-tak vuurt hier
        // alsnog en levert de ontbrekende inhoudelijke Change op.
        // StripVersion blijft hier bewust null (productie-stand: zulke
        // ruis-rijen dateren van vóór de strip-versionering) — de scan loopt
        // dan via het rebaseline-pad, dat dezelfde candidacy draait.
        using var db = NewDb();
        var raw = RawHtml("Legion is a dependent keyword.");
        var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
        var hash = TextUtils.Sha256(text);
        db.Sources.Add(new Source
        {
            Id = "core-rules-vendetta-patch-notes", Name = "Core Rules: Vendetta Patch Notes",
            Url = "https://playriftbound.com/en-us/news/announcements/core-rules-vendetta-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
            ContentKind = SourceContentKind.PatchNotes, ContentKindSource = SourceContentKind.LlmOrigin,
            LastHash = hash,
        });
        db.Documents.Add(new Document
        {
            SourceId = "core-rules-vendetta-patch-notes", Content = text, ContentHash = hash,
        });
        db.Changes.Add(new Change
        {
            SourceId = "core-rules-vendetta-patch-notes", ChangeType = "editorial", Severity = "low",
            Summary = "volgorde van aankondigingen gewijzigd", Diff = "…",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) });

        var results = await svc.ScanAsync(onlyDue: false);

        Assert.Equal("changed", Assert.Single(results).Status);
        var changes = await db.Changes.ToListAsync();
        Assert.Equal(2, changes.Count); // de bestaande editorial-rij + de nieuwe one-shot-Change
        Assert.Contains(changes, c => c.ChangeType != "editorial" && c.Diff!.Contains("Legion"));
    }

    [Fact]
    public async Task ScanAsync_EersteScanVanCommunityFaqMirror_MaaktGeenChange()
    {
        // Autoriteitsmodel #166: alleen TrustTier == 1 krijgt de automatische
        // sjabloon-change — een community-mirror (trust 3) niet, ook al matcht
        // de naam op "faq".
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "community-faq-mirror", Name = "Community FAQ mirror",
            Url = "https://example.com/faq", Type = "community", TrustTier = 3,
            Rank = 10, Parser = "html", Cadence = "weekly",
        });
        await db.SaveChangesAsync();
        var svc = NewIngest(db, _ => Html("Uitleg."));

        await svc.ScanAsync(onlyDue: false);

        Assert.Empty(await db.Changes.ToListAsync());
    }

    // --- testinfra (zelfde patroon als IngestServiceUpdatedAtTests) -------

    private static string RawHtml(string text) => $"<html><body><p>{text}</p></body></html>";

    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(RawHtml(text)),
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
