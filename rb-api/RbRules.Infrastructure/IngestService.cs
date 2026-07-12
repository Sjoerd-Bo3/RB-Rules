using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record IngestResult(string SourceId, string Status, string? Detail = null);

/// <summary>Scan-pipeline (port van de PoP-runner, met audit-fixes):
/// fetch → boilerplate-strip → hash → diff → AI-classify → store + log.
/// Sluit af met een naclassificatie-ronde voor changes die eerder zonder
/// samenvatting zijn opgeslagen (#58).</summary>
public class IngestService(
    RbRulesDbContext db, HttpClient http, RbAiClient ai, ChangeClassificationService classifier)
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
        var query = db.Sources.Where(s => s.Enabled);
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
            var hash = TextUtils.Sha256(text);
            if (src.LastHash == hash) return new(src.Id, "unchanged");

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
}
