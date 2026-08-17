using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

/// <summary>Alleen de tekstvelden die een change nodig heeft om er een
/// geraakt domein uit af te leiden — losgekoppeld van de feed-DTO's zodat
/// zowel de publieke feed (<see cref="ChangeFeedService"/>) als het
/// admin-overzicht (<see cref="AdminOverviewService"/>) dezelfde resolver
/// kunnen voeden.</summary>
public readonly record struct ChangeTextRow(
    long Id, string ChangeType, string? Summary, string? Diff, string? Meaning);

/// <summary>Domein-kleurcodering per wijziging (#214). Read-time afleiding —
/// geen kolom, geen migratie: het domein volgt uit de <em>geraakte kaart(en)</em>
/// van een change via de gestructureerde ban-/errata-laag (die de kaartnaam al
/// naar <see cref="RbRules.Domain.Card.RiftboundId"/> matchte en dus haar
/// <see cref="RbRules.Domain.Card.Domains"/> kent).
///
/// <para><b>Bron per type.</b> Alleen <c>ban</c> en <c>errata</c> hebben een
/// gestructureerde kaart-laag; hun kandidatenpoel is dus klein (de daadwerkelijk
/// verboden/geërrata'de kaarten, niet de hele kaartenbak). Alle andere types
/// (<c>core-rule</c>/<c>tournament-rule</c>/<c>set-release</c>/<c>editorial</c>/
/// <c>clarification</c>/<c>unknown</c>) noemen zelden één specifieke kaart en
/// vallen bewust terug op geen domein (Colorless-neutraal in de UI).</para>
///
/// <para><b>Matching.</b> Een kandidaatkaart telt als "geraakt" wanneer haar
/// naam (≥ 4 tekens, zelfde drempel als <see cref="RelationMiningService"/> —
/// kortere namen geven te veel toevalstreffers) letterlijk in de change-tekst
/// (Summary + Diff + Meaning) voorkomt.</para>
///
/// <para><b>Eén domein of neutraal.</b> Een change kan meerdere kaarten raken,
/// en een kaart kan zelf meerdere domeinen dragen. De regel is bewust simpel en
/// voorspelbaar: precies één onderscheiden domein over álle geraakte kaarten →
/// dat domein kleurt de streep; nul of meer dan één (ambigu) → geen domein
/// (neutrale streep). Zo hoeft de UI nooit een willekeurige "winnaar" te
/// verzinnen.</para></summary>
public static class ChangeDomains
{
    private const int MinNameLength = 4;

    /// <summary>Leidt per change-id het geraakte domein af (of null =
    /// Colorless-neutraal). Doet hooguit twee kleine queries (ban-/errata-
    /// kaarten) en scant daarna in-memory — er is niets te resolven als de
    /// changeset geen ban/errata bevat.</summary>
    public static async Task<Dictionary<long, string?>> ResolveAsync(
        RbRulesDbContext db, IReadOnlyList<ChangeTextRow> changes, CancellationToken ct = default)
    {
        var result = changes.ToDictionary(c => c.Id, _ => (string?)null);
        var needBans = changes.Any(c => IsType(c.ChangeType, "ban"));
        var needErrata = changes.Any(c => IsType(c.ChangeType, "errata"));
        if (!needBans && !needErrata) return result;

        var banCards = needBans ? await CardsForBansAsync(db, ct) : [];
        var errataCards = needErrata ? await CardsForErrataAsync(db, ct) : [];

        foreach (var ch in changes)
        {
            var pool = IsType(ch.ChangeType, "ban") ? banCards
                : IsType(ch.ChangeType, "errata") ? errataCards
                : null;
            if (pool is null || pool.Count == 0) continue;
            result[ch.Id] = Pick(ch, pool);
        }
        return result;
    }

    private static string? Pick(ChangeTextRow ch, IReadOnlyList<DomainCard> pool)
    {
        var text = string.Join('\n', new[] { ch.Summary, ch.Diff, ch.Meaning }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Length == 0) return null;

        var domains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in pool)
        {
            if (card.Name.Length < MinNameLength) continue;
            if (!text.Contains(card.Name, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var d in card.Domains) domains.Add(d);
            if (domains.Count > 1) return null; // ambigu → neutraal
        }
        return domains.Count == 1 ? domains.First() : null;
    }

    private static bool IsType(string type, string want) =>
        string.Equals(type, want, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<DomainCard>> CardsForBansAsync(RbRulesDbContext db, CancellationToken ct)
    {
        var rows = await db.BanEntries.AsNoTracking()
            .Where(b => b.CardRiftboundId != null)
            .Join(db.Cards, b => b.CardRiftboundId, c => c.RiftboundId,
                (b, c) => new { c.Name, c.Domains })
            .ToListAsync(ct);
        return Dedupe(rows.Select(r => new DomainCard(r.Name, r.Domains)));
    }

    private static async Task<List<DomainCard>> CardsForErrataAsync(RbRulesDbContext db, CancellationToken ct)
    {
        var rows = await db.Errata.AsNoTracking()
            .Where(e => e.CardRiftboundId != null)
            .Join(db.Cards, e => e.CardRiftboundId, c => c.RiftboundId,
                (e, c) => new { c.Name, c.Domains })
            .ToListAsync(ct);
        return Dedupe(rows.Select(r => new DomainCard(r.Name, r.Domains)));
    }

    private static List<DomainCard> Dedupe(IEnumerable<DomainCard> cards) => cards
        .Where(c => c.Domains.Length > 0)
        .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();

    private readonly record struct DomainCard(string Name, string[] Domains);
}
