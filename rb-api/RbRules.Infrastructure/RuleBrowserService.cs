using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

public record RuleTocSection(string Code, string Preview);
public record RuleTocSource(
    string SourceId, string SourceName, IReadOnlyList<RuleTocSection> Sections);

public record RuleSection(
    string Code, string SourceId, string SourceName, string SourceUrl,
    string Text, string? PdfUrl, int? Page,
    IReadOnlyList<ParentSection> Parents, string? Prev, string? Next);

/// <summary>Regels-browser (#59, uit de endpoints): de hoofdstuk-hiërarchie
/// (toc) en één sectie met ouderketen, PDF-deeplink en buursecties.</summary>
public class RuleBrowserService(RbRulesDbContext db)
{
    public async Task<IReadOnlyList<RuleTocSource>> TocAsync(CancellationToken ct = default)
    {
        var rows = await db.RuleChunks
            .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new
            {
                c.SourceId, c.SectionCode, c.ChunkIndex,
                Preview = c.Text.Substring(0, Math.Min(c.Text.Length, 140)),
            })
            .ToListAsync(ct);
        var sources = await db.Sources.ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return [.. rows
            .GroupBy(r => r.SourceId)
            .Select(g => new RuleTocSource(
                g.Key,
                sources.GetValueOrDefault(g.Key, g.Key),
                [.. g.GroupBy(r => r.SectionCode!)
                    .Select(sg => new
                    {
                        Code = sg.Key,
                        Preview = sg.OrderBy(x => x.ChunkIndex).First().Preview,
                        Index = sg.Min(x => x.ChunkIndex),
                    })
                    .OrderBy(s => s.Index)
                    .Select(s => new RuleTocSection(s.Code, s.Preview))]))
            .OrderBy(g => g.SourceId)];
    }

    public async Task<RuleSection?> SectionAsync(
        string code, string? source, CancellationToken ct = default)
    {
        var query = db.RuleChunks.Where(c => c.SectionCode == code);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);
        var chunks = await query
            .OrderBy(c => c.ChunkIndex)
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.SourceId, SourceName = s.Name, SourceUrl = s.Url,
                c.ChunkIndex, c.Text, c.Page, c.DocumentId,
            })
            .ToListAsync(ct);
        if (chunks.Count == 0) return null;

        // PDF-deeplink: werkelijke bestands-URL + beginpagina van de sectie.
        var fileUrl = await db.Documents
            .Where(d => d.Id == chunks[0].DocumentId)
            .Select(d => d.FileUrl)
            .FirstOrDefaultAsync(ct);

        // Bij codes die in meerdere bronnen voorkomen: houd één bron aan.
        var srcId = chunks[0].SourceId;
        chunks = [.. chunks.Where(c => c.SourceId == srcId)];

        // Buursecties in leesvolgorde van dezelfde bron.
        var codes = await db.RuleChunks
            .Where(c => c.SourceId == srcId && c.SectionCode != null &&
                        c.SectionCode != "" && c.SectionCode != "intro")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.SectionCode!)
            .ToListAsync(ct);
        var distinct = codes.Distinct().ToList();
        var idx = distinct.IndexOf(code);

        // Ouderketen (#39): subregels tonen hun bovenliggende regels mee.
        var parents = await RuleParentLookup.FetchAsync(db, [(srcId, code)], ct);

        return new RuleSection(
            code, srcId, chunks[0].SourceName, chunks[0].SourceUrl,
            string.Join("\n\n", chunks.Select(c => c.Text)),
            fileUrl, chunks[0].Page,
            parents.GetValueOrDefault((srcId, code)) ?? [],
            idx > 0 ? distinct[idx - 1] : null,
            idx >= 0 && idx < distinct.Count - 1 ? distinct[idx + 1] : null);
    }
}
