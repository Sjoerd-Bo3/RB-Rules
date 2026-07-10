using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record IngestResult(string SourceId, string Status, string? Detail = null);

/// <summary>Scan-pipeline (port van de PoP-runner, met audit-fixes):
/// fetch → boilerplate-strip → hash → diff → AI-classify → store + log.</summary>
public class IngestService(RbRulesDbContext db, HttpClient http, RbAiClient ai)
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public async Task<List<IngestResult>> ScanAsync(
        bool onlyDue, string? sourceId = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.Sources.Where(s => s.Enabled);
        if (sourceId is not null) query = db.Sources.Where(s => s.Id == sourceId);
        var sources = await query
            .OrderBy(s => s.TrustTier).ThenByDescending(s => s.Rank)
            .ToListAsync(ct);

        var results = new List<IngestResult>();
        foreach (var src in sources)
        {
            if (onlyDue && !Scheduling.IsDue(src.Cadence, src.LastChecked, now)) continue;
            var r = await ScanOneAsync(src, ct);
            results.Add(r);
            db.RunLogs.Add(new RunLog { Kind = "scan", Ref = src.Id, Status = r.Status, Detail = r.Detail });
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

            var raw = await res.Content.ReadAsStringAsync(ct);
            // Alleen html geïmplementeerd; 'pdf' volgt in S2 met een echte parser
            // (audit-fix: nooit stilletjes binaire bytes als tekst opslaan).
            if (src.Parser != "html")
                return new(src.Id, "error", $"parser '{src.Parser}' nog niet ondersteund");

            var text = TextUtils.HtmlToText(TextUtils.StripBoilerplate(raw));
            var hash = TextUtils.Sha256(text);
            if (src.LastHash == hash) return new(src.Id, "unchanged");

            var prevDoc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);

            db.Documents.Add(new Document { SourceId = src.Id, Content = text, ContentHash = hash });

            var isNew = src.LastHash is null;
            if (!isNew)
            {
                var diff = DiffUtils.LineDiff(prevDoc?.Content ?? "", text);
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
