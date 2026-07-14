using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record FeedCrawlResult(
    int FeedsChecked, int ArticlesSeen, int NewSources, int NewProposals, string Message);

/// <summary>Bron-feeds (#167): index-pagina's periodiek afspeuren op nieuwe
/// artikel-URL's en die — per feed — direct als <see cref="Source"/>
/// (AutoApprove) of als <see cref="SourceProposal"/> (reviewqueue) toevoegen.
/// Registreert alleen de artikel-URL zelf; of de inhoud daarachter inline HTML
/// of een opake Sanity-CDN-PDF is, bepaalt de bestaande artikel-ingest
/// (<see cref="IngestService"/>) bij de eerstvolgende bron-scan — deze service
/// raadt nooit zelf een PDF-link.
///
/// AutoApprove-gate (#167, security): AutoApprove maakt van een artikel alléén
/// dírect een enabled trust-1 official Source wanneer zowel de feed-URL als de
/// artikel-URL op een officieel Riot-domein staan (<see cref="OfficialDomains"/>).
/// Een AutoApprove-feed op élke andere host — een typo, een look-alike-domein —
/// routeert nieuwe artikelen naar de reviewqueue in plaats van onbeheerd een
/// stroom trust-1 official bronnen te fabriceren die /ask-citaties en de
/// TrustTier==1-gefilterde ban/errata/claim-pipelines voeden. De admin-endpoint
/// weigert AutoApprove=true al bij het opslaan op een niet-officieel domein
/// (nette melding); deze crawl-check is de tweede laag (defense-in-depth, net
/// als UrlGuard: endpoint + fetch-rand).
///
/// Idempotent op genormaliseerde URL: een artikel dat al een Source of
/// SourceProposal is (uit een eerdere run, of uit een andere feed eerder in
/// dézelfde run — de drie hoofdfeeds overlappen deels) wordt nooit dubbel
/// aangemaakt. Dat maakt deze crawl ook doof voor een per-request wisselende
/// linkvolgorde (zoals de Rules Hub laat zien, docs/CLAUDE.md): een reparse na
/// een gewijzigde hash levert hooguit dezelfde al-bekende URL's opnieuw op,
/// nooit een duplicaat of een change-achtige melding — er bestaat hier geen
/// Change-entiteit om ruis in te produceren. <see cref="SourceFeed.LastHash"/>
/// is dus puur een goedkope skip-optimalisatie (ongewijzigde pagina ⇒
/// gegarandeerd geen nieuwe artikelen), geen correctheidsmechanisme.
///
/// Best-effort per feed: een fout (fetch, guard, opslag) landt in run_log en
/// de andere feeds draaien gewoon door. <see cref="RunAsync"/> respecteert
/// <paramref name="onlyDue"/> per feed net als <see
/// cref="IngestService.ScanAsync"/> dat voor bronnen doet — de geplande
/// uurlijkse tick (<c>onlyDue: true</c>) hamert dus niet elk uur op
/// playriftbound.com; een handmatige "Bronnen scannen" of de losse
/// "feeds"-job (<c>onlyDue: false</c>) forceert alle enabled feeds.</summary>
public class FeedCrawlService(RbRulesDbContext db, HttpClient http)
{
    public const string LedgerKind = "feeds";

    /// <summary>Auto-ontdekte artikelen (alleen via de officiële-domein-gate,
    /// dus per definitie official/trust 1) staan lager dan de handmatig-curated
    /// officiële bronnen (rank 90-110 in SourceSeed) — een beheerder die zo'n
    /// bron handmatig omhoog curateert wint altijd. Boven partner (70): het
    /// blijft Riot's eigen site, alleen automatisch ontdekt in plaats van
    /// handmatig gekozen.</summary>
    public const int AutoDiscoveredRank = 80;

