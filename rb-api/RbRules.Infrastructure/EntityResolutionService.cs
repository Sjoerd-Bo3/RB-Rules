using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van een resolutie: de gevonden entiteit (of <c>null</c> als
/// geen match), de eventuele magnitude (kritiek Risico 2a — 'Assault 2' → familie
/// 'Assault' + waarde 2) en de genormaliseerde basis die is opgezocht.</summary>
public sealed record EntityResolution(
    CanonicalEntity? Entity, int? Magnitude, bool Matched, string NormalizedBase);

/// <summary>Resultaat van het opnieuw doorlichten van de canonieke laag op
/// duplicaten: hoeveel paren zijn voorgesteld, hoeveel direct auto-gemerged (gate
/// open + lange labels) en hoeveel naar review gingen.</summary>
public sealed record MergeScanResult(int Proposed, int AutoMerged, int Queued, bool GateOpen);

/// <summary>Fase 1 (#225) — entity-resolution &amp; canonicalisatie. Hangt de IO
/// (Postgres = SoT) om de pure bouwstenen in <see cref="Domain.EntityResolution"/>:
/// resolveert surface-forms tegen het alias-lexicon VÓÓR er een nieuwe kandidaat
/// ontstaat (versla #2), doorzoekt de laag drietraps op merge-kandidaten ZONDER
/// agressieve auto-merge (versla #1), en houdt merges omkeerbaar via tombstones +
/// een expliciete <see cref="MergeDecision"/>-memo (rode draad #236).
///
/// Bij fase-1-cardinaliteit (tientallen mechanics/keywords) draait de duurdere
/// signaal-vergelijking bewust in-memory over de per-kind geladen entiteiten: dat
/// is KISS, gate-consistent met de gouden set (zelfde <see cref="Trigrams"/>-code)
/// en volledig testbaar zonder database. De <c>pg_trgm</c>-extensie + GIN-index
/// (migratie) staan klaar als het gedocumenteerde schaal-pad.</summary>
public class EntityResolutionService(RbRulesDbContext db)
{
    private readonly EntityResolutionThresholds _thresholds = EntityResolutionThresholds.Default;

    // ── Resolutie (ingest-tijd): resolve VÓÓR kandidaat-creatie ──────────────

    /// <summary>Resolveert een surface-form tegen <c>CanonicalLabel ∪ AltLabels</c>
    /// van alle levende entiteiten van dat kind (case/whitespace-genormaliseerd).
    /// Magnitude wordt afgesplitst en als parameter teruggegeven — de familie-basis
    /// bepaalt de match. Volgt de tombstone-keten naar de levende entiteit.</summary>
    public async Task<EntityResolution> ResolveAsync(
        string surfaceForm, string kind, CancellationToken ct = default)
    {
        if (!CanonicalEntityKinds.IsValid(kind))
            throw new ArgumentException($"Onbekend canoniek kind '{kind}'.", nameof(kind));

        var (baseLabel, magnitude) = Magnitude.Parse(surfaceForm);
        var normalizedBase = AliasNormalizer.Normalize(baseLabel);
        if (normalizedBase.Length == 0)
            return new(null, magnitude, false, normalizedBase);

        var entities = await LiveEntitiesAsync(kind, ct);
        foreach (var e in entities)
        {
            if (AliasNormalizer.Normalize(e.CanonicalLabel) == normalizedBase)
                return new(e, magnitude, true, normalizedBase);
            if (e.AltLabels.Any(a => AliasNormalizer.Normalize(a) == normalizedBase))
                return new(e, magnitude, true, normalizedBase);
        }
        return new(null, magnitude, false, normalizedBase);
    }

    /// <summary>Resolveert een surface-form en maakt, alleen als er nog geen match
    /// is, een nieuwe kandidaat-entiteit (status=candidate) met de magnitude-vrije
    /// basis als CanonicalLabel. Idempotent: dezelfde term (of een casing/whitespace-
    /// variant) levert dezelfde entiteit. Dit is de poort die synoniem-proliferatie
    /// stopt.</summary>
    public async Task<CanonicalEntity> ResolveOrRegisterAsync(
        string surfaceForm, string kind, string runId,
        string? definition = null, CancellationToken ct = default)
    {
        var resolution = await ResolveAsync(surfaceForm, kind, ct);
        if (resolution.Entity is { } existing)
        {
            // Nieuwe surface-form die (nog) niet als alias staat maar wel resolvet
            // (bv. via de basis) — geen actie; resolvet al. Een écht nieuw synoniem
            // registreren gebeurt bewust via de review-poort, niet automatisch.
            return existing;
        }

        var baseLabel = Magnitude.Parse(surfaceForm).BaseLabel;
        var entity = new CanonicalEntity
        {
            Kind = kind,
            CanonicalLabel = baseLabel,
            AltLabels = [],
            Definition = definition,
            Status = CanonicalEntityStatus.Candidate,
            CreatedByRunId = runId,
        };
        db.CanonicalEntities.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>Additieve backfill: registreert bestaande mechanic-strings
    /// (<c>Card.Mechanics[]</c> + geaccepteerde <see cref="MechanicKeyword"/>-termen)
    /// als canonieke entiteiten, zonder de bronvelden aan te raken (NIET-destructief;
    /// scope-eis #225). Elke term resolvet eerst tegen het lexicon — al bekende
    /// termen leveren geen duplicaat. Retourneert het aantal nieuw aangemaakte
    /// entiteiten.</summary>
    public async Task<int> RegisterExistingMechanicsAsync(string kind, CancellationToken ct = default)
    {
        if (!CanonicalEntityKinds.IsValid(kind))
            throw new ArgumentException($"Onbekend canoniek kind '{kind}'.", nameof(kind));

        var run = await StartRunAsync("entity_resolution_backfill", ct);

        // Distinct surface-forms uit de bestaande data (mechanics zijn strings op
        // de kaart; geaccepteerde vocabulaire-termen vullen aan).
        var fromCards = await db.Cards.AsNoTracking()
            .Where(c => c.Mechanics != null)
            .Select(c => c.Mechanics!)
            .ToListAsync(ct);
        var fromVocab = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Status == "accepted")
            .Select(k => k.Term)
            .ToListAsync(ct);

        var surfaceForms = fromCards.SelectMany(m => m)
            .Concat(fromVocab)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var created = 0;
        // Genormaliseerde basis dedupe binnen deze run (zodat twee kaarten met
        // dezelfde mechanic niet twee entiteiten maken vóór de eerste is opgeslagen).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in surfaceForms)
        {
            var normalizedBase = AliasNormalizer.Normalize(Magnitude.Parse(surface).BaseLabel);
            if (normalizedBase.Length == 0 || !seen.Add(normalizedBase)) continue;

            var resolution = await ResolveAsync(surface, kind, ct);
            if (resolution.Matched) continue;

            db.CanonicalEntities.Add(new CanonicalEntity
            {
                Kind = kind,
                CanonicalLabel = Magnitude.Parse(surface).BaseLabel,
                AltLabels = [],
                Status = CanonicalEntityStatus.Candidate,
                CreatedByRunId = run.Id,
            });
            created++;
        }

        run.Candidates = surfaceForms.Count;
        run.Verified = created;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return created;
    }

    // ── Dedup: drietraps-signalen, GEEN agressieve auto-merge ────────────────

    /// <summary>Doorzoekt één kind op merge-kandidaten. Blocking (genormaliseerd
    /// prefix) → trigram → embedding-cosine; alle drie = auto-merge-kandidaat, twee =
    /// review. Persisteert elk voorstel als <see cref="MergeCandidate"/> (idempotent)
    /// en voert alleen dán zelf een merge uit als de precisie-gate open staat ÉN
    /// beide labels lang genoeg zijn (kritiek Risico 2b). Anders: naar review.</summary>
    public async Task<MergeScanResult> ScanForMergeCandidatesAsync(
        string kind, CancellationToken ct = default)
    {
        if (!CanonicalEntityKinds.IsValid(kind))
            throw new ArgumentException($"Onbekend canoniek kind '{kind}'.", nameof(kind));

        var run = await StartRunAsync("entity_resolution_scan", ct);
        var gateOpen = EntityResolutionGate.IsOpen(EntityResolutionGoldSet.EvaluateDefault(_thresholds));

        var entities = await LiveEntitiesAsync(kind, ct);
        var normalized = entities.ToDictionary(
            e => e.Id, e => AliasNormalizer.Normalize(e.CanonicalLabel));

        // Blocking: groepeer op genormaliseerd prefix; alleen paren binnen een blok
        // worden verder gescoord (goedkoop→duur).
        var blocks = entities
            .GroupBy(e => EntityResolutionClassifier.BlockingKey(
                normalized[e.Id], _thresholds.BlockingPrefixLength))
            .Where(g => g.Count() > 1);

        int proposed = 0, autoMerged = 0, queued = 0;
        foreach (var block in blocks)
        {
            var members = block.ToList();
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                {
                    var a = members[i];
                    var b = members[j];
                    var signals = new EntityMatchSignals(
                        normalized[a.Id], normalized[b.Id],
                        Trigrams.Similarity(normalized[a.Id], normalized[b.Id]),
                        Cosine(a.Embedding, b.Embedding));
                    var decision = EntityResolutionClassifier.Classify(signals, _thresholds);
                    if (decision.Verdict == EntityMergeVerdict.NoMatch) continue;

                    proposed++;
                    // Doelconventie: laagste id overleeft (oudste/meest gevestigd).
                    var (target, source) = a.Id < b.Id ? (a, b) : (b, a);

                    var canAuto = decision.Verdict == EntityMergeVerdict.AutoMergeCandidate
                        && EntityResolutionGate.MayAutoMerge(
                            gateOpen, target.CanonicalLabel, source.CanonicalLabel, _thresholds);

                    if (canAuto)
                    {
                        await MergeInternalAsync(target, source, run.Id, "auto", decision.Reason, ct);
                        await UpsertCandidateAsync(target.Id, source.Id, decision, run.Id,
                            MergeCandidateStatus.Merged, ct);
                        autoMerged++;
                    }
                    else
                    {
                        await UpsertCandidateAsync(target.Id, source.Id, decision, run.Id,
                            MergeCandidateStatus.Open, ct);
                        queued++;
                    }
                }
        }

        run.Candidates = proposed;
        run.Verified = autoMerged;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(proposed, autoMerged, queued, gateOpen);
    }

    // ── Merge (tombstone + Decision-memo) & herstelpad ───────────────────────

    /// <summary>Beheerder-merge vanuit de reviewqueue: voegt <paramref name="sourceId"/>
    /// in <paramref name="targetId"/> (tombstone + memo). Geen precisie-gate — dit is
    /// een expliciete menselijke beslissing.</summary>
    public async Task<MergeDecision?> MergeAsync(
        long targetId, long sourceId, string memo, CancellationToken ct = default)
    {
        if (targetId == sourceId) return null;
        var target = await db.CanonicalEntities.FindAsync([targetId], ct);
        var source = await db.CanonicalEntities.FindAsync([sourceId], ct);
        if (target is null || source is null) return null;
        if (source.Status == CanonicalEntityStatus.Merged) return null;

        var run = await StartRunAsync("entity_resolution_merge", ct);
        var decision = await MergeInternalAsync(target, source, run.Id, "admin", memo, ct);

        // Bijbehorende kandidaat sluiten.
        var (aId, bId) = Order(targetId, sourceId);
        var candidate = await db.MergeCandidates
            .FirstOrDefaultAsync(c => c.EntityAId == aId && c.EntityBId == bId, ct);
        if (candidate is not null)
        {
            candidate.Status = MergeCandidateStatus.Merged;
            candidate.ReviewedAt = DateTimeOffset.UtcNow;
        }

        run.Verified = 1;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return decision;
    }

    /// <summary>Herstelpad (rode draad): draait de laatste niet-teruggedraaide merge
    /// van <paramref name="sourceId"/> terug — trekt exact de bij de merge verplaatste
    /// alias-labels weer uit het doel, haalt de tombstone weg en heropent de
    /// kandidaat. De <see cref="MergeDecision"/> blijft als audit-spoor bestaan
    /// (Reverted=true); niets wordt hard-deleted.</summary>
    public async Task<bool> UnconsolidateAsync(long sourceId, CancellationToken ct = default)
    {
        var source = await db.CanonicalEntities.FindAsync([sourceId], ct);
        if (source is null || source.Status != CanonicalEntityStatus.Merged) return false;

        var decision = await db.MergeDecisions
            .Where(d => d.SourceEntityId == sourceId && !d.Reverted)
            .OrderByDescending(d => d.DecidedAt)
            .FirstOrDefaultAsync(ct);
        if (decision is null) return false;

        var target = await db.CanonicalEntities.FindAsync([decision.TargetEntityId], ct);
        if (target is not null && decision.MovedAltLabels.Length > 0)
        {
            var moved = decision.MovedAltLabels
                .Select(AliasNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
            target.AltLabels = target.AltLabels
                .Where(a => !moved.Contains(AliasNormalizer.Normalize(a)))
                .ToArray();
        }

        source.Status = CanonicalEntityStatus.Candidate;
        source.MergedIntoId = null;
        source.MergedAt = null;
        decision.Reverted = true;
        decision.RevertedAt = DateTimeOffset.UtcNow;

        var (aId, bId) = Order(decision.TargetEntityId, sourceId);
        var candidate = await db.MergeCandidates
            .FirstOrDefaultAsync(c => c.EntityAId == aId && c.EntityBId == bId, ct);
        if (candidate is not null)
        {
            candidate.Status = MergeCandidateStatus.Open;
            candidate.ReviewedAt = null;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Drift-snapshot (inzicht #236) ────────────────────────────────────────

    /// <summary>Node-count per kind, tombstones, singletons (entiteiten zonder
    /// geabsorbeerde alias) en duplicatie-schuld (open merge-kandidaten).</summary>
    public async Task<CanonicalDriftSnapshot> DriftSnapshotAsync(CancellationToken ct = default)
    {
        var rows = await db.CanonicalEntities.AsNoTracking()
            .GroupBy(e => e.Kind)
            .Select(g => new
            {
                Kind = g.Key,
                Candidates = g.Count(e => e.Status == CanonicalEntityStatus.Candidate),
                Canonical = g.Count(e => e.Status == CanonicalEntityStatus.Canonical),
                Tombstones = g.Count(e => e.Status == CanonicalEntityStatus.Merged),
                // Singleton: levend en nog geen alias geabsorbeerd (lege AltLabels).
                Singletons = g.Count(e =>
                    e.Status != CanonicalEntityStatus.Merged && e.AltLabels.Length == 0),
            })
            .ToListAsync(ct);

        var byKind = rows
            .Select(r => new CanonicalKindDrift(
                r.Kind, r.Candidates + r.Canonical, r.Candidates, r.Canonical,
                r.Tombstones, r.Singletons))
            .ToList();

        var debt = await db.MergeCandidates.AsNoTracking()
            .CountAsync(c => c.Status == MergeCandidateStatus.Open, ct);

        return CanonicalDriftSnapshot.Build(byKind, debt, DateTimeOffset.UtcNow);
    }

    // ── Precisie-gate (gouden set) ───────────────────────────────────────────

    /// <summary>De gemeten ER-gouden-set-precisie en of de auto-merge-gate daarmee
    /// open staat — voor het beheer-inzicht en de scan-beslissing.</summary>
    public (EntityResolutionEvalResult Eval, bool GateOpen) PrecisionGate()
    {
        var eval = EntityResolutionGoldSet.EvaluateDefault(_thresholds);
        return (eval, EntityResolutionGate.IsOpen(eval));
    }

    // ── Interne helpers ──────────────────────────────────────────────────────

    private async Task<MergeDecision> MergeInternalAsync(
        CanonicalEntity target, CanonicalEntity source,
        string runId, string decidedBy, string memo, CancellationToken ct)
    {
        // Labels verplaatsen: bron-CanonicalLabel + bron-AltLabels die het doel nog
        // niet kent (genormaliseerd vergeleken). Alleen de daadwerkelijk toegevoegde
        // labels gaan in MovedAltLabels — dát is het exacte herstelpad.
        var existing = new HashSet<string>(
            new[] { target.CanonicalLabel }.Concat(target.AltLabels)
                .Select(AliasNormalizer.Normalize),
            StringComparer.Ordinal);

        var incoming = new[] { source.CanonicalLabel }.Concat(source.AltLabels);
        var moved = new List<string>();
        var newAlt = target.AltLabels.ToList();
        foreach (var label in incoming)
        {
            var norm = AliasNormalizer.Normalize(label);
            if (norm.Length == 0 || !existing.Add(norm)) continue;
            newAlt.Add(label);
            moved.Add(label);
        }
        target.AltLabels = newAlt.ToArray();

        source.Status = CanonicalEntityStatus.Merged;
        source.MergedIntoId = target.Id;
        source.MergedAt = DateTimeOffset.UtcNow;

        var decision = new MergeDecision
        {
            SourceEntityId = source.Id,
            TargetEntityId = target.Id,
            RunId = runId,
            DecidedBy = decidedBy,
            Memo = memo,
            MovedAltLabels = moved.ToArray(),
        };
        db.MergeDecisions.Add(decision);
        await Task.CompletedTask;
        return decision;
    }

    private async Task UpsertCandidateAsync(
        long targetId, long sourceId, EntityMergeDecision decision,
        string runId, string status, CancellationToken ct)
    {
        var (aId, bId) = Order(targetId, sourceId);
        var existing = await db.MergeCandidates
            .FirstOrDefaultAsync(c => c.EntityAId == aId && c.EntityBId == bId, ct);
        var verdict = decision.Verdict == EntityMergeVerdict.AutoMergeCandidate
            ? "auto_merge_candidate" : "review";
        if (existing is null)
        {
            db.MergeCandidates.Add(new MergeCandidate
            {
                EntityAId = aId, EntityBId = bId,
                Verdict = verdict, SignalCount = decision.SignalCount,
                Reason = decision.Reason, Status = status, RunId = runId,
                ReviewedAt = status == MergeCandidateStatus.Merged ? DateTimeOffset.UtcNow : null,
            });
        }
        else
        {
            existing.Verdict = verdict;
            existing.SignalCount = decision.SignalCount;
            existing.Reason = decision.Reason;
            existing.RunId = runId;
            if (status == MergeCandidateStatus.Merged)
            {
                existing.Status = MergeCandidateStatus.Merged;
                existing.ReviewedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task<IReadOnlyList<CanonicalEntity>> LiveEntitiesAsync(
        string kind, CancellationToken ct) =>
        await db.CanonicalEntities
            .Where(e => e.Kind == kind && e.Status != CanonicalEntityStatus.Merged)
            .ToListAsync(ct);

    private async Task<MiningRun> StartRunAsync(string kind, CancellationToken ct)
    {
        // Vocabulaire-snapshot: hash over de huidige canonieke labels — de stale-
        // conditie voor toekomstige her-mining (§3.5).
        var labels = await db.CanonicalEntities.AsNoTracking()
            .OrderBy(e => e.Id).Select(e => e.CanonicalLabel).ToListAsync(ct);
        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = kind,
            VocabSnapshot = TextUtils.Sha256(string.Join('\n', labels)),
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private static (long, long) Order(long x, long y) => x < y ? (x, y) : (y, x);

    /// <summary>Cosine tussen twee embedding-vectoren (in-memory, over de reeds
    /// geladen kandidaten). <c>null</c> als een van beide ontbreekt — dan vuurt het
    /// embedding-signaal niet en kan er nooit op alleen embedding gemerged worden.</summary>
    private static double? Cosine(Pgvector.Vector? a, Pgvector.Vector? b)
    {
        if (a is null || b is null) return null;
        var va = a.ToArray();
        var vb = b.ToArray();
        if (va.Length == 0 || va.Length != vb.Length) return null;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < va.Length; i++)
        {
            dot += va[i] * vb[i];
            na += va[i] * va[i];
            nb += vb[i] * vb[i];
        }
        if (na == 0 || nb == 0) return null;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
