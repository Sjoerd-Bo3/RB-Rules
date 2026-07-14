using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record FeedCrawlResult(
    int FeedsChecked, int ArticlesSeen, int NewSources, int NewProposals,
    int AdoptedSources, int MergedDuplicates, string Message);

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
/// "feeds"-job (<c>onlyDue: false</c>) forceert alle enabled feeds.
///
/// Herkomst-adoptie (#175): een artikel-URL die al een bestaande <see
/// cref="Source"/> is met <see cref="Source.FeedId"/> == null (handmatig/
/// legacy toegevoegd, maar in werkelijkheid een afstammeling van déze feed)
/// wordt niet meer stilzwijgend overgeslagen — de bron adopteert de feed als
/// herkomst. Curatie (Enabled/TrustTier/Rank) blijft daarbij exact zoals ze
/// is: adoptie is pure herkomst-correctie, geen nieuwe beoordeling. Near-
/// duplicaat-bronnen (dezelfde pagina in een andere URL-vorm — trailing
/// slash, http/https, www — die vóór deze fix als aparte rijen bestonden)
/// worden aan het begin van elke run samengevoegd (<see
/// cref="MergeNearDuplicateSourcesAsync"/>); beide stappen zijn idempotent.</summary>
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
        // Near-duplicaat-samenvoeging (#175) vóóraf: onafhankelijk van welke
        // feeds aan de beurt zijn — het is een opschoning van het volledige
        // bronnenregister, geen per-feed-stap — en zo ziet de rest van deze
        // run (known/adoptable hieronder) meteen de opgeschoonde stand.
        progress?.Invoke("near-duplicaat-bronnen samenvoegen");
        var mergedDuplicates = await MergeNearDuplicateSourcesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var allFeeds = await db.SourceFeeds.Where(f => f.Enabled).OrderBy(f => f.Id).ToListAsync(ct);
        var feeds = onlyDue
            ? allFeeds.Where(f => Scheduling.IsDue(f.Cadence, f.LastChecked, now)).ToList()
            : allFeeds;

        if (feeds.Count == 0)
        {
            var idleMessage = "geen feeds aan de beurt" + (mergedDuplicates > 0
                ? $", {mergedDuplicates} near-duplicaat-bron(nen) samengevoegd" : "");
            return new(0, 0, 0, 0, 0, mergedDuplicates, idleMessage);
        }

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
        // Adoptie-kandidaten (#175): getrackte Sources zonder FeedId, opgezocht
        // op genormaliseerde URL — meerdere rijen kunnen bewust dezelfde URL
        // delen (Rules Hub-PDF/HTML-drieling), dus een Lookup i.p.v. Dictionary.
        var adoptable = (await db.Sources.Where(s => s.FeedId == null).ToListAsync(ct))
            .ToLookup(s => RiotNewsFeed.NormalizeUrl(s.Url), StringComparer.OrdinalIgnoreCase);

        var articlesSeen = 0;
        var newSources = 0;
        var newProposals = 0;
        var adoptedSources = 0;
        for (var i = 0; i < feeds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var feed = feeds[i];
            progress?.Invoke($"feed {i + 1}/{feeds.Count}: {feed.Name} afspeuren");
            var (seen, sources, proposals, adopted) =
                await CrawlOneAsync(feed, known, knownIds, adoptable, ct);
            articlesSeen += seen;
            newSources += sources;
            newProposals += proposals;
            adoptedSources += adopted;
        }

        var message = $"{feeds.Count} feed(s) afgespeurd, {articlesSeen} artikelen gezien, "
            + $"{newSources} nieuwe bron(nen), {newProposals} nieuw(e) voorstel(len)"
            + (adoptedSources > 0
                ? $", {adoptedSources} bestaande bron(nen) geadopteerd (herkomst)" : "")
            + (mergedDuplicates > 0
                ? $", {mergedDuplicates} near-duplicaat-bron(nen) samengevoegd" : "");
        return new(feeds.Count, articlesSeen, newSources, newProposals,
            adoptedSources, mergedDuplicates, message);
    }

    /// <summary>Eén feed, volledig gecontaineerd: elke terugkeer (guard-
    /// weigering, HTTP-fout, uitzondering) logt naar run_log en levert
    /// (0, 0, 0, 0) op — de aanroeper telt gewoon door met de volgende feed.</summary>
    private async Task<(int Seen, int NewSources, int NewProposals, int Adopted)> CrawlOneAsync(
        SourceFeed feed, HashSet<string> known, HashSet<string> knownIds,
        ILookup<string, Source> adoptable, CancellationToken ct)
    {
        try
        {
            // SSRF-guard (#45): feed-URL's zijn beheerder-invoer (of straks
            // zelf toegevoegd via /api/admin/feeds) — zelfde guard als Source.
            if (UrlGuard.Check(feed.Url) is { Allowed: false } g)
            {
                await LogErrorAsync(feed.Id, $"URL geweigerd (SSRF-guard): {g.Reason}", ct);
                return (0, 0, 0, 0);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, feed.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", IngestService.BrowserUserAgent);
            using var res = await http.SendAsync(req, ct);
            feed.LastChecked = DateTimeOffset.UtcNow;
            if (!res.IsSuccessStatusCode)
            {
                await LogErrorAsync(feed.Id, $"HTTP {(int)res.StatusCode}", ct);
                return (0, 0, 0, 0);
            }

            var html = await res.Content.ReadAsStringAsync(ct);
            var hash = TextUtils.Sha256(html);
            if (feed.LastHash == hash)
            {
                // Ongewijzigd sinds de vorige run ⇒ gegarandeerd geen nieuwe
                // artikelen (zie klasse-doc) — LastChecked telt wél mee.
                await db.SaveChangesAsync(ct);
                return (0, 0, 0, 0);
            }

            var articles = RiotNewsFeed.ParseArticles(html, new Uri(feed.Url), feed.CategoryFilter);
            // Officiële-domein-gate (#167): alleen een feed op een officieel
            // Riot-domein mag via AutoApprove auto-enablen. De artikel-URL
            // wordt hieronder per stuk óók gecheckt (defense-in-depth naast de
            // same-host-filter in de parser).
            var feedOfficial = OfficialDomains.IsOfficialUrl(feed.Url);
            var newSources = 0;
            var newProposals = 0;
            var adopted = 0;
            foreach (var article in articles)
            {
                if (!known.Add(article.Url))
                {
                    // Al bron, al voorstel, of eerder deze run — maar een
                    // bestaande Source zonder FeedId die toevallig dezelfde
                    // (genormaliseerde) URL draagt, stamt in werkelijkheid van
                    // déze feed (#175): adopteer stil, curatie blijft ongemoeid.
                    adopted += AdoptExistingSources(article.Url, feed.Id, adoptable);
                    continue;
                }

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
                        // Temporele precedentie (#168): de feed levert de
                        // publicatiedatum van het artikel zelf mee — geen gok,
                        // een ontbrekende <time>-tag (RiotNewsFeed) laat dit null.
                        PublishedAt = article.Date,
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
                         + $"({newSources} bron, {newProposals} voorstel)"
                         + (adopted > 0
                             ? $", {adopted} bestaande bron(nen) geadopteerd (herkomst)" : ""),
            });
            await db.SaveChangesAsync(ct);
            return (articles.Count, newSources, newProposals, adopted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Eén giftige feed mag de rest van de run niet meeslepen (#15-
            // patroon): getrackte entiteiten van deze poging weg vóór de
            // volgende SaveChanges — óók een adoptie die deze poging
            // (nog niet opgeslagen) deed.
            db.ChangeTracker.Clear();
            await LogErrorAsync(feed.Id, ex.Message, ct);
            return (0, 0, 0, 0);
        }
    }

    /// <summary>Herkomst-adoptie (#175, stil): zet <see cref="Source.FeedId"/>
    /// op elke bestaande Source zonder herkomst die dezelfde (genormaliseerde)
    /// URL draagt als dit net "opnieuw ontdekte" artikel. Curatie (Enabled/
    /// TrustTier/Rank) blijft ongemoeid — dit is geen nieuwe beoordeling, puur
    /// een correctie van waar de bron vandaan blijkt te komen. Meerdere Source-
    /// rijen kunnen bewust dezelfde URL delen (Rules Hub-PDF/HTML-drieling in
    /// SourceSeed, elk met een eigen Parser); die adopteren dan allemaal, want
    /// de herkomst-vraag geldt voor elke rij die naar die URL wijst. Idempotent:
    /// een Source met FeedId al gezet komt niet meer door het filter.</summary>
    private static int AdoptExistingSources(
        string articleUrl, string feedId, ILookup<string, Source> adoptable)
    {
        var norm = RiotNewsFeed.NormalizeUrl(articleUrl);
        var adopted = 0;
        foreach (var candidate in adoptable[norm].Where(s => s.FeedId is null))
        {
            candidate.FeedId = feedId;
            adopted++;
        }
        return adopted;
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

    /// <summary>Near-duplicaat-samenvoeging (#175): bronnen die vóór deze fix
    /// als aparte rijen bestonden, maar wier URL alléén in VORM verschilt
    /// (trailing slash, http/https, www) — <see
    /// cref="SourceScout.StrongNormalizeUrl"/> vangt die vormen samen. Twee (of
    /// meer) rijen met een LETTERLIJK gelijke URL (zoals de Rules Hub-PDF/HTML-
    /// drieling in SourceSeed, die bewust dezelfde ontdek-pagina delen met elk
    /// een eigen Parser) zijn GEEN near-duplicaat: zo'n groep telt alleen mee
    /// als elke rij een eigen (zwak-genormaliseerde) URL-vorm heeft — anders
    /// zou deze stap per ongeluk een bewust-gedeelde bron opeten. Winnaar: de
    /// rij mét FeedId (herkomst al vastgesteld), anders de hoogste Rank (meest
    /// gecureerd), anders de laagste Id (stabiele, deterministische
    /// tie-breaker — Source kent geen CreatedAt-kolom). Referenties hangen mee
    /// om (#144-patroon): Document/Change/RuleChunk/ClaimSource op SourceId,
    /// Conflict op SourceAId/SourceBId/WinnerSourceId (anders cascadeert een
    /// verwijderde bron die weg, #45-stijl audit-fix), BanEntry/Erratum/
    /// Correction op de URL-vorm (SourceUrl/SourceRef, #171-patroon). Eigen
    /// transactie (net als CardSyncService.RepairSourceFormsAsync): meerdere
    /// tabellen wijzigen mee, of alles landt of niets. Idempotent: na de
    /// eerste merge blijft nog maar één rij per groep over.</summary>
    public async Task<int> MergeNearDuplicateSourcesAsync(CancellationToken ct = default)
    {
        var sources = await db.Sources.ToListAsync(ct); // getrackt: FeedId/verwijderen wijzigt mee
        var groups = sources
            .GroupBy(s => SourceScout.StrongNormalizeUrl(s.Url))
            .Where(g => g.Count() > 1)
            .Where(g => g.Select(s => SourceScout.NormalizeUrl(s.Url))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == g.Count())
            .ToList();
        if (groups.Count == 0) return 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var merged = 0;
        foreach (var group in groups)
        {
            var winner = group
                .OrderByDescending(s => s.FeedId is not null)
                .ThenByDescending(s => s.Rank)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .First();
            var losers = group.Where(s => s.Id != winner.Id).ToList();
            var winnerClaimIds = (await db.ClaimSources
                .Where(cs => cs.SourceId == winner.Id).Select(cs => cs.ClaimId).ToListAsync(ct))
                .ToHashSet();
            foreach (var loser in losers)
            {
                await RepointSourceReferencesAsync(loser, winner, winnerClaimIds, ct);
                db.Sources.Remove(loser);
                merged++;
            }
        }

        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind, Ref = null, Status = "ok",
            Detail = $"{merged} near-duplicaat-bron(nen) samengevoegd op URL-vorm (#175)",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return merged;
    }

    private async Task RepointSourceReferencesAsync(
        Source loser, Source winner, HashSet<long> winnerClaimIds, CancellationToken ct)
    {
        var loserId = loser.Id;
        var winnerId = winner.Id;

        // ── SourceId-koppelvorm ─────────────────────────────────────────
        foreach (var d in await db.Documents.Where(x => x.SourceId == loserId).ToListAsync(ct))
            d.SourceId = winnerId;
        foreach (var c in await db.Changes.Where(x => x.SourceId == loserId).ToListAsync(ct))
            c.SourceId = winnerId;
        foreach (var rc in await db.RuleChunks.Where(x => x.SourceId == loserId).ToListAsync(ct))
            rc.SourceId = winnerId;

        // ClaimSource: (ClaimId, SourceId) is uniek — een botsing betekent dat
        // de winnaar dezelfde claim al draagt (corroboratie telt al mee via de
        // winnaar); de dubbele rij vervalt dan i.p.v. de index te schenden
        // (zelfde #144-patroon als de kaart-interactie-samenvoeging).
        foreach (var cs in await db.ClaimSources.Where(x => x.SourceId == loserId).ToListAsync(ct))
        {
            if (!winnerClaimIds.Add(cs.ClaimId))
            {
                db.ClaimSources.Remove(cs);
                continue;
            }
            cs.SourceId = winnerId;
            cs.Url = winner.Url;
        }

        // Conflict: geen unieke index op de Source-velden, gewoon omhangen —
        // zonder dit zou de FK-cascade (SourceAId) de rij stilzwijgend
        // verwijderen zodra de bron straks weg is.
        foreach (var cf in await db.Conflicts
            .Where(x => x.SourceAId == loserId || x.SourceBId == loserId || x.WinnerSourceId == loserId)
            .ToListAsync(ct))
        {
            if (cf.SourceAId == loserId) cf.SourceAId = winnerId;
            if (cf.SourceBId == loserId) cf.SourceBId = winnerId;
            if (cf.WinnerSourceId == loserId) cf.WinnerSourceId = winnerId;
        }

        // ── SourceUrl-koppelvorm (#171-patroon) ─────────────────────────
        var loserUrls = UrlCandidates(loser.Url);
        foreach (var ban in await db.BanEntries.Where(x => loserUrls.Contains(x.SourceUrl)).ToListAsync(ct))
            ban.SourceUrl = winner.Url;
        foreach (var erratum in await db.Errata.Where(x => loserUrls.Contains(x.SourceUrl)).ToListAsync(ct))
            erratum.SourceUrl = winner.Url;
        foreach (var correction in await db.Corrections
            .Where(x => x.SourceRef != null && loserUrls.Contains(x.SourceRef)).ToListAsync(ct))
            correction.SourceRef = winner.Url;
    }

    /// <summary>Genormaliseerde varianten van een bron-URL om tegen SourceUrl/
    /// SourceRef te matchen — zelfde constructie als
    /// SourceDossierService.UrlCandidates (#171): letterlijke kandidaten vóór
    /// de query, dan een vertaalbare <c>Contains</c> op een gesloten lijst.</summary>
    private static HashSet<string> UrlCandidates(string url)
    {
        var normalized = SourceScout.NormalizeUrl(url);
        return [url, normalized, normalized + "/"];
    }
}
