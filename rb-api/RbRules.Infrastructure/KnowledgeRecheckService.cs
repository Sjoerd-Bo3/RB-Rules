using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record KnowledgeRecheckResult(
    int Changes, int DocsMarked, int ClaimsRechecked, int ClaimsOutdated,
    int Deferred, string Message);

/// <summary>Kennis-levenscyclus (#119, het uitgestelde punt 3 van #52): een
/// verwerkte regelwijziging hertoetst de kennis die erop leunt in plaats van
/// die stil te laten verouderen. De AFFECTS-mapper (#104) is de enige bron
/// van "betrokken"; de doorsnede met docs/claims is puur Domain
/// (KnowledgeRecheck). Primer-docs op geraakte secties gaan terug naar draft
/// mét reden (kanttekening vooraan in de tekst — de beheerder ziet hem in de
/// reviewqueue, nooit stil terugzetten); betrokken accepted claims gaan
/// opnieuw door de bestaande official-check. Idempotent via een
/// run_log-grootboek (kind "recheck", ref "change:id" — een "ok"-regel pas
/// nadat álles voor die change slaagde, #93), gecapt en best-effort: wat
/// uitvalt blijft staan voor een volgende scan-afronding.</summary>
public class KnowledgeRecheckService(RbRulesDbContext db, ClaimMiningService claims)
{
    /// <summary>Zelfde venster-afweging als de naclassificatie (#58): ruim
    /// genoeg om dagen rb-ai-uitval te overbruggen, begrensd zodat de
    /// historie van vóór deze feature nooit als golf drafts binnenkomt.</summary>
    public static readonly TimeSpan RecheckWindow = TimeSpan.FromDays(14);

    /// <summary>Caps per run: de hertoets lift mee in de scan-afronding en
    /// mag die niet domineren — de rest volgt bij de volgende run.</summary>
    public const int MaxChangesPerRun = 10;
    public const int MaxClaimChecksPerRun = 15;

    public const string LedgerKind = "recheck";

    /// <param name="since">Ondergrens (venster): oudere changes blijven met
    /// rust — die dateren van vóór deze feature of zijn via de handmatige
    /// weg al beoordeeld.</param>
    /// <param name="before">Bovengrens: de aanroeper (scan-afronding) geeft
    /// hier zijn eigen starttijd door, zodat changes van de lopende scan
    /// wachten tot de her-index de nieuwe regelversie in rule_chunk heeft
    /// gezet — anders toetst de official-check tegen de oude tekst.</param>
    public async Task<KnowledgeRecheckResult> RunAsync(
        DateTimeOffset since, DateTimeOffset before,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Grootboek: alleen "ok"-regels gelden als afgehandeld — markeren na
        // succes (#93); een info-/error-regel laat de change staan voor een
        // volgende run.
        var handled = (await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == LedgerKind && l.Status == "ok"
                            && l.Ref != null && l.Ref.StartsWith("change:"))
                .Select(l => l.Ref!)
                .ToListAsync(ct))
            .ToHashSet();

        // "unknown" wacht op de naclassificatie (#58) die hiervóór draait: de
        // mapper kan er nog geen doelen uit afleiden en het type kan alsnog
        // ban/errata/core-rule worden — dus niet afvinken.
        // Bewust GEEN roots-only-filter (#206): de hertoets moet élke echte
        // detectie zien — ook een geconsolideerde secundaire is een reële
        // observatie die kennis kan raken; consolidatie is alleen
        // feed-presentatie, en het change:{id}-grootboek voorkomt al dubbel
        // werk per rij.
        var candidates = await db.Changes.AsNoTracking()
            .Where(c => c.DetectedAt >= since && c.DetectedAt < before
                        && c.ChangeType != "unknown")
            .OrderBy(c => c.DetectedAt)
            .Select(c => new { c.Id, c.ChangeType, c.Summary, c.Meaning, c.Diff })
            .ToListAsync(ct);
        var pending = candidates
            .Where(c => !handled.Contains($"change:{c.Id}"))
            .Take(MaxChangesPerRun)
            .ToList();
        if (pending.Count == 0)
            return new(0, 0, 0, 0, 0, "geen onverwerkte wijzigingen");

