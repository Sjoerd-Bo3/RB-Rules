using Microsoft.EntityFrameworkCore;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

public static class RuleEndpoints
{
    public static void MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Regels-browser ─────────────────────────────────────────────
        app.MapGet("/api/rules/toc", async (RbRulesDbContext db) =>
        {
            var rows = await db.RuleChunks
                .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
                .OrderBy(c => c.ChunkIndex)
                .Select(c => new
                {
                    c.SourceId, c.SectionCode, c.ChunkIndex,
                    Preview = c.Text.Substring(0, Math.Min(c.Text.Length, 140)),
                })
                .ToListAsync();
            var sources = await db.Sources.ToDictionaryAsync(s => s.Id, s => s.Name);
            var toc = rows
                .GroupBy(r => r.SourceId)
                .Select(g => new
                {
                    SourceId = g.Key,
                    SourceName = sources.GetValueOrDefault(g.Key, g.Key),
                    Sections = g.GroupBy(r => r.SectionCode!)
                        .Select(sg => new
                        {
                            Code = sg.Key,
                            Preview = sg.OrderBy(x => x.ChunkIndex).First().Preview,
                            Index = sg.Min(x => x.ChunkIndex),
                        })
                        .OrderBy(s => s.Index)
                        .Select(s => new { s.Code, s.Preview })
                        .ToList(),
                })
                .OrderBy(g => g.SourceId);
            return Results.Ok(toc);
        });

        app.MapGet("/api/rules/section/{code}", async (string code, string? source, RbRulesDbContext db) =>
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
                .ToListAsync();
            if (chunks.Count == 0) return Results.NotFound();

            // PDF-deeplink: werkelijke bestands-URL + beginpagina van de sectie.
            var fileUrl = await db.Documents
                .Where(d => d.Id == chunks[0].DocumentId)
                .Select(d => d.FileUrl)
                .FirstOrDefaultAsync();

            // Bij codes die in meerdere bronnen voorkomen: houd één bron aan.
            var srcId = chunks[0].SourceId;
            chunks = [.. chunks.Where(c => c.SourceId == srcId)];

            // Buursecties in leesvolgorde van dezelfde bron.
            var codes = await db.RuleChunks
                .Where(c => c.SourceId == srcId && c.SectionCode != null &&
                            c.SectionCode != "" && c.SectionCode != "intro")
                .OrderBy(c => c.ChunkIndex)
                .Select(c => c.SectionCode!)
                .ToListAsync();
            var distinct = codes.Distinct().ToList();
            var idx = distinct.IndexOf(code);

            // Ouderketen (#39): subregels tonen hun bovenliggende regels mee.
            var parents = await RuleParentLookup.FetchAsync(db, [(srcId, code)]);

            return Results.Ok(new
            {
                Code = code,
                SourceId = srcId,
                chunks[0].SourceName,
                chunks[0].SourceUrl,
                Text = string.Join("\n\n", chunks.Select(c => c.Text)),
                PdfUrl = fileUrl,
                chunks[0].Page,
                Parents = parents.GetValueOrDefault((srcId, code)) ?? [],
                Prev = idx > 0 ? distinct[idx - 1] : null,
                Next = idx >= 0 && idx < distinct.Count - 1 ? distinct[idx + 1] : null,
            });
        });
    }
}
