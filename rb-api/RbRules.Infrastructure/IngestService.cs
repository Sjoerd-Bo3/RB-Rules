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
                    break;
                }
                case "pdf":
                {
                    // src.Url is de ontdek-pagina (Rules Hub); de PDF-link wordt
                    // per run gevonden (versies wisselen — nooit hardcoden).
                    var hubHtml = await res.Content.ReadAsStringAsync(ct);
                    var keyword = src.Id.Contains("tournament", StringComparison.OrdinalIgnoreCase)
                        ? "tournament" : "core";
                    var pdfUrl = PdfDiscovery.FindPdfUrl(hubHtml, keyword, new Uri(src.Url));
                    if (pdfUrl is null)
                        return new(src.Id, "error", $"geen '{keyword}'-PDF-link gevonden op {src.Url}");

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

    private async Task<Classification?> ClassifyAsync(string sourceName, string diff, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(diff)) return null;
        var raw = await ai.AskAsync(Classifier.BuildPrompt(sourceName, diff), Classifier.SystemPrompt, ct: ct);
        return raw is null ? null : Classifier.Parse(raw);
    }
}