    public async Task<FeedCrawlResult> RunAsync(
        bool onlyDue, Action<string>? progress = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allFeeds = await db.SourceFeeds.Where(f => f.Enabled).OrderBy(f => f.Id).ToListAsync(ct);
        var feeds = onlyDue
            ? allFeeds.Where(f => Scheduling.IsDue(f.Cadence, f.LastChecked, now)).ToList()
            : allFeeds;

        if (feeds.Count == 0)
            return new(0, 0, 0, 0, "geen feeds aan de beurt");

        // Eén "known"-set voor de hele run (net als SourceScoutService): de
        // drie hoofdfeeds overlappen deels (de rules-hub-carrousel toont
        // dezelfde announcements als de algemene nieuws-hub), dus een
        // artikel dat feed A al registreerde slaat feed B in dezelfde run
        // over — geen duplicaten, ook niet binnen één run.
        var known = (await db.Sources.AsNoTracking().Select(s => s.Url).ToListAsync(ct))
            .Concat(await db.SourceProposals.AsNoTracking().Select(p => p.Url).ToListAsync(ct))
            .Select(RiotNewsFeed.NormalizeUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownIds = (await db.Sources.AsNoTracking().Select(s => s.Id).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var articlesSeen = 0;
        var newSources = 0;
        var newProposals = 0;
        for (var i = 0; i < feeds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var feed = feeds[i];
            progress?.Invoke($"feed {i + 1}/{feeds.Count}: {feed.Name} afspeuren");
            var (seen, sources, proposals) = await CrawlOneAsync(feed, known, knownIds, ct);
            articlesSeen += seen;
            newSources += sources;
            newProposals += proposals;
        }

        var message = $"{feeds.Count} feed(s) afgespeurd, {articlesSeen} artikelen gezien, "
            + $"{newSources} nieuwe bron(nen), {newProposals} nieuw(e) voorstel(len)";
        return new(feeds.Count, articlesSeen, newSources, newProposals, message);
    }

    /// <summary>Eén feed, volledig gecontaineerd: elke terugkeer (guard-
    /// weigering, HTTP-fout, uitzondering) logt naar run_log en levert
    /// (0, 0, 0) op — de aanroeper telt gewoon door met de volgende feed.</summary>
    private async Task<(int Seen, int NewSources, int NewProposals)> CrawlOneAsync(
        SourceFeed feed, HashSet<string> known, HashSet<string> knownIds, CancellationToken ct)
    {
        try
        {
            // SSRF-guard (#45): feed-URL's zijn beheerder-invoer (of straks
            // zelf toegevoegd via /api/admin/feeds) — zelfde guard als Source.
            if (UrlGuard.Check(feed.Url) is { Allowed: false } g)
            {
                await LogErrorAsync(feed.Id, $"URL geweigerd (SSRF-guard): {g.Reason}", ct);
                return (0, 0, 0);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, feed.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", IngestService.BrowserUserAgent);
            using var res = await http.SendAsync(req, ct);
            feed.LastChecked = DateTimeOffset.UtcNow;
            if (!res.IsSuccessStatusCode)
            {
                await LogErrorAsync(feed.Id, $"HTTP {(int)res.StatusCode}", ct);
                return (0, 0, 0);
            }

            var html = await res.Content.ReadAsStringAsync(ct);
            var hash = TextUtils.Sha256(html);
            if (feed.LastHash == hash)
            {
                // Ongewijzigd sinds de vorige run ⇒ gegarandeerd geen nieuwe
                // artikelen (zie klasse-doc) — LastChecked telt wél mee.
                await db.SaveChangesAsync(ct);
                return (0, 0, 0);
            }

            var articles = RiotNewsFeed.ParseArticles(html, new Uri(feed.Url), feed.CategoryFilter);
            // Officiële-domein-gate (#167): alleen een feed op een officieel
            // Riot-domein mag via AutoApprove auto-enablen. De artikel-URL
            // wordt hieronder per stuk óók gecheckt (defense-in-depth naast de
            // same-host-filter in de parser).
            var feedOfficial = OfficialDomains.IsOfficialUrl(feed.Url);
            var newSources = 0;
            var newProposals = 0;
            foreach (var article in articles)
            {
                if (!known.Add(article.Url)) continue; // al bron, al voorstel, of eerder deze run

                // Ook per artikel-URL (naast de feed-URL zelf): een feed-
                // pagina is externe invoer, een gemanipuleerde/onverwachte
                // link mag nooit ongezien het register in.
                if (UrlGuard.Check(article.Url) is { Allowed: false }) continue;

                // Auto-approve alleen als feed én artikel op een officieel
                // domein staan; een look-alike-pagina die een externe-host-URL
                // als "artikel" injecteert (articleOfficial=false) belandt zo
                // in de reviewqueue, nooit als enabled trust-1 bron.
                var articleOfficial = OfficialDomains.IsOfficialUrl(article.Url);
                if (feed.AutoApprove && feedOfficial && articleOfficial)
                {
                    var id = UniqueSourceId(SourceScout.SlugForUrl(article.Url), knownIds);
                    db.Sources.Add(new Source
                    {
                        Id = id, Name = article.Title, Url = article.Url,
                        Type = "official", TrustTier = 1, Rank = AutoDiscoveredRank,
                        // html: de bestaande artikel-ingest bepaalt zelf of de
                        // inhoud inline is of naar een PDF linkt (ankertekst-
                        // match, zelfde pad als de Rules Hub-ontdekking).
                        Parser = "html", Cadence = "weekly", Enabled = true,
                        FeedId = feed.Id,
                    });
                    newSources++;
                }
                else
                {
                    // Type-inschatting: alleen een officieel-domein-artikel
                    // heet "official"; al het andere degradeert naar community
                    // (de beheerder beslist bij het accepteren — bron-trust is
                    // heilig, docs/KNOWLEDGE.md).
                    var kind = articleOfficial ? "official" : "community";
                    db.SourceProposals.Add(new SourceProposal
                    {
                        Url = article.Url,
                        Name = article.Title,
                        Type = kind,
                        Motivation = $"Nieuw artikel gevonden via feed '{feed.Name}'"
                            + (article.Category is not null ? $" ({article.Category})" : "")
                            + (article.Date is not null ? $", gepubliceerd {article.Date:yyyy-MM-dd}" : "")
                            + (feed.AutoApprove && !feedOfficial
                                ? " — feed staat op een niet-officieel domein, dus ter review i.p.v. auto-toegevoegd."
                                : " — mogelijk een nieuwe regel-/errata-pagina."),
                    });
                    newProposals++;
                }
            }

            feed.LastHash = hash;
            db.RunLogs.Add(new RunLog
            {
                Kind = LedgerKind, Ref = feed.Id, Status = "ok",
                Detail = $"{articles.Count} artikelen gezien, {newSources + newProposals} nieuw "
                         + $"({newSources} bron, {newProposals} voorstel)",
            });
            await db.SaveChangesAsync(ct);
            return (articles.Count, newSources, newProposals);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Eén giftige feed mag de rest van de run niet meeslepen (#15-
            // patroon): getrackte entiteiten van deze poging weg vóór de
            // volgende SaveChanges.
            db.ChangeTracker.Clear();
            await LogErrorAsync(feed.Id, ex.Message, ct);
            return (0, 0, 0);
        }
    }

    private async Task LogErrorAsync(string feedId, string detail, CancellationToken ct)
    {
        db.RunLogs.Add(new RunLog { Kind = LedgerKind, Ref = feedId, Status = "error", Detail = detail });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Slug-botsingen (twee artikelen op hetzelfde host+laatste
    /// padsegment) krijgen een volgnummer-suffix — zelfde patroon als
    /// SourceScoutService, maar in-memory: <paramref name="knownIds"/> is
    /// vooraf gevuld met alle bestaande Id's en groeit binnen de run mee, dus
    /// twee artikelen binnen dezelfde feed-crawl kunnen elkaar nooit
    /// overschrijven vóórdat SaveChanges de eerste heeft weggeschreven.</summary>
    private static string UniqueSourceId(string baseId, HashSet<string> knownIds)
    {
        var id = baseId;
        for (var n = 2; knownIds.Contains(id); n++) id = $"{baseId}-{n}";
        knownIds.Add(id);
        return id;
    }
}
