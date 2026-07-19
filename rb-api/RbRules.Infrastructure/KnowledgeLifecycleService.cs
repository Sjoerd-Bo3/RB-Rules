using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>De geconsolideerde levenscyclus-service (fase 6, #230, §6) — de IO-schil om
/// de pure levenscyclus-logica (<see cref="LifecycleState"/>,
/// <see cref="StalenessEvaluator"/>, <see cref="ErrataLifecycle"/>,
/// <see cref="ModelUpgradeInvalidation"/>). Vervangt niet de bestaande, specifieke
/// tombstones (fase 1 <c>merge_decision</c>, fase 2 <c>rejection_tombstone</c>) maar
/// overkoepelt ze met één auditeerbaar <see cref="LifecycleEvent"/>-log: staleness,
/// SUPERSEDES-deprecatie, errata-invalidatie en de kostengegate model-upgrade-schaduw-
/// mine landen hier als expliciete, herstelbare transities. Niets wordt hard-deleted;
/// niets levert onzichtbare state (rode draad #236).
///
/// Postgres = SoT. De Neo4j-projectie (het <c>:Superseded</c>/<c>:Tombstone</c>-label
/// op de betrokken knoop) en het daadwerkelijk her-minen van de schaduw-mine-batch zijn
/// bewuste integratie-follow-ups (§8) — deze service selecteert en legt vast.</summary>
public class KnowledgeLifecycleService(RbRulesDbContext db)
{
    /// <summary>Legt één levenscyclus-transitie vast (de geconsolideerde tombstone-/
    /// deprecatie-writer). Weigert een ongeldige transitie hard — nooit stil een feit
    /// vernietigen of heropenen.</summary>
    public async Task<LifecycleEvent> RecordTransitionAsync(
        string subjectRef, string factKind, string fromState, string toState,
        string reason, string actor, string runId,
        string? supersededByRef = null, string? restorePath = null,
        CancellationToken ct = default)
    {
        if (!LifecycleState.CanTransition(fromState, toState))
            throw new InvalidOperationException(
                $"Ongeldige levenscyclus-transitie {fromState} → {toState} voor {subjectRef}.");

        var ev = new LifecycleEvent
        {
            SubjectRef = subjectRef,
            FactKind = factKind,
            FromState = fromState,
            ToState = toState,
            Reason = reason,
            Actor = actor,
            RunId = runId,
            SupersededByRef = supersededByRef,
            RestorePath = restorePath ?? "admin:restore",
        };
        db.LifecycleEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        return ev;
    }

    /// <summary>De HUIDIGE levenscyclus-toestand van een feit, afgeleid uit het
    /// append-only log: de <see cref="LifecycleEvent.ToState"/> van de laatste
    /// niet-teruggedraaide transitie, of <see cref="LifecycleState.Active"/> als er nog
    /// geen enkele transitie is. Zo krijgt elke nieuwe transitie een WAARE
    /// <c>FromState</c> — nooit een hardgecodeerde <c>Active</c> die een onmogelijke
    /// historie (bv. tweemaal <c>Active→Stale</c>) in het provenance-spoor schrijft
    /// (review-defect #230; rode draad #236).</summary>
    private async Task<string> CurrentStateAsync(string subjectRef, CancellationToken ct)
    {
        var last = await db.LifecycleEvents.AsNoTracking()
            .Where(e => e.SubjectRef == subjectRef && !e.Reverted)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Select(e => e.ToState)
            .FirstOrDefaultAsync(ct);
        return last ?? LifecycleState.Active;
    }

    /// <summary>Legt een transitie naar <paramref name="toState"/> vast met de ECHTE
    /// huidige toestand als <c>FromState</c>. Idempotent en niet-fataal voor de bulk-
    /// invalidatieflows (errata/staleness/model-upgrade): staat het feit al in de
    /// doeltoestand of al verder (een transitie die de toestand-machine niet toelaat
    /// vanuit de huidige toestand — bv. een reeds getombsteend feit), dan is dit een
    /// no-op (<c>null</c>) i.p.v. een dubbele/valse transitie of een harde fout die de
    /// hele batch afbreekt. Alleen een echte, toegestane vooruitgang wordt geschreven.</summary>
    private async Task<LifecycleEvent?> TransitionIfProgressingAsync(
        string subjectRef, string factKind, string toState,
        string reason, string actor, string runId,
        string? supersededByRef = null, string? restorePath = null,
        CancellationToken ct = default)
    {
        var fromState = await CurrentStateAsync(subjectRef, ct);
        if (fromState == toState || !LifecycleState.CanTransition(fromState, toState))
            return null;
        return await RecordTransitionAsync(subjectRef, factKind, fromState, toState,
            reason, actor, runId, supersededByRef, restorePath, ct);
    }

    /// <summary>Zet een vers-beoordeeld feit dat de staleness-poort raakte naar
    /// <see cref="LifecycleState.Stale"/> — de her-verificatie-trigger, met de
    /// evaluator-memo als reden. Al-stale of al-teruggetrokken feiten leveren een
    /// no-op (<c>null</c>): geen dubbele her-verificatie-transitie.</summary>
    public Task<LifecycleEvent?> RequeueStaleAsync(
        string subjectRef, string factKind, StalenessEvaluator.Verdict verdict, string runId,
        CancellationToken ct = default)
        => TransitionIfProgressingAsync(subjectRef, factKind,
            LifecycleState.Stale, verdict.Memo, "staleness", runId, ct: ct);

    /// <summary>Voert het errata-plan uit: de vervangen ruling → <see cref="LifecycleState.Superseded"/>
    /// (SUPERSEDES, blijft bestaan voor dossier-historie), elk afhankelijk feit/eval-case
    /// → <see cref="LifecycleState.Stale"/> (her-verificatie / forbidden_claim-verval).
    /// Retourneert de vastgelegde transities.</summary>
    public async Task<IReadOnlyList<LifecycleEvent>> ApplyErratumSupersessionAsync(
        ErrataLifecycle.Plan plan, string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var events = new List<LifecycleEvent>();

        if (plan.TargetRulingRef is { } rulingRef)
        {
            var superseded = await TransitionIfProgressingAsync(
                rulingRef, "ruling", LifecycleState.Superseded,
                $"vervangen door {plan.ErratumRef}", "errata", runId,
                supersededByRef: plan.ErratumRef, restorePath: "admin:restore-ruling", ct: ct);
            if (superseded is not null) events.Add(superseded);
        }

        foreach (var inv in plan.Invalidations)
        {
            var stale = await TransitionIfProgressingAsync(
                inv.SubjectRef, inv.FactKind, LifecycleState.Stale,
                inv.Reason, "errata", runId, ct: ct);
            if (stale is not null) events.Add(stale);
        }

        return events;
    }

    /// <summary>Zet de kostengegate schaduw-mine-batch (BESLISSING #232) om in
    /// her-verificatie-transities: elk geselecteerd, puur-LLM-ongesteund feit →
    /// <see cref="LifecycleState.Stale"/> (re-queue voor her-mining door het nieuwe
    /// model). De backlog blijft ongemoeid tot een volgende cyclus. Het daadwerkelijke
    /// her-minen is een integratie-follow-up. Retourneert het aantal DAADWERKELIJK
    /// vastgelegde transities — een reeds-stale feit (bv. eerder al door errata geraakt)
    /// wordt overgeslagen en niet dubbel geteld.</summary>
    public async Task<int> ApplyModelUpgradeAsync(
        ModelUpgradeInvalidation.Plan plan, string oldModel, string runId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var requeued = 0;
        foreach (var fact in plan.Selected)
        {
            var ev = await TransitionIfProgressingAsync(
                fact.SubjectRef, fact.FactKind, LifecycleState.Stale,
                $"model-upgrade van {oldModel}: puur-LLM-ongesteund → her-mine", "model_upgrade", runId, ct: ct);
            if (ev is not null) requeued++;
        }
        return requeued;
    }

    /// <summary>Het geconsolideerde herstelpad (unconsolidate): heropent het laatste,
    /// niet-al-teruggedraaide eindpunt (deprecated/superseded/tombstoned) van een feit
    /// via een expliciete <see cref="LifecycleState.Restored"/>-transitie. De oude
    /// gebeurtenis blijft als audit-spoor bestaan, gemarkeerd als teruggedraaid — nooit
    /// een hard-delete.</summary>
    public async Task<LifecycleEvent> RestoreAsync(
        string subjectRef, string actor, string runId, CancellationToken ct = default)
    {
        var terminal = new[] { LifecycleState.Deprecated, LifecycleState.Superseded, LifecycleState.Tombstoned };
        var last = await db.LifecycleEvents
            .Where(e => e.SubjectRef == subjectRef && !e.Reverted && terminal.Contains(e.ToState))
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"Geen herstelbare eind-transitie voor {subjectRef} — niets te herstellen.");

        last.Reverted = true;
        last.RevertedAt = DateTimeOffset.UtcNow;

        var restored = new LifecycleEvent
        {
            SubjectRef = subjectRef,
            FactKind = last.FactKind,
            FromState = last.ToState,
            ToState = LifecycleState.Restored,
            Reason = $"herstel van {last.ToState}: {last.Reason}",
            Actor = actor,
            RunId = runId,
            RestorePath = last.RestorePath,
        };
        db.LifecycleEvents.Add(restored);
        await db.SaveChangesAsync(ct);
        return restored;
    }
}
