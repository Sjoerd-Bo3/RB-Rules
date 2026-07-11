using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ParentSection(string Code, string Text);

/// <summary>Ouderketen-teksten bij regelsecties (#39): "466.2.c" is zonder
/// § 466.2 en § 466 onleesbaar. Eén helper voor citaties én sectiepagina.</summary>
public static class RuleParentLookup
{
    private const int MaxParentText = 300;

    /// <summary>Haalt per (sourceId, code) de ouderketen op; één query per bron.</summary>
    public static async Task<Dictionary<(string SourceId, string Code), List<ParentSection>>> FetchAsync(
        RbRulesDbContext db,
        IReadOnlyCollection<(string SourceId, string Code)> sections,
        CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), List<ParentSection>>();
        foreach (var group in sections.Distinct().GroupBy(s => s.SourceId))
        {
            var wanted = group
                .SelectMany(s => RuleSectionParser.ParentCodes(s.Code))
                .Distinct()
                .ToList();
            if (wanted.Count == 0) continue;

            var sourceId = group.Key;
            var rows = await db.RuleChunks.AsNoTracking()
                .Where(c => c.SourceId == sourceId && c.SectionCode != null &&
                            wanted.Contains(c.SectionCode))
                .OrderBy(c => c.ChunkIndex)
                .Select(c => new { c.SectionCode, c.Text })
                .ToListAsync(ct);
            var byCode = rows
                .GroupBy(r => r.SectionCode!)
                .ToDictionary(g => g.Key, g =>
                {
                    var t = g.First().Text;
                    return t.Length <= MaxParentText ? t : t[..MaxParentText] + "…";
                });

            foreach (var s in group)
            {
                result[(s.SourceId, s.Code)] = [.. RuleSectionParser.ParentCodes(s.Code)
                    .Where(byCode.ContainsKey)
                    .Select(code => new ParentSection(code, byCode[code]))];
            }
        }
        return result;
    }
}
