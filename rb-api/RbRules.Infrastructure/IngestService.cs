using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record IngestResult(string SourceId, string Status, string? Detail = null);

/// <summary>Scan-pipeline (port van de PoP-runner, met audit-fixes):
/// fetch → boilerplate-strip → hash → bron-type-classificatie (#188
/// increment 2, <see cref="ClassifyContentKindAsync"/>) → diff → AI-classify
/// → store + log. Opent met de bron-feed-crawl (#167, <see
/// cref="FeedCrawlService"/>): nieuw-ontdekte artikelen worden zo in dezelfde
/// run als Source al meegescand. Sluit af met een naclassificatie-ronde voor
/// changes die eerder zonder samenvatting zijn opgeslagen (#58) en de
/// kennis-hertoets (#119): een verwerkte regelwijziging legt de kennis die
/// erop leunt terug voor review in plaats van die stil te laten
/// verouderen.</summary>
public class IngestService(
    RbRulesDbContext db, HttpClient http, RbAiClient ai,
    ChangeClassificationService classifier, KnowledgeRecheckService recheck,
    FeedCrawlService feeds)
{
    /// <summary>Retry-venster voor naclassificatie: oud genoeg om een paar
    /// dagen rb-ai-uitval te overbruggen, jong genoeg om de scan goedkoop te
    /// houden (oudere gevallen pakt de handmatige classify-job op).</summary>
    public static readonly TimeSpan ReclassifyWindow = TimeSpan.FromDays(14);

    public const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public async Task<List<IngestResult>> ScanAsync(
        bool onlyDue, string? sourceId = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Bron-feeds (#167) vóór de bron-scan: een net ontdekt artikel wordt
        // zo in dezelfde run als nieuwe Source al gescand (in plaats van pas
        // een run later). FeedCrawlService is zelf al volledig best-effort
        // per feed (eigen run_log-regels); deze try/catch vangt alleen een
        // onverwachte fout die daar toch doorheen zou glippen (bv. de
        // SourceFeeds-query zelf) — de bron-scan mag daar nooit op stranden.
        // onlyDue gaat één-op-één door: de geplande uurlijkse tick
        // (ScanScheduler) hamert zo niet elk uur op playriftbound.com, een
        // handmatige scan of de losse "feeds"-job forceert alle feeds.
        try
        {
            await feeds.RunAsync(onlyDue, progress: p => progress?.Invoke($"feeds — {p}"), ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = FeedCrawlService.LedgerKind, Ref = null, Status = "error", Detail = ex.Message,
            });
            await db.SaveChangesAsync(ct);
        }

        // Genegeerd (#180) ≠ uitgeschakeld: beide slaan de geplande scan-lus
        // over (geen LLM-kosten voor een bron die niets oplevert), maar een
        // handmatige rescan van één specifieke bron (sourceId hieronder)
        // bypasst dit filter net zoals hij Enabled al bypasste — de
        // beheerder mag een genegeerde bron altijd gericht opnieuw bekijken.
        var query = db.Sources.Where(s => s.Enabled && s.IgnoredAt == null);
        if (sourceId is not null) query = db.Sources.Where(s => s.Id == sourceId);
        var sources = await query
            .OrderBy(s => s.TrustTier).ThenByDescending(s => s.Rank)
            .ToListAsync(ct);

        var results = new List<IngestResult>();
        var n = 0;
        foreach (var src in sources)
        {
            n++;
            if (onlyDue && !Scheduling.IsDue(src.Cadence, src.LastChecked, now)) continue;
            progress?.Invoke($"bron {n}/{sources.Count}: {src.Name} ophalen en vergelijken");
            var r = await ScanOneAsync(src, ct);
            results.Add(r);
            db.RunLogs.Add(new RunLog { Kind = "scan", Ref = src.Id, Status = r.Status, Detail = r.Detail });
            await db.SaveChangesAsync(ct);
        }

        // Naclassificatie (#58): changes die bij een eerdere scan zonder
        // classificatie zijn opgeslagen (rb-ai-uitval) krijgen alsnog een kans
        // — de diff staat immers opgeslagen. Best-effort: uitval hier raakt de
        // scan-resultaten niet.
        try
        {
            var r = await classifier.ClassifyPendingAsync(
                since: now - ReclassifyWindow,
                progress: p => progress?.Invoke($"naclassificatie — {p}"), ct: ct);
            if (r.Attempted > 0)
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "classify", Ref = "scan-retry",
                    Status = r.Failed > 0 ? "info" : "ok",
                    Detail = $"{r.Classified} changes alsnog geclassificeerd, {r.Failed} mislukt",
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "classify", Ref = "scan-retry", Status = "error", Detail = ex.Message,
            });
            await db.SaveChangesAsync(ct);
        }

        // Kennis-hertoets (#119): verwerkte regelwijzigingen hertoetsen de
        // kennis die erop leunt — primer-docs op geraakte secties terug naar
        // draft (met reden), betrokken accepted claims door de official-check.
        // Bewust alleen changes van vóór deze run (before: now): de her-index
        // van de scheduler moet de nieuwe regelversie eerst in rule_chunk
        // zetten, anders toetst de official-check tegen de oude tekst; een
        // change van deze scan komt bij de volgende afronding vanzelf aan de
        // beurt. Best-effort met een eigen run_log-grootboek (#58-patroon):
        // uitval hier raakt de scan-resultaten niet.
        try
        {
            var r = await recheck.RunAsync(
                since: now - KnowledgeRecheckService.RecheckWindow, before: now,
                progress: p => progress?.Invoke($"kennis-hertoets — {p}"), ct: ct);
            if (r.Changes > 0)
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = KnowledgeRecheckService.LedgerKind, Ref = "scan-afronding",
                    Status = r.Deferred > 0 ? "info" : "ok",
                    Detail = r.Message,
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = KnowledgeRecheckService.LedgerKind, Ref = "scan-afronding",
                Status = "error", Detail = ex.Message,
            });
            await db.SaveChangesAsync(ct);
        }

        return results;
    }

    private async Task<IngestResult> ScanOneAsync(Source src, CancellationToken ct)
    {
        try
        {
            // SSRF-guard (#45): register-URL's zijn beheerder-invoer en (via
            // scout/hub-ontdekking) deels webvondsten — valideer vóór elke
            // fetch. De fetch-laag (SafeExternalHttp) checkt daarnaast de
            // geresolvede IP's, ook van redirect-doelen.
            if (UrlGuard.Check(src.Url) is { Allowed: false } g)
                return new(src.Id, "error", $"URL geweigerd (SSRF-guard): {g.Reason}");

            using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            using var res = await http.SendAsync(req, ct);
            src.LastChecked = DateTimeOffset.UtcNow;
            if (!res.IsSuccessStatusCode)
                return new(src.Id, "error", $"HTTP {(int)res.StatusCode}");

            string text;
            string? fileUrl = null;
            switch (src.Parser)
            {
                case "html":
                {
                    var raw = await res.Content.ReadAsStringAsync(ct);
                    text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
                    // #94: de hub-index is óók de ontdek-pagina voor per-set-
                    // artikelen — op de rauwe HTML, want de tekst-extractie
                    // stript de ankers (href + linktekst) die de ontdekking
                    // nodig heeft.
                    if (src.Id == SourceSeed.RulesHubId)
                        await ProposeHubSetPagesAsync(raw, new Uri(src.Url), ct);
                    break;
                }
                case "pdf":
                {
                    // Directe PDF-URL (partner-bronnen zoals UVS Games, #63):
                    // de respons is zelf al een PDF — geen ontdek-stap nodig.
                    if (res.Content.Headers.ContentType?.MediaType == "application/pdf")
                    {
                        text = PdfTextExtractor.Extract(await res.Content.ReadAsByteArrayAsync(ct));
                        fileUrl = src.Url;
                        break;
                    }

                    // Anders is src.Url de ontdek-pagina (Rules Hub); de PDF-link
                    // wordt per run gevonden (versies wisselen — nooit hardcoden).
                    var hubHtml = await res.Content.ReadAsStringAsync(ct);
                    var keyword = src.Id.Contains("tournament", StringComparison.OrdinalIgnoreCase)
                        ? "tournament" : "core";
                    var pdfUrl = PdfDiscovery.FindPdfUrl(hubHtml, keyword, new Uri(src.Url));
                    if (pdfUrl is null)
                        return new(src.Id, "error", $"geen '{keyword}'-PDF-link gevonden op {src.Url}");

                    // SSRF-guard (#45): de PDF-link komt uit opgehaalde HTML
                    // en is dus per definitie externe invoer.
                    if (UrlGuard.Check(pdfUrl) is { Allowed: false } pg)
                        return new(src.Id, "error", $"PDF-URL geweigerd (SSRF-guard): {pg.Reason} ({pdfUrl})");

                    using var pdfReq = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
                    pdfReq.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                    using var pdfRes = await http.SendAsync(pdfReq, ct);
                    if (!pdfRes.IsSuccessStatusCode)
                        return new(src.Id, "error", $"PDF HTTP {(int)pdfRes.StatusCode} ({pdfUrl})");
                    var bytes = await pdfRes.Content.ReadAsByteArrayAsync(ct);
                    text = PdfTextExtractor.Extract(bytes);
                    fileUrl = pdfUrl.ToString();
                    break;
                }
                default:
                    return new(src.Id, "error", $"parser '{src.Parser}' nog niet ondersteund");
            }
            // #188 increment 2: bron-type-classificatie (LLM i.p.v. keyword-
            // heuristiek) — alleen officiële (trust-1) bronnen, en alleen als
            // er nog geen classificatie is (null) of de vorige poging een
            // heuristische fallback was (upgrade-pad: een latere run mag een
            // heuristische classificatie alsnog naar een LLM-oordeel
            // optillen; het omgekeerde gebeurt nooit stilzwijgend). Een
            // "llm"- of "admin"-herkomst valt buiten deze guard en wordt dus
            // nooit geherclassificeerd — een beheerder-override (#188-review,
            // SourceContentKind.TryApplyOverride) is definitief tot de
            // beheerder hem zelf wist. Vóór de hash-vergelijking, zodat ook
            // een ongewijzigde/flip-floppende pagina een kans krijgt —
            // bronnen worden zo vanzelf geclassificeerd bij hun
            // eerstvolgende scan, zonder apart backfill-commando (zelfde
            // "backfilt vanzelf"-patroon als ClarificationMiningService).
            if (src.TrustTier == 1
                && (src.ContentKind is null || src.ContentKindSource == SourceContentKind.HeuristicOrigin))
                await ClassifyContentKindAsync(src, text, ct);

            var effectiveKind = SourceContentKind.Resolve(src.ContentKind, src.Id, src.Url, src.Name);

            var hash = TextUtils.Sha256(text);
            if (src.LastHash == hash)
            {
                // #205 backfill: de inhoud is ongewijzigd (vandaar de vroege
                // "unchanged" hieronder), maar als deze patch-notes-bron nog
                // NOOIT een niet-editoriale Change kreeg (bv. de Vendetta-
                // bron: al gescand vóórdat deze fix bestond, dus alleen een
                // Document, geen Change) mag de al opgeslagen inhoud alsnog
                // als delta verwerkt worden — voor een one-shot-artikel is
                // dit de enige kans, want de inhoud verandert per definitie
                // nooit meer.
                if (await IsPatchNotesOneShotCandidateAsync(src, effectiveKind, ct)
                    && await TryAddOneShotPatchNotesChangeAsync(src, text, ct))
                    return new(src.Id, "changed",
                        "patch notes: alsnog een inhoudelijke Change gemaakt (backfill, #205)");
                return new(src.Id, "unchanged");
            }

            // Flip-flop-suppressie: sommige pagina's (Rules Hub) wisselen per
            // request de volgorde van gerelateerde-artikellinks. Is deze exacte
            // inhoud al eerder gezien, dan is het geen echte wijziging.
            var seenBefore = await db.Documents
                .AnyAsync(d => d.SourceId == src.Id && d.ContentHash == hash, ct);
            if (seenBefore)
            {
                src.LastHash = hash;
                return new(src.Id, "unchanged", "flip-flop: inhoud eerder gezien");
            }

            var prevDoc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);

            db.Documents.Add(new Document
            {
                SourceId = src.Id, Content = text, ContentHash = hash, FileUrl = fileUrl,
            });

            var isNew = src.LastHash is null;
            if (!isNew)
            {
                var diff = DiffUtils.LineDiff(prevDoc?.Content ?? "", text);
                if (string.IsNullOrWhiteSpace(diff))
                {
                    // Zelfde zinnen, andere volgorde of alleen opmaak — het
                    // document wél bewaren (nieuwste versie is leidend voor
                    // indexering), maar geen change-item in de feed.
                    src.LastHash = hash;
                    return new(src.Id, "unchanged", "alleen herordening/opmaak");
                }
                var cls = await ClassifyAsync(src.Name, diff, ct);
                db.Changes.Add(new Change
                {
                    SourceId = src.Id,
                    ChangeType = cls?.ChangeType ?? "unknown",
                    Severity = cls?.Severity ?? "medium",
                    Summary = cls?.Summary,
                    Meaning = cls?.Meaning,
                    Diff = diff,
                });
                // Temporele precedentie (#168): dit is het beste signaal dat
                // we hebben voor "wanneer is de bron zelf gewijzigd" — de
                // officiële pagina publiceert zelf geen aparte update-datum,
                // dus het detectiemoment is de benadering (zelfde aanname als
                // Change.DetectedAt elders in deze pipeline).
                src.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (await IsPatchNotesOneShotCandidateAsync(src, effectiveKind, ct))
            {
                // #205: een gloednieuwe patch-notes-bron (isNew, dus normaal
                // gesproken niets om te diffen) — de volledige inhoud IS de
                // delta voor een one-shot per-set-artikel (zie
                // PatchNotesOneShotChange voor de volledige motivatie). Dit is
                // de opvolger van de #185-beslissing om patch notes hier
                // NIET te raken: dat bleef correct voor een terugkerende
                // pagina (core-rules-patch-notes, die na haar eigen eerste
                // scan allang niet-editoriale Changes heeft en dus deze tak
                // nooit meer raakt), maar liet een one-shot-artikel
                // (Vendetta) permanent zonder inhoudelijke Change achter.
                await TryAddOneShotPatchNotesChangeAsync(src, text, ct);
            }
            else if (src.TrustTier == 1 && effectiveKind == SourceContentKind.Faq)
            {
                // #177: een FAQ-/clarificatie-artikel heeft bij zijn
                // allereerste scan geen vorige versie om te diffen (isNew),
                // dus bleef de aankomst zelf onzichtbaar in de wijzigingen-
                // feed (bug-rapport: "0 changes" voor de Unleashed Rules
                // FAQ) — terwijl de losse concepten er via
                // ClarificationMiningService straks wél als citeerbare
                // rulings uitkomen. Eén sjabloon-change bij aankomst (geen
                // extra LLM-call — de classificatie hierboven heeft het
                // bron-type al bepaald, de concept-extractie levert de echte
                // duiding later, apart als job); alleen voor officiële
                // bronnen, zelfde trust-gate als de mining zelf.
                //
                // #188 increment 2: de kind-check gebruikt SourceContentKind.
                // Resolve (LLM-classificatie, met de oude
                // ClarificationSources-heuristiek als transitionele
                // null-fallback) i.p.v. rechtstreeks ClarificationSources.
                // IsMatch — zelfde uitkomst voor een nog-niet-geclassificeerde
                // bron, maar nu ook correct voor een bron die de LLM als
                // "faq" herkent zonder de magische woorden in zijn slug.
                //
                // #205: patch-notes-bronnen krijgen sinds #205 hun eigen tak
                // hierboven (one-shot delta) in plaats van niets — dit
                // sjabloon blijft alleen voor FAQ-/clarificatie-bronnen: die
                // krijgen NOOIT een diff-Change (elke scan mint dezelfde
                // soort inhoud opnieuw als losse rulings, niet als delta),
                // dus zonder dit sjabloon zou hun aankomst permanent
                // onzichtbaar blijven in de wijzigingen-feed.
                db.Changes.Add(new Change
                {
                    SourceId = src.Id,
                    ChangeType = "clarification",
                    Severity = "medium",
                    Summary = $"Nieuw FAQ-/clarificatie-artikel: {src.Name}",
                });
                src.UpdatedAt = DateTimeOffset.UtcNow;
            }

            src.LastHash = hash;
            return new(src.Id, isNew ? "new" : "changed");
        }
        catch (Exception ex)
        {
            return new(src.Id, "error", ex.Message);
        }
    }

    /// <summary>#94: nieuwe per-set-links ("… Patch Notes"/"… Errata") op de
    /// Rules Hub als bronvoorstel loggen — dezelfde run_log-vorm als de scout
    /// (#63): kind "scout", Ref = url. Zo delen hub-ontdekking en webscout één
    /// dedupe (over runs heen én onderling) en één kanaal dat de komende
    /// voorstellenqueue kan backfillen. HubDiscovery sorteert en dedupliceert,
    /// dus de per-request wisselende linkvolgorde van de hub (flip-flop) geeft
    /// hier nooit ruis; er ontstaat géén Change-item. Best-effort: uitval van
    /// de ontdekking mag de scan van de hub zelf niet breken.</summary>
    private async Task ProposeHubSetPagesAsync(string html, Uri baseUri, CancellationToken ct)
    {
        try
        {
            var found = HubDiscovery.FindSetPages(html, baseUri);
            if (found.Count == 0) return;

            var registered = await db.Sources.AsNoTracking()
                .Select(s => s.Url).ToListAsync(ct);
            var proposedEarlier = await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == "scout" && l.Ref != null)
                .Select(l => l.Ref!).Distinct().ToListAsync(ct);
            var known = registered.Concat(proposedEarlier)
                .Select(HubDiscovery.ComparisonKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var page in found.Where(p => !known.Contains(HubDiscovery.ComparisonKey(p.Url))))
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "scout", Ref = page.Url, Status = "info",
                    Detail = $"{page.Url} — {page.Title} (official): nieuwe {page.Kind}-pagina "
                             + "op de Rules Hub (nieuwe set?); kandidaat trust-1-bron.",
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "scout", Ref = null, Status = "error",
                Detail = $"hub-ontdekking mislukt: {ex.Message}",
            });
        }
    }

    private async Task<Classification?> ClassifyAsync(string sourceName, string diff, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(diff)) return null;
        var raw = await ai.AskAsync(Classifier.BuildPrompt(sourceName, diff), Classifier.SystemPrompt, ct: ct);
        return raw is null ? null : Classifier.Parse(raw);
    }

    /// <summary>#205: cheap-eerst guard — de trustTier/kind-check gebeurt vóór
    /// de (enige) DB-query, zodat een gewone bron (verreweg de meeste scans)
    /// nooit de Changes-tabel raakt voor deze beslissing.</summary>
    private async Task<bool> IsPatchNotesOneShotCandidateAsync(
        Source src, string effectiveKind, CancellationToken ct)
    {
        if (src.TrustTier != 1 || effectiveKind != SourceContentKind.PatchNotes) return false;
        var hasNonEditorialChange = await db.Changes
            .AnyAsync(c => c.SourceId == src.Id && c.ChangeType != "editorial", ct);
        return PatchNotesOneShotChange.IsCandidate(src.TrustTier, effectiveKind, hasNonEditorialChange);
    }

    /// <summary>#205: bouwt de one-shot Change voor een patch-notes-bron
    /// zonder niet-editoriale Change — de volledige inhoud is de delta (lege
    /// "voor"-versie), dezelfde AI-classificatie/samenvatting als een echte
    /// diff (ChangeType uit de classifier, niet hardcoded). True als er een
    /// Change is toegevoegd; false bij een lege diff (leeg document — niets
    /// te classificeren, de bron blijft dan ongemoeid zoals elke andere lege
    /// bron zou blijven).</summary>
    private async Task<bool> TryAddOneShotPatchNotesChangeAsync(Source src, string text, CancellationToken ct)
    {
        var diff = DiffUtils.LineDiff("", text);
        if (string.IsNullOrWhiteSpace(diff)) return false;

        var cls = await ClassifyAsync(src.Name, diff, ct);
        db.Changes.Add(new Change
        {
            SourceId = src.Id,
            ChangeType = cls?.ChangeType ?? "unknown",
            Severity = cls?.Severity ?? "medium",
            Summary = cls?.Summary,
            Meaning = cls?.Meaning,
            Diff = diff,
        });
        src.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>#188 increment 2: bron-type-classificatie via rb-ai ("faq" |
    /// "patch-notes" | "other", <see cref="SourceContentKind"/>) i.p.v. de
    /// oude keyword-heuristiek — het model beoordeelt naam + URL + een kort
    /// content-fragment. Degradeert bij AI-uitval (scout-timeoutpatroon,
    /// zelfde als ClarificationMiningService.AskSafeAsync) of een onbruikbaar
    /// antwoord naar <see cref="SourceContentKind.HeuristicKind"/> — nooit een
    /// harde 500, altijd een classificatie. Wijkt een geslaagd LLM-oordeel af
    /// van wat de heuristiek zou zeggen, dan komt er één run_log-regel met
    /// beide waarden (#188-review, fix E — zichtbaarheid zonder blokkade;
    /// stemmen ze overeen, dan blijft het stil: dat is het normale geval).
    /// Wijzigt alleen de getrackte <paramref name="src"/>-entity; de
    /// aanroeper (ScanOneAsync/ScanAsync) bewaart via de bestaande
    /// SaveChangesAsync na elke bron.</summary>
    private async Task ClassifyContentKindAsync(Source src, string content, CancellationToken ct)
    {
        string? raw;
        try
        {
            raw = await ai.AskAsync(
                SourceContentKind.BuildPrompt(src.Name, src.Url, content), SourceContentKind.SystemPrompt, ct: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            raw = null;
        }

        var kind = raw is null ? null : SourceContentKind.Parse(raw);
        if (kind is not null)
        {
            src.ContentKind = kind;
            src.ContentKindSource = SourceContentKind.LlmOrigin;

            // #188-review, fix E: disagreement tussen LLM en heuristiek
            // zichtbaar maken zonder te blokkeren — het LLM-oordeel wint en
            // wordt gewoon opgeslagen, maar de beheerder kan de afwijking
            // terugvinden (en zo nodig via de content-kind-override
            // corrigeren of bevestigen). De consensus-poort in de retractie
            // (ClarificationMiningService) is de plek waar een afwijking
            // ook echt gedrag tegenhoudt.
            var heuristic = SourceContentKind.HeuristicKind(src.Id, src.Url, src.Name);
            if (kind != heuristic)
                db.RunLogs.Add(new RunLog
                {
                    Kind = "content-kind", Ref = src.Id, Status = "info",
                    Detail = $"LLM-oordeel '{kind}' wijkt af van de heuristiek ('{heuristic}') — "
                             + "LLM-oordeel opgeslagen; corrigeer of bevestig zo nodig via de "
                             + "content-kind-override",
                });
            return;
        }

        src.ContentKind = SourceContentKind.HeuristicKind(src.Id, src.Url, src.Name);
        src.ContentKindSource = SourceContentKind.HeuristicOrigin;
        db.RunLogs.Add(new RunLog
        {
            Kind = "content-kind", Ref = src.Id, Status = "info",
            Detail = raw is null
                ? "rb-ai niet beschikbaar — heuristische bron-type-classificatie toegepast"
                : "LLM-antwoord onbruikbaar voor bron-type-classificatie — heuristiek toegepast. "
                  + $"Respons (afgekapt): {LlmJson.Snippet(raw, 200)}",
        });
    }
}
