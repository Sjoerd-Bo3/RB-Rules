using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ChangeConsolidationResult(
    int Merged, int Judged, int Rejected, int Skipped, string Message);

/// <summary>Uitkomst van de handmatige ontkoppeling (#206 review-fix,
/// finding 1): <see cref="NotConsolidated"/> betekent dat de change bestaat
/// maar geen secundaire is — er valt niets te ontkoppelen.</summary>
public enum UnconsolidateOutcome { Applied, NotFound, NotConsolidated }

/// <summary>Changeconsolidatie (issue #206): koppelt changes die vanuit
/// verschillende bronnen hetzelfde real-world event melden (bv. de Rules
/// Hub- en Mobalytics-melding van dezelfde ban, 16 juli) tot één
/// geconsolideerd paar in de feed — de meest gezaghebbende/vroegste wordt
/// primair (<see cref="ChangeConsolidationPrimary"/>), de rest krijgt
/// <see cref="Change.ConsolidatedWithId"/>. Beide rijen blijven gewoon
/// bestaan; dit is een presentatie-koppeling, geen inhoudelijke waarheid
/// (die blijft bij de structured BanEntry-/errata-precedentie, #168).
///
/// Kleine, idempotente stap ná elke scan (<see cref="JobCatalog"/>
/// "consolidatechanges", <see cref="JobPaths"/> "ingest" ná "classify", en
/// uurlijks via de ScanScheduler-periodiek): ChangeType/Summary/Diff moeten
/// al ingevuld zijn (classify) voordat de kandidaat-poort en de LLM-toets
/// iets zinvols kunnen beoordelen.
///
/// Werkt alleen op nog niet-geconsolideerde ("root") changes binnen <see
/// cref="LookbackWindow"/> (ruim boven <see
/// cref="ChangeConsolidationGate.Window"/>, 72u) — oudere roots zijn ofwel
/// al gekoppeld, ofwel structureel nooit gekoppeld (het venster is dan al
/// lang gepasseerd) en hoeven niet steeds opnieuw vergeleken te worden
/// (#58-precedent, IngestService.ReclassifyWindow). Terugwerkend: de eerste
/// run na deze feature pakt bestaande paren binnen het venster ook op —
/// er is geen aparte backfill nodig.
///
/// <b>Pair-memo (review-fix findings 2+6):</b> een "nee"-oordeel wordt per
/// paar onthouden via het bestaande run_log-als-memo-idioom
/// (SetReleaseService/DeckIngestService-patroon): kind
/// <see cref="LedgerKind"/>, ref <c>pair:{minId}-{maxId}</c>, status
/// "rejected". De kandidaat-lus slaat paren met zo'n memo over — elke
/// paar-judge is zo éénmalig (geen LLM-budget aan hetzelfde overlevende
/// paar tot 30 dagen lang, en geen tweede flip-kans op een eerder
/// afgewezen paar). Een "ja" hoeft geen memo: de merge zelf is het bewijs
/// (de secundaire is geen root meer). Transiënte uitval (rb-ai weg,
/// onparseerbaar antwoord) krijgt bewust GEEN memo — de volgende run
/// probeert gewoon opnieuw. <see cref="UnconsolidateAsync"/> schrijft
/// hetzelfde memo, zodat een handmatig ontkoppeld paar sticky blijft.
///
/// Nooit ketens: bij een match wordt de winnende primaire bepaald via <see
/// cref="ChangeConsolidationPrimary"/>; verliest de bestaande root, dan
/// verhuizen ook haar bestaande secundairen in dezelfde merge mee naar de
/// nieuwe primaire (nooit secundaire-van-een-secundaire).</summary>
public class ChangeConsolidationService(RbRulesDbContext db, RbAiClient ai)
{
    /// <summary>Terugkijkvenster voor de root-kandidatenquery: ruim boven
    /// <see cref="ChangeConsolidationGate.Window"/> (72u) zodat ook bronnen
    /// met een lagere scan-cadans (weekly) een event nog kunnen inhalen, maar
    /// begrensd zodat de query niet de hele Change-geschiedenis blijft
    /// meeslepen.</summary>
    public static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(30);

    public const string LedgerKind = "consolidatechanges";

    private const int ResponseSnippetLength = 400;
    private const string PairRefPrefix = "pair:";

