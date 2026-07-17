using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ChangeConsolidationResult(int Merged, int Judged, int Skipped, string Message);

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
/// "consolidatechanges", <see cref="JobPaths"/> "ingest" ná "classify"):
/// ChangeType/Summary/Diff moeten al ingevuld zijn (classify) voordat de
/// kandidaat-poort en de LLM-toets iets zinvols kunnen beoordelen.
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

    private const int ResponseSnippetLength = 400;
    private const string LedgerKind = "consolidatechanges";

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
            return new(0, 0, 0, "geen kandidaat-paren (minder dan twee ongekoppelde changes)");

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

        var merged = 0;
        var judged = 0;
        var skipped = 0;

        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var rootA = currentRootId[roots[i].Id];
                var rootB = currentRootId[roots[j].Id];
                if (rootA == rootB) continue; // al hetzelfde geconsolideerde paar (deze run)

                var repA = byId[rootA];
                var repB = byId[rootB];
                var candA = new ChangeConsolidationCandidate(
                    repA.ChangeType, repA.DetectedAt, repA.SourceId, refsById[rootA]);
                var candB = new ChangeConsolidationCandidate(
                    repB.ChangeType, repB.DetectedAt, repB.SourceId, refsById[rootB]);
                if (!ChangeConsolidationGate.IsCandidate(candA, candB)) continue;

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
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = $"{repA.Id}+{repB.Id}", Status = "error",
                        Detail = "rb-ai niet beschikbaar — paar niet geconsolideerd, komt de volgende run terug",
                    });
                    skipped++;
                    continue;
                }

                judged++;
                var judgement = ChangeEventJudge.Parse(raw);
                if (judgement is null)
                {
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = $"{repA.Id}+{repB.Id}", Status = "error",
                        Detail = "LLM-antwoord onbruikbaar — paar niet geconsolideerd. "
                                 + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
                    });
                    skipped++;
                    continue;
                }
                if (!judgement.SameEvent)
                {
                    skipped++;
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

        var message = $"{merged} paar(en) geconsolideerd, {judged} LLM-toetsen, {skipped} overgeslagen";
        db.RunLogs.Add(new RunLog { Kind = LedgerKind, Ref = null, Status = "ok", Detail = message });
        await db.SaveChangesAsync(ct);
        return new(merged, judged, skipped, message);
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
