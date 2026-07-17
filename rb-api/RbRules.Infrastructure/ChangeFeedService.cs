using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

/// <summary>Eén bevestiging (#206) van een geconsolideerde primaire change:
/// een secundaire change (andere bron, zelfde gebeurtenis) die naar de
/// primaire wijst via <see cref="RbRules.Domain.Change.ConsolidatedWithId"/>.
/// Source.Url is een geregistreerde, admin-beheerde kolom — zelfde
/// vertrouwensniveau als de <c>SourceUrl</c> die dit endpoint al ongefilterd
/// serveert voor de primaire zelf, dus geen aparte UrlGuard-Safe-vlag nodig
/// (dat patroon (#184) is voor vrije/LLM-tekst-URL's zoals bij claims/
/// correcties, niet voor het bronnenregister).</summary>
public record ChangeFeedConfirmation(
    long Id, string SourceId, string SourceName, string SourceUrl, short TrustTier,
    string? Summary, DateTimeOffset DetectedAt);

/// <summary>Eén rij in de wijzigingen-feed (#206): een PRIMAIRE change
/// (<c>ConsolidatedWithId == null</c>) met de secundaire changes die
/// hetzelfde gebeurtenis vanuit een andere bron bevestigen, genest onder
/// haar in plaats van als losse kaart. Ongewijzigd t.o.v. vóór #206 als er
/// geen bevestigingen zijn (lege lijst).</summary>
public record ChangeFeedItem(
    long Id, string SourceId, string ChangeType, string Severity,
    string? Summary, string? Meaning, string? Diff, DateTimeOffset DetectedAt,
    string SourceName, string SourceUrl, short TrustTier,
    IReadOnlyList<ChangeFeedConfirmation> ConfirmedBy);

/// <summary>Changeconsolidatie-presentatie (#206): gedeeld tussen het
/// publieke <c>/api/changes</c>-endpoint en het admin-overzicht
/// (<c>/api/admin/overview/changes</c>) — beide tonen dezelfde
/// geconsolideerde lijst (primaire changes met genestelde bevestigingen),
/// dus één plek voor de query in plaats van twee keer dezelfde vorm.
/// Consolidatie zelf is een presentatie-koppeling (geen inhoudelijke
/// waarheid, dat blijft bij de structured BanEntry-/errata-precedentie
/// #168) — deze service raakt dan ook nooit <see
/// cref="RbRules.Domain.Change.ChangeType"/>/Summary/Diff, alleen hoe de
/// lijst gegroepeerd wordt.</summary>
public class ChangeFeedService(RbRulesDbContext db)
{
    /// <summary>Publieke feed (<c>/api/changes</c>): alleen primaire changes
    /// (secundaire changes verdwijnen uit de hoofdlijst, #206), met de
    /// bestaande severity/type/source-filters — die filteren op de PRIMAIRE
    /// change zelf, niet op een bevestiging (kleinste, voorspelbaarste
    /// gedrag; deze filters worden vandaag niet door rb-web gebruikt).</summary>
    public async Task<List<ChangeFeedItem>> ListAsync(
        string? severity, string? type, string? source, int take, CancellationToken ct = default)
    {
        var query = db.Changes.Where(c => c.ConsolidatedWithId == null);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(c => c.Severity == severity);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(c => c.ChangeType == type);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);

        var primaries = await query
            .OrderByDescending(c => c.DetectedAt)
            .Take(take)
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.SourceId, c.ChangeType, c.Severity,
                c.Summary, c.Meaning, c.Diff, c.DetectedAt,
                SourceName = s.Name, SourceUrl = s.Url, s.TrustTier,
            })
            .ToListAsync(ct);

        var confirmations = await ConfirmationsByPrimaryIdAsync(
            [.. primaries.Select(p => p.Id)], ct);

        return [.. primaries.Select(p => new ChangeFeedItem(
            p.Id, p.SourceId, p.ChangeType, p.Severity, p.Summary, p.Meaning, p.Diff, p.DetectedAt,
            p.SourceName, p.SourceUrl, p.TrustTier,
            confirmations.GetValueOrDefault(p.Id, [])))];
    }

    /// <summary>Bevestigingen per primaire change-id, oplopend gesorteerd op
    /// detectiemoment (eerste bevestiging eerst). Lege dictionary-waarde
    /// (nooit ontbrekend) voor een primaire zonder bevestiging —
    /// aanroepers gebruiken <c>GetValueOrDefault(id, [])</c>.</summary>
    public async Task<Dictionary<long, List<ChangeFeedConfirmation>>> ConfirmationsByPrimaryIdAsync(
        IReadOnlyList<long> primaryIds, CancellationToken ct = default)
    {
        if (primaryIds.Count == 0) return [];

        var rows = await db.Changes
            .Where(c => c.ConsolidatedWithId != null && primaryIds.Contains(c.ConsolidatedWithId!.Value))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                PrimaryId = c.ConsolidatedWithId!.Value,
                c.Id, c.SourceId, SourceName = s.Name, SourceUrl = s.Url, s.TrustTier,
                c.Summary, c.DetectedAt,
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.PrimaryId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.DetectedAt)
                .Select(r => new ChangeFeedConfirmation(
                    r.Id, r.SourceId, r.SourceName, r.SourceUrl, r.TrustTier, r.Summary, r.DetectedAt))
                .ToList());
    }
}