        // Lookups één keer, in dezelfde vorm als de graph-projectie (#104):
        // canonieke printings voor AFFECTS, alle printings voor naam→canoniek,
        // secties op bron-rank (voorkeursvolgorde bij gedeelde codes).
        var allCards = await db.Cards.AsNoTracking()
            .Select(c => new { c.RiftboundId, c.Name, c.VariantOf })
            .ToListAsync(ct);
        var rankBySource = await db.Sources.AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.Rank, ct);
        var sections = (await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && r.SectionCode != "")
                .Select(r => new { r.SourceId, Code = r.SectionCode! })
                .Distinct()
                .ToListAsync(ct))
            .OrderByDescending(s => rankBySource.GetValueOrDefault(s.SourceId))
            .ThenBy(s => s.SourceId).ThenBy(s => s.Code)
            .Select(s => (s.SourceId, s.Code))
            .ToList();

        var affects = ChangeAffectsMapper.Create(
            allCards.Where(c => c.VariantOf == null).Select(c => (c.RiftboundId, c.Name)),
            sections);
        // Mechanieken/concepten bewust leeg: de AFFECTS-mapper produceert
        // alleen kaart- en sectie-doelen, dus die topics kunnen nooit matchen.
        var topics = ClaimTopicMapper.Create(
            allCards.Select(c => (c.RiftboundId, c.Name, c.VariantOf)),
            mechanics: [], sections, concepts: []);

        var docs = await db.KnowledgeDocs
            .Where(k => k.Kind == "primer")
            .ToListAsync(ct);
        var acceptedClaims = await db.Claims
            .Where(c => c.Status == "accepted")
            .ToListAsync(ct);
        var officialSourceIds = await db.Sources.AsNoTracking()
            .Where(s => s.TrustTier == 1)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var docsById = docs.ToDictionary(d => d.Id);
        var claimsById = acceptedClaims.ToDictionary(c => c.Id);
        var docCandidates = docs
            .Select(d => new KnowledgeRecheck.DocCandidate(d.Id, d.SectionRefs))
            .ToList();
        var claimCandidates = acceptedClaims
            .Select(c => new KnowledgeRecheck.ClaimCandidate(c.Id, c.TopicType, c.TopicRef))
            .ToList();

        var docsMarked = 0;
        var rechecked = 0;
        var outdated = 0;
        var deferred = 0;
        var budget = MaxClaimChecksPerRun;
        var n = 0;
        foreach (var change in pending)
        {
            n++;
            progress?.Invoke($"wijziging {n}/{pending.Count} ({change.ChangeType}) hertoetsen");
            var plan = KnowledgeRecheck.PlanFor(
                change.Id, change.ChangeType, change.Summary, change.Meaning, change.Diff,
                affects, topics, docCandidates, claimCandidates);

            var changeDocs = 0;
            foreach (var mark in plan.Docs)
            {
                var doc = docsById[mark.DocId];
                var body = KnowledgeRecheck.AddMarker(doc.Body, KnowledgeRecheck.Marker(mark.Reason));
                if (body == doc.Body && doc.Status == "draft")
                    continue; // al gemarkeerd door een eerdere (deels mislukte) run
                doc.Body = body;
                doc.Status = "draft"; // nooit stil: de reden staat vooraan in de tekst
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                // Bewust geen her-embed: drafts doen niet mee in /ask en de
                // goedkeuring stript de kanttekening weer — de bestaande
                // embedding hoort bij de tekst zónder kanttekening.
                changeDocs++;
                docsMarked++;
            }

            var changeRechecked = 0;
            var changeOutdated = 0;
            string? defer = null;
            foreach (var id in plan.ClaimIds)
            {
                var claim = claimsById[id];
                if (claim.Status != "accepted")
                    continue; // al door een eerdere change in deze run verlegd
                if (claim.Embedding is null)
                {
                    defer = "claim zonder embedding — official-check kan geen §'s selecteren";
                    continue;
                }
                if (budget <= 0)
                {
                    defer = $"cap van {MaxClaimChecksPerRun} official-checks bereikt";
                    break;
                }
                budget--;

                string officialStatus;
                string? reason;
                string? degraded;
                try
                {
                    (officialStatus, reason, degraded) = await claims.CheckOfficialAsync(
                        claim.Statement, claim.Embedding, officialSourceIds, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    defer = $"official-check faalde: {ex.Message}";
                    continue;
                }
                if (officialStatus == "unchecked")
                {
                    // Geen oordeel (rb-ai weg, geen regelindex, onzin-output):
                    // de change blijft staan en de reden is herleidbaar.
                    defer = degraded ?? "official-check gaf geen oordeel";
                    continue;
                }

                changeRechecked++;
                rechecked++;
                claim.OfficialStatus = officialStatus;
                if (officialStatus == "contradicted")
                {
                    var (status, statusReason) = KnowledgeRecheck.ApplyContradicted(
                        change.Id, claim.Status, reason);
                    claim.Status = status;
                    claim.StatusReason = statusReason;
                    changeOutdated++;
                    outdated++;
                }
            }

            if (defer is not null)
            {
                deferred++;
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = $"change:{change.Id}", Status = "info",
                    Detail = $"hertoets onvolledig — blijft staan voor een volgende run: {defer}",
                });
            }
            else
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = $"change:{change.Id}", Status = "ok",
                    Detail = plan.IsEmpty
                        ? "geen betrokken kennis"
                        : $"betrokken: {plan.Docs.Count} primer-doc(s), {plan.ClaimIds.Count} claim(s)"
                          + $" — {changeDocs} naar draft, {changeRechecked} hertoetst"
                          + $" ({changeOutdated} verouderd)",
                });
            }
            await db.SaveChangesAsync(ct);
        }

        var message =
            $"{pending.Count - deferred} van {pending.Count} wijzigingen hertoetst: "
            + $"{docsMarked} primer-doc(s) naar draft, {rechecked} claim(s) opnieuw getoetst "
            + $"({outdated} verouderd)"
            + (deferred > 0 ? $", {deferred} uitgesteld (redenen in run_log)" : "");
        return new(pending.Count, docsMarked, rechecked, outdated, deferred, message);
    }
}
