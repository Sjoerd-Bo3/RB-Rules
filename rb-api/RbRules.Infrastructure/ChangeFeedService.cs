using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

/// <summary>Eén bevestiging (#206) van een geconsolideerde primaire change:
/// een secundaire change (andere bron, zelfde gebeurtenis) die naar de
/// primaire wijst via <see cref="RbRules.Domain.Change.ConsolidatedWithId"/>.
/// Draagt ook Meaning en Diff (review-fix finding 3): de secundaire details
/// blijven ná consolidatie gewoon inspecteerbaar — dezelfde uitklap als de
/// primaire kaart, geen apart detail-endpoint nodig. Source.Url is een
/// geregistreerde, admin-beheerde kolom — zelfde vertrouwensniveau als de
/// <c>SourceUrl</c> die dit endpoint al ongefilterd serveert voor de
/// primaire zelf, dus geen aparte UrlGuard-Safe-vlag nodig (dat patroon
/// (#184) is voor vrije/LLM-tekst-URL's zoals bij claims/correcties, niet
/// voor het bronnenregister).</summary>
public record ChangeFeedConfirmation(
    long Id, string SourceId, string SourceName, string SourceUrl, short TrustTier,
    string? Summary, string? Meaning, string? Diff, DateTimeOffset DetectedAt);

/// <summary>Uitkomst van de feed-curatie-delete (#206 review-fix, finding 9):
/// <see cref="RemovedConfirmations"/> telt de secundairen die met een
/// primaire zijn meeverwijderd.</summary>
public record ChangeDeleteResult(bool Found, int RemovedConfirmations);

/// <summary>Eén rij in de wijzigingen-feed (#206): een PRIMAIRE change
/// (<c>ConsolidatedWithId == null</c>) met de secundaire changes die
/// hetzelfde gebeurtenis vanuit een andere bron bevestigen, genest onder
/// haar in plaats van als losse kaart. Ongewijzigd t.o.v. vóór #206 als er
/// geen bevestigingen zijn (lege lijst).</summary>
public record ChangeFeedItem(
    long Id, string SourceId, string ChangeType, string Severity,
    string? Summary, string? Meaning, string? Diff, DateTimeOffset DetectedAt,
    string SourceName, string SourceUrl, short TrustTier,
    IReadOnlyList<ChangeFeedConfirmation> ConfirmedBy,
    // Domein-kleurcodering (#214): read-time afgeleid uit de geraakte kaart(en)
    // via de ban-/errata-laag (zie ChangeDomains). Null = geen/ambigu domein
    // → Colorless-neutraal in de UI.
    string? Domain);

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
    /// gedrag; deze filters worden vandaag niet door rb-web gebruikt).
    /// Editorials (#207) verschijnen nooit als zelfstandige kaart op de
    /// publieke pagina ("volgorde gewijzigd; inhoud ongewijzigd" is ruis
    /// voor spelers) — read-time gefilterd, dus bestaande rijen verdwijnen
    /// direct. "unknown" blijft wél zichtbaar (kan echte inhoud zijn die op
    /// de #58-naclassificatie wacht), en een editorial die SECUNDAIRE is van
    /// een niet-editoriale primaire blijft gewoon als bevestiging werken
    /// (het filter geldt alleen voor de hoofdlijst, niet voor ConfirmedBy).
    /// Het admin-overzicht (<see cref="AdminOverviewService.ChangesAsync"/>,
    /// eigen query) toont editorials wél — inspectie/verwijderen blijft
    /// mogelijk.</summary>
    public async Task<List<ChangeFeedItem>> ListAsync(
        string? severity, string? type, string? source, int take, CancellationToken ct = default)
    {
        var query = db.Changes.Where(c =>
            c.ConsolidatedWithId == null && c.ChangeType != "editorial");
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
        var domains = await ChangeDomains.ResolveAsync(db,
            [.. primaries.Select(p => new ChangeTextRow(p.Id, p.ChangeType, p.Summary, p.Diff, p.Meaning))], ct);

        return [.. primaries.Select(p => new ChangeFeedItem(
            p.Id, p.SourceId, p.ChangeType, p.Severity, p.Summary, p.Meaning, p.Diff, p.DetectedAt,
            p.SourceName, p.SourceUrl, p.TrustTier,
            confirmations.GetValueOrDefault(p.Id, []),
            domains.GetValueOrDefault(p.Id)))];
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
                c.Summary, c.Meaning, c.Diff, c.DetectedAt,
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.PrimaryId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.DetectedAt)
                .Select(r => new ChangeFeedConfirmation(
                    r.Id, r.SourceId, r.SourceName, r.SourceUrl, r.TrustTier,
                    r.Summary, r.Meaning, r.Diff, r.DetectedAt))
                .ToList());
    }

    /// <summary>Feed-curatie-delete (#206 review-fix, finding 9): een
    /// primaire verwijderen verwijdert óók haar secundairen — het is per
    /// definitie hetzelfde event, en de kale FK-SetNull zou de kaart anders
    /// meteen laten herrijzen vanuit de andere bron. Eén SaveChanges = één
    /// transactie. Een secundaire los verwijderen blijft gewoon kunnen
    /// (die heeft zelf nooit secundairen — nooit ketens).</summary>
    public async Task<ChangeDeleteResult> DeleteAsync(long id, CancellationToken ct = default)
    {
        var change = await db.Changes.FindAsync([id], ct);
        if (change is null) return new(false, 0);

        var secondaries = await db.Changes
            .Where(c => c.ConsolidatedWithId == id)
            .ToListAsync(ct);
        db.Changes.RemoveRange(secondaries);
        db.Changes.Remove(change);
        await db.SaveChangesAsync(ct);
        return new(true, secondaries.Count);
    }
}
