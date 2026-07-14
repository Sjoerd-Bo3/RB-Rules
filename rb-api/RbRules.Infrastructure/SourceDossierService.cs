using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

// ── Bron-dossier (#171) ──────────────────────────────────────────────────

public record SourceDossierOrigin(string? FeedId, string? FeedName);

public record SourceYieldChange(
    long Id, string ChangeType, string Severity, string? Summary, DateTimeOffset DetectedAt);

public record SourceYieldBan(
    long Id, string Name, string? CardRiftboundId, string Kind, string Format,
    DateOnly? EffectiveFrom, DateTimeOffset DetectedAt);

public record SourceYieldErratum(
    long Id, string CardName, string? CardRiftboundId, DateTimeOffset DetectedAt);

public record SourceYieldRuling(
    long Id, string Scope, string Ref, string? Question, string Text, string Status,
    DateTimeOffset At);

public record SourceYieldClaim(
    long Id, string TopicType, string TopicRef, string Statement, string Status,
    DateTimeOffset LastSeen);

/// <summary>Opbrengst per soort (#171): aantal + een begrensde recente lijst
/// met doorkliks. Twee koppelvormen: Document/RuleChunk/Change dragen
/// SourceId; BanEntry/Erratum/Correction dragen SourceUrl (gematcht op
/// Source.Url, genormaliseerd); Claim koppelt via ClaimSource.SourceId (een
/// directe FK, geen URL-match).</summary>
public record SourceDossierYield(
    int Documents, DateTimeOffset? LastDocumentAt,
    int RuleChunks,
    int ChangesTotal, IReadOnlyList<SourceYieldChange> Changes,
    int BansTotal, IReadOnlyList<SourceYieldBan> Bans,
    int ErrataTotal, IReadOnlyList<SourceYieldErratum> Errata,
    int RulingsTotal, IReadOnlyList<SourceYieldRuling> Rulings,
    int ClaimsTotal, IReadOnlyList<SourceYieldClaim> Claims);

/// <summary>Eén run_log-regel die aan deze bron te koppelen is: een
/// claims-mining-stap voor de bron zelf (Ref = source.Id), of een
/// classify-stap voor een change die van deze bron komt (Ref =
/// "change:{id}", gematcht via Change.SourceId).</summary>
public record SourceDossierStep(string Kind, string Status, string? Detail, DateTimeOffset At);

public record SourceDossierScan(string Status, string? Detail, DateTimeOffset At);

public record SourceDossierProcessing(
    SourceDossierScan? LastScan,
    IReadOnlyList<SourceDossierStep> FollowUps,
    string CompletenessStatus,
    string CompletenessNote);

public record SourceDossier(
    string SourceId, string SourceName, short TrustTier,
    SourceDossierOrigin Origin, SourceDossierYield Yield, SourceDossierProcessing Processing);

/// <summary>Bron-dossier (#171, spiegelbeeld van #167): vanuit een bron zien
/// wat die aan het systeem heeft toegevoegd (herkomst + opbrengst) en of de
/// verwerking daarvan compleet is (run_log-gebaseerd signaal). Alles
/// bestaande data, alleen geprojecteerd (#127-patroon, CardDetailService)
/// — geen embeddings, geen LLM.</summary>
public class SourceDossierService(RbRulesDbContext db)
{
    private const int YieldListSize = 10;
    private const int FollowUpListSize = 15;

    public async Task<SourceDossier?> GetAsync(string id, CancellationToken ct = default)
    {
        var source = await db.Sources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (source is null) return null;

        var origin = await OriginAsync(source, ct);
        var yield = await YieldAsync(source, ct);
        var processing = await ProcessingAsync(source, yield, ct);

        return new(source.Id, source.Name, source.TrustTier, origin, yield, processing);
    }

