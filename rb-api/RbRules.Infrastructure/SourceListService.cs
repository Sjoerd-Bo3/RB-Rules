using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Eén rij in de beheer-bronnenlijst (#180): de bestaande
/// Source-velden plus de negeer-status en de goedkoop-berekende
/// negeer-kandidaat-vlag. Bewust GEEN paginering/aparte tabel — de
/// bronnenlijst is klein genoeg om in één keer te tonen (zelfde aanname als
/// de bestaande publieke <c>/api/sources</c>).</summary>
public record SourceListItem(
    string Id, string Name, string Url, string Type, short TrustTier, int Rank,
    string Parser, string Cadence, bool Enabled, DateTimeOffset? LastChecked,
    string? FeedId, DateTimeOffset? PublishedAt, DateTimeOffset? UpdatedAt,
    string? ContentKind, string? ContentKindSource,
    DateTimeOffset? IgnoredAt, string? IgnoreReason, bool IsIgnoreCandidate);

/// <summary>Bronnenlijst-projectie voor het beheer (#180): dezelfde bronnen
/// als de publieke <c>/api/sources</c> (incl. genegeerde — de UI filtert
/// client-side, zelfde patroon als de andere lijsten op de admin-pagina),
/// plus de negeer-kandidaat-vlag ("levert niets op — negeren?"). Dat laatste
/// is bewust LICHTER dan <see cref="SourceDossierService"/> (#171, de diepe
/// per-bron opbrengst mét doorkliks): hier volstaan vier gebatchte
/// group-by's over de hele bronnenlijst — geen aparte query per bron, dus
/// geen N+1 ongeacht het aantal bronnen.</summary>
public class SourceListService(RbRulesDbContext db)
{
    public async Task<List<SourceListItem>> ListAsync(CancellationToken ct = default)
    {
        var sources = await db.Sources.AsNoTracking()
            .OrderBy(s => s.TrustTier).ThenByDescending(s => s.Rank)
            .ToListAsync(ct);
        if (sources.Count == 0) return [];

        // Voltooide scans per bron: een "scan"-run_log-regel met een andere
        // status dan "error" telt mee — een mislukte poging zegt nog niets
        // over of de bron zelf iets oplevert.
        var scanCounts = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "scan" && l.Ref != null && l.Status != "error")
            .GroupBy(l => l.Ref!)
            .Select(g => new { Ref = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Ref, x => x.Count, ct);

        var changeCounts = await db.Changes.AsNoTracking()
            .GroupBy(c => c.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct);

        // ClaimSource kent een unieke index op (ClaimId, SourceId) — één rij
        // per bron-claim-combinatie, dus een simpele rij-telling per bron
        // volstaat (geen Distinct nodig).
        var claimCounts = await db.ClaimSources.AsNoTracking()
            .GroupBy(cs => cs.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct);

        // Rulings (#177 clarify-mining): Provenance draagt de bron-id direct
        // ("clarify-mining:{sourceId}") — geen URL-normalisatie nodig zoals
        // SourceDossierService dat voor bans/errata/rulings via SourceUrl
        // moet doen. In één keer ophalen en in-memory groeperen (de set is
        // per definitie klein: alleen clarify-mining-Corrections).
        var rulingCounts = (await db.Corrections.AsNoTracking()
                .Where(c => c.Provenance != null
                            && c.Provenance.StartsWith(ClarificationMiningService.ProvenancePrefix))
                .Select(c => c.Provenance!)
                .ToListAsync(ct))
            .GroupBy(p => p[ClarificationMiningService.ProvenancePrefix.Length..])
            .ToDictionary(g => g.Key, g => g.Count());

        return sources.Select(s =>
        {
            var candidate = s.IgnoredAt is null && SourceIgnoreCandidacy.Evaluate(
                scanCounts.GetValueOrDefault(s.Id),
                changeCounts.GetValueOrDefault(s.Id),
                claimCounts.GetValueOrDefault(s.Id),
                rulingCounts.GetValueOrDefault(s.Id));
            return new SourceListItem(
                s.Id, s.Name, s.Url, s.Type, s.TrustTier, s.Rank, s.Parser, s.Cadence, s.Enabled,
                s.LastChecked, s.FeedId, s.PublishedAt, s.UpdatedAt, s.ContentKind, s.ContentKindSource,
                s.IgnoredAt, s.IgnoreReason, candidate);
        }).ToList();
    }
}
