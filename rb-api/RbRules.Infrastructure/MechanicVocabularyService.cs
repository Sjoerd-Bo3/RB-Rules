using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record MechanicKeywordItem(
    long Id, string Term, string Status, int Occurrences,
    DateTimeOffset FirstSeen, DateTimeOffset? ReviewedAt);

public record KeywordAcceptResult(string Term, int RequeuedCards);

public record KeywordCardItem(
    string Id, string Name, string Before, string Match, string After);
public record KeywordCardsResult(
    string Term, int Total, IReadOnlyList<KeywordCardItem> Items);

/// <summary>Beheer van het groeiende mechaniek-vocabulaire (#52): kandidaten
/// uit de miner accepteren of verwerpen. Accepteren maakt de term onderdeel
/// van het mining-vocabulaire én zet de kaarten met dat keyword terug in de
/// mine-wachtrij (Mechanics = null), zodat de eerstvolgende mining-run —
/// scheduler-tick of handmatige job — ze met het nieuwe vocabulaire her-mined.</summary>
public class MechanicVocabularyService(RbRulesDbContext db)
{
    public async Task<IReadOnlyList<MechanicKeywordItem>> ListAsync(CancellationToken ct = default) =>
        await db.MechanicKeywords.AsNoTracking()
            .OrderBy(k => k.Status == "candidate" ? 0 : 1)
            .ThenByDescending(k => k.Occurrences)
            .ThenBy(k => k.Term)
            .Select(k => new MechanicKeywordItem(
                k.Id, k.Term, k.Status, k.Occurrences, k.FirstSeen, k.ReviewedAt))
            .ToListAsync(ct);

    public async Task<KeywordAcceptResult?> AcceptAsync(long id, CancellationToken ct = default)
    {
        var keyword = await db.MechanicKeywords.FindAsync([id], ct);
        if (keyword is null) return null;
        keyword.Status = "accepted";
        keyword.ReviewedAt = DateTimeOffset.UtcNow;
        // Eerst de status vastleggen, daarna pas de bulk-update — nooit
        // ExecuteUpdate mengen met getrackte entiteiten die nog SaveChanges krijgen.
        await db.SaveChangesAsync(ct);

        // Re-mine: kaarten met "[Term]" of "[Term N]" terug in de wachtrij.
        var exact = "[" + keyword.Term + "]";
        var withParameter = "[" + keyword.Term + " ";
        var requeued = await db.Cards
            .Where(c => c.VariantOf == null && c.TextPlain != null &&
                        (c.TextPlain.Contains(exact) || c.TextPlain.Contains(withParameter)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Mechanics, (string[]?)null)
                .SetProperty(c => c.Triggers, (string[]?)null)
                .SetProperty(c => c.Effects, (string[]?)null), ct);

        db.RunLogs.Add(new RunLog
        {
            Kind = "mine", Ref = $"keyword:{keyword.Term}", Status = "ok",
            Detail = $"keyword geaccepteerd — {requeued} kaarten opnieuw te minen",
        });
        await db.SaveChangesAsync(ct);
        return new(keyword.Term, requeued);
    }

    /// <summary>Bewijs bij een keyword-kandidaat (#123): de canonieke kaarten
    /// waarop de bracketed term voorkomt, met een snippet rond de term.
    /// Zelfde bracketed vormen als AcceptAsync ("[Term]" of "[Term N]"), maar
    /// case-insensitive (ILike) omdat de kandidaten-harvest ook
    /// case-insensitive dedupliceert. Begrensd op <paramref name="limit"/>;
    /// Total draagt de rest ("en N meer").</summary>
    public async Task<KeywordCardsResult?> CardsForKeywordAsync(
        long id, int limit = 20, CancellationToken ct = default)
    {
        var term = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Id == id)
            .Select(k => k.Term)
            .FirstOrDefaultAsync(ct);
        if (term is null) return null;

        var exact = "%[" + EscapeLike(term) + "]%";
        var withParameter = "%[" + EscapeLike(term) + " %";
        var query = db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.TextPlain != null &&
                        (EF.Functions.ILike(c.TextPlain!, exact, "\\") ||
                         EF.Functions.ILike(c.TextPlain!, withParameter, "\\")));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(c => c.Name).ThenBy(c => c.RiftboundId)
            .Take(limit)
            .Select(c => new { c.RiftboundId, c.Name, c.TextPlain })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            // Het ILike-voorfilter is iets ruimer dan de miner-match (bv.
            // "[Term up]"); zonder exacte voorkoming tonen we het tekstbegin
            // zonder markering in plaats van de kaart te verzwijgen.
            var s = MechanicMiner.SnippetFor(r.TextPlain, term)
                ?? new KeywordSnippet("", "", Truncate(r.TextPlain!, 120));
            return new KeywordCardItem(r.RiftboundId, r.Name, s.Before, s.Match, s.After);
        }).ToList();
        return new(term, total, items);
    }

    /// <summary>LIKE/ILIKE-metatekens onschadelijk maken (escape-teken is
    /// backslash, zie de ILike-aanroepen hierboven).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public async Task<bool> RejectAsync(long id, CancellationToken ct = default)
    {
        var keyword = await db.MechanicKeywords.FindAsync([id], ct);
        if (keyword is null) return false;
        keyword.Status = "rejected"; // blijft bewaard: wordt niet opnieuw voorgesteld
        keyword.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