    /// <summary>Volgorde-onafhankelijke memo-sleutel voor één paar:
    /// <c>pair:{minId}-{maxId}</c>.</summary>
    public static string PairRef(long a, long b) =>
        a < b ? $"{PairRefPrefix}{a}-{b}" : $"{PairRefPrefix}{b}-{a}";

    public async Task<ChangeConsolidationResult> RunAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - LookbackWindow;
        var roots = await db.Changes
            .Where(c => c.ConsolidatedWithId == null && c.DetectedAt >= cutoff)
            .OrderBy(c => c.DetectedAt).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.SourceId, c.ChangeType, c.Summary, c.Meaning, c.Diff, c.DetectedAt,
                SourceName = c.Source!.Name, c.Source!.TrustTier,
            })
            .ToListAsync(ct);

        if (roots.Count < 2)
            return new(0, 0, 0, 0, "geen kandidaat-paren (minder dan twee ongekoppelde changes)");

        // Pair-memo's (findings 2+6): eerder afgewezen of handmatig ontkoppelde
        // paren in één gebatchte, EF-vertaalbare query — die worden nooit
        // opnieuw aan de LLM voorgelegd.
        var rejectedPairs = (await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == LedgerKind && l.Status == "rejected"
                            && l.Ref != null && l.Ref.StartsWith(PairRefPrefix))
                .Select(l => l.Ref!)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        // Zelfde AFFECTS-resolutie als de graph-projectie (#104): één
        // gedeelde waarheid over "welke kaarten/secties raakt deze change",
        // geen aparte extractielaag voor #206.
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);
        var sections = await db.RuleChunks.AsNoTracking()
            .Where(r => r.SectionCode != null && r.SectionCode != "")
            .Select(r => new { r.SourceId, Code = r.SectionCode! })
            .Distinct()
            .ToListAsync(ct);
        var mapper = ChangeAffectsMapper.Create(
            cards.Select(c => (c.RiftboundId, c.Name)),
            sections.Select(s => (s.SourceId, s.Code)));

        var byId = roots.ToDictionary(r => r.Id);
        var refsById = roots.ToDictionary(r => r.Id, r => mapper.Resolve(
            r.ChangeType, ChangeAffectsMapper.AffectsText(r.Summary, r.Meaning, r.Diff)));
        // Huidige wortel per oorspronkelijke root-id — bijgewerkt zodra
        // binnen deze run een merge plaatsvindt, zodat een derde kandidaat
        // in dezelfde run naar de juiste (mogelijk net veranderde)
        // wortel-primaire wordt vergeleken/gekoppeld.
        var currentRootId = roots.ToDictionary(r => r.Id, r => r.Id);
        // Binnen-run-dedupe (findings 4+7): na een merge kunnen meerdere
        // (i,j)-combinaties op hetzelfde EFFECTIEVE root-paar uitkomen —
        // elk effectief paar krijgt hooguit één LLM-poging per run (geen
        // dubbele call, geen tweede flip-kans).
        var attemptedThisRun = new HashSet<string>();

        var merged = 0;
        var judged = 0;
        var rejected = 0;
        var skipped = 0;

        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var rootA = currentRootId[roots[i].Id];
                var rootB = currentRootId[roots[j].Id];
                if (rootA == rootB) continue; // al hetzelfde geconsolideerde paar (deze run)

                var pairRef = PairRef(rootA, rootB);
                if (rejectedPairs.Contains(pairRef)) continue;   // memo: éénmalig geoordeeld
                if (attemptedThisRun.Contains(pairRef)) continue; // al geprobeerd deze run

                var repA = byId[rootA];
                var repB = byId[rootB];
                var candA = new ChangeConsolidationCandidate(
                    repA.ChangeType, repA.DetectedAt, repA.SourceId, refsById[rootA]);
                var candB = new ChangeConsolidationCandidate(
                    repB.ChangeType, repB.DetectedAt, repB.SourceId, refsById[rootB]);
                if (!ChangeConsolidationGate.IsCandidate(candA, candB)) continue;

                attemptedThisRun.Add(pairRef);
                progress?.Invoke($"kandidaat-paar change {repA.Id} + {repB.Id} — LLM-toets");
                string? raw;
                try
                {
                    raw = await ai.AskAsync(
                        ChangeEventJudge.BuildPrompt(
                            repA.SourceName, repA.Summary, repA.Diff,
                            repB.SourceName, repB.Summary, repB.Diff),
                        ChangeEventJudge.SystemPrompt, ct: ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Zelfde scout-timeoutpatroon als de andere mining-services:
                    // een HttpClient-timeout is AI-uitval, geen crash van de run.
                    raw = null;
                }

                if (raw is null)
                {
                    // Transiënt — bewust géén pair-memo: de volgende run
                    // probeert dit paar gewoon opnieuw.
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = pairRef, Status = "error",
                        Detail = "rb-ai niet beschikbaar — paar niet geconsolideerd, komt de volgende run terug",
                    });
                    skipped++;
                    continue;
                }

                judged++;
                var judgement = ChangeEventJudge.Parse(raw);
                if (judgement is null)
                {
                    // Ook transiënt (onparseerbaar ≠ "nee") — geen memo.
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = pairRef, Status = "error",
                        Detail = "LLM-antwoord onbruikbaar — paar niet geconsolideerd. "
                                 + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
                    });
                    skipped++;
                    continue;
                }
                if (!judgement.SameEvent)
                {
                    // Pair-memo (findings 2+6): het "nee" is definitief —
                    // dit paar wordt nooit meer aan de LLM voorgelegd.
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = pairRef, Status = "rejected",
                        Detail = $"LLM-oordeel: change {repA.Id} en {repB.Id} beschrijven "
                                 + "niet hetzelfde event — paar definitief niet geconsolideerd",
                    });
                    rejectedPairs.Add(pairRef);
                    rejected++;
                    continue;
                }

                var aWins = ChangeConsolidationPrimary.Wins(
                    repA.TrustTier, repA.DetectedAt, repB.TrustTier, repB.DetectedAt);
                var winnerId = aWins ? rootA : rootB;
                var loserId = aWins ? rootB : rootA;

                await MergeAsync(winnerId, loserId, ct);
                foreach (var key in currentRootId.Keys.ToList())
                    if (currentRootId[key] == loserId) currentRootId[key] = winnerId;
                merged++;
            }
        }

        var message = $"{merged} paar(en) geconsolideerd, {judged} LLM-toetsen "
            + $"({rejected} afgewezen met memo), {skipped} overgeslagen";
        db.RunLogs.Add(new RunLog { Kind = LedgerKind, Ref = null, Status = "ok", Detail = message });
        await db.SaveChangesAsync(ct);
        return new(merged, judged, rejected, skipped, message);
    }

    /// <summary>Handmatige ontkoppeling van een foute merge (#206 review-fix,
    /// finding 1): zet <see cref="Change.ConsolidatedWithId"/> terug op null
    /// én schrijft een sticky pair-memo (zelfde store als het "nee"-oordeel
    /// van de run), zodat de eerstvolgende consolidatie-run het paar niet
    /// meteen weer merget — zonder memo zou handmatig ontkoppelen binnen een
    /// uur teruggedraaid worden. Endpoint:
    /// <c>POST /api/admin/changes/{id}/unconsolidate</c> (op de secundaire).</summary>
    public async Task<UnconsolidateOutcome> UnconsolidateAsync(
        long secondaryId, CancellationToken ct = default)
    {
        var change = await db.Changes.FindAsync([secondaryId], ct);
        if (change is null) return UnconsolidateOutcome.NotFound;
        if (change.ConsolidatedWithId is not { } primaryId)
            return UnconsolidateOutcome.NotConsolidated;

        change.ConsolidatedWithId = null;
        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind, Ref = PairRef(primaryId, secondaryId), Status = "rejected",
            Detail = $"handmatig ontkoppeld door de beheerder (change {secondaryId} los van "
                     + $"{primaryId}) — dit paar wordt nooit meer automatisch geconsolideerd",
        });
        await db.SaveChangesAsync(ct);
        return UnconsolidateOutcome.Applied;
    }

    /// <summary>Koppelt <paramref name="loserId"/> aan <paramref
    /// name="winnerId"/> en verhuist meteen haar bestaande secundairen mee
    /// (nooit ketens) — één transactie rond beide koppel-writes.</summary>
    private async Task MergeAsync(long winnerId, long loserId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var loser = await db.Changes.FindAsync([loserId], ct);
        if (loser is null) return; // defensief; kan niet gebeuren binnen één run
        loser.ConsolidatedWithId = winnerId;

        var existingSecondaries = await db.Changes
            .Where(c => c.ConsolidatedWithId == loserId)
            .ToListAsync(ct);
        foreach (var s in existingSecondaries) s.ConsolidatedWithId = winnerId;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