    private async Task<SourceDossierOrigin> OriginAsync(Source source, CancellationToken ct)
    {
        if (source.FeedId is null) return new(null, null);
        var feedName = await db.SourceFeeds.AsNoTracking()
            .Where(f => f.Id == source.FeedId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(ct);
        return new(source.FeedId, feedName);
    }

    private async Task<SourceDossierYield> YieldAsync(Source source, CancellationToken ct)
    {
        var id = source.Id;

        // ── SourceId-koppelvorm ─────────────────────────────────────────
        var documents = await db.Documents.AsNoTracking().CountAsync(d => d.SourceId == id, ct);
        var lastDocumentAt = await db.Documents.AsNoTracking()
            .Where(d => d.SourceId == id)
            .OrderByDescending(d => d.RetrievedAt)
            .Select(d => (DateTimeOffset?)d.RetrievedAt)
            .FirstOrDefaultAsync(ct);
        var ruleChunks = await db.RuleChunks.AsNoTracking().CountAsync(c => c.SourceId == id, ct);

        var changesTotal = await db.Changes.AsNoTracking().CountAsync(c => c.SourceId == id, ct);
        var changes = await db.Changes.AsNoTracking()
            .Where(c => c.SourceId == id)
            .OrderByDescending(c => c.DetectedAt)
            .Take(YieldListSize)
            .Select(c => new SourceYieldChange(c.Id, c.ChangeType, c.Severity, c.Summary, c.DetectedAt))
            .ToListAsync(ct);

        // ── SourceUrl-koppelvorm — vergelijkingsvormen vóór de query's
        // bepalen (CONVENTIONS: eigen methodes horen niet in expression
        // trees), genormaliseerd zoals #167 dat al doet voor bron-URL's.
        var urlCandidates = UrlCandidates(source.Url);

        var bansTotal = await db.BanEntries.AsNoTracking()
            .CountAsync(b => urlCandidates.Contains(b.SourceUrl), ct);
        var bans = await db.BanEntries.AsNoTracking()
            .Where(b => urlCandidates.Contains(b.SourceUrl))
            .OrderByDescending(b => b.DetectedAt)
            .Take(YieldListSize)
            .Select(b => new SourceYieldBan(
                b.Id, b.Name, b.CardRiftboundId, b.Kind, b.Format, b.EffectiveFrom, b.DetectedAt))
            .ToListAsync(ct);

        var errataTotal = await db.Errata.AsNoTracking()
            .CountAsync(e => urlCandidates.Contains(e.SourceUrl), ct);
        var errata = await db.Errata.AsNoTracking()
            .Where(e => urlCandidates.Contains(e.SourceUrl))
            .OrderByDescending(e => e.DetectedAt)
            .Take(YieldListSize)
            .Select(e => new SourceYieldErratum(e.Id, e.CardName, e.CardRiftboundId, e.DetectedAt))
            .ToListAsync(ct);

        var rulingsTotal = await db.Corrections.AsNoTracking()
            .CountAsync(c => c.SourceRef != null && urlCandidates.Contains(c.SourceRef), ct);
        var rulings = await db.Corrections.AsNoTracking()
            .Where(c => c.SourceRef != null && urlCandidates.Contains(c.SourceRef))
            .OrderByDescending(c => c.VerifiedAt ?? c.CreatedAt)
            .Take(YieldListSize)
            .Select(c => new SourceYieldRuling(
                c.Id, c.Scope, c.Ref, c.Question, c.Text, c.Status, c.VerifiedAt ?? c.CreatedAt))
            .ToListAsync(ct);

        // ── Claims: directe FK via ClaimSource, geen URL-match ──────────
        var claimIds = await db.ClaimSources.AsNoTracking()
            .Where(cs => cs.SourceId == id)
            .Select(cs => cs.ClaimId)
            .Distinct()
            .ToListAsync(ct);
        var claimsTotal = claimIds.Count;
        List<SourceYieldClaim> claims = claimIds.Count == 0
            ? []
            : await db.Claims.AsNoTracking()
                .Where(c => claimIds.Contains(c.Id))
                .OrderByDescending(c => c.LastSeen)
                .Take(YieldListSize)
                .Select(c => new SourceYieldClaim(
                    c.Id, c.TopicType, c.TopicRef, c.Statement, c.Status, c.LastSeen))
                .ToListAsync(ct);

        return new(
            documents, lastDocumentAt, ruleChunks,
            changesTotal, changes,
            bansTotal, bans,
            errataTotal, errata,
            rulingsTotal, rulings,
            claimsTotal, claims);
    }

    private async Task<SourceDossierProcessing> ProcessingAsync(
        Source source, SourceDossierYield yield, CancellationToken ct)
    {
        var id = source.Id;

        var scanRow = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "scan" && l.Ref == id)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.Status, l.Detail, l.CreatedAt })
            .FirstOrDefaultAsync(ct);
        var lastScan = scanRow is null
            ? null
            : new SourceDossierScan(scanRow.Status, scanRow.Detail, scanRow.CreatedAt);

        // Claims-mining-stappen voor deze bron (Ref = source.Id).
        var claimSteps = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "claims" && l.Ref == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(FollowUpListSize)
            .Select(l => new SourceDossierStep("claims", l.Status, l.Detail, l.CreatedAt))
            .ToListAsync(ct);

        // Classify-stappen voor changes die van deze bron komen (Ref =
        // "change:{id}") — Change zelf draagt SourceId, run_log niet.
        var changeIds = await db.Changes.AsNoTracking()
            .Where(c => c.SourceId == id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var changeRefs = changeIds.Select(cid => $"change:{cid}").ToHashSet();
        List<SourceDossierStep> classifySteps = changeRefs.Count == 0
            ? []
            : await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == "classify" && l.Ref != null && changeRefs.Contains(l.Ref))
                .OrderByDescending(l => l.CreatedAt)
                .Take(FollowUpListSize)
                .Select(l => new SourceDossierStep("classify", l.Status, l.Detail, l.CreatedAt))
                .ToListAsync(ct);

        var followUps = claimSteps.Concat(classifySteps)
            .OrderByDescending(s => s.At)
            .Take(FollowUpListSize)
            .ToList();

        var anyFailed = followUps.Any(s => s.Status == "error");

        // Pending: claims-mining geldt alleen voor community-bronnen
        // (trust ≥ 3, zelfde poort als ClaimMiningService) en alleen als er
        // al een document is — anders is er simpelweg nog niets om te minen.
        var anyPending = false;
        if (source.TrustTier >= 3 && yield.Documents > 0)
        {
            var latestDocMined = await db.Documents.AsNoTracking()
                .Where(d => d.SourceId == id)
                .OrderByDescending(d => d.RetrievedAt)
                .Select(d => d.ClaimsMinedAt)
                .FirstAsync(ct);
            anyPending = latestDocMined is null;
        }

        var opbrengstTotaal = yield.RuleChunks + yield.ChangesTotal + yield.BansTotal
            + yield.ErrataTotal + yield.RulingsTotal + yield.ClaimsTotal;
        var status = SourceDossierCompleteness.Evaluate(
            lastScan?.Status, anyFailed, anyPending, opbrengstTotaal);

        return new(lastScan, followUps, status, SourceDossierCompleteness.Note(status));
    }

    /// <summary>Genormaliseerde varianten van de bron-URL om tegen SourceUrl/
    /// SourceRef te matchen (#171: "match SourceUrl op Source.Url,
    /// genormaliseerd") — letterlijke kandidaten vóór de query, dan een
    /// vertaalbare <c>Contains</c> op een gesloten lijst (geen TrimEnd per
    /// rij, dat vertaalt niet bewezen naar SQL).</summary>
    private static HashSet<string> UrlCandidates(string url)
    {
        var normalized = SourceScout.NormalizeUrl(url);
        return [url, normalized, normalized + "/"];
    }
}
