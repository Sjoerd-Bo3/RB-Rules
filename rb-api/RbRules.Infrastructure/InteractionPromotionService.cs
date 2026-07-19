using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

/// <summary>Eén promotie-verzoek voor de poort (fase 2, #226): een gereïficeerde,
/// gekwalificeerde interactie mét het bewijs dat de deterministische signalen
/// voedt. Agent/patient dragen hun ontologie-<see cref="EntityType"/> zodat de
/// reïficatie-vorm-poort de rol-range en de kale-edge-dwang kan toetsen zonder een
/// BrainRef→type-gok.</summary>
/// <param name="DerivedFromRef">DERIVED_FROM (verplicht, geldige BrainRef): de bron
/// waaruit het feit is afgeleid — de gecite RuleSection, de bronkaart, of het
/// brondocument.</param>
public sealed record InteractionPromotionRequest(
    string AgentRef, EntityType AgentType,
    string PatientRef, EntityType PatientType,
    string Kind,
    string DerivedFromRef,
    string? GovernedByRef,
    IReadOnlyList<InteractionConditionInput> Conditions,
    bool LexicalSupport,
    int ConsensusCount,
    bool LlmVerdictInteracts);

/// <summary>Eén conditie-invoer (window/status/cost) op een promotie-verzoek.</summary>
public sealed record InteractionConditionInput(
    string OnKind, string? SubjectRole, string Value, string? Operator);

/// <summary>De uitkomst van één promotie-poort-run: de tier + memo, plus (als er
/// een interactie werd vastgelegd) het id.</summary>
public sealed record InteractionPromotionResult(
    InteractionGateOutcome Outcome, string StatusReason, long? InteractionId);

/// <summary>Fase 2 (#226) — de promotie-pipeline rond de deterministische poort
/// (<see cref="InteractionPromotionGate"/>). Hangt de IO (Postgres = SoT) om de
/// pure poort: valideert de reïficatie-vorm tegen de ontologie, raadpleegt de
/// <see cref="RejectionTombstone"/>-grafstenen tegen stil-heropenen, weegt de
/// signalen, en legt de uitslag zichtbaar vast (Interaction + Conditions +
/// Assertion-provenance + InteractionDecision-memo; bij verwerping een tombstone).
/// Geen enkele stap laat een LLM-verdict alléén een promotie dragen (rode draad
/// #236). De consensus-drempel N is een expliciete parameter (default 2).
///
/// De live rb-ai-extractie (tool-forced <c>emit_interactions</c>, §3.1) is bewust
/// als integratie-follow-up afgesplitst: de bestaande <c>InteractionMiner</c> kent
/// geen condities, en tool-forcing vereist een rb-ai-uitbreiding. Deze service
/// levert de promotie-pipeline + structuur; de extractie-vorm zelf staat puur in
/// <see cref="InteractionExtraction"/>.</summary>
public class InteractionPromotionService(RbRulesDbContext db)
{
    public const int DefaultConsensusThreshold = 2;

    /// <summary>Draait de promotie-poort voor één verzoek binnen een bestaande
    /// <see cref="MiningRun"/> en legt de uitslag vast (idempotent op de
    /// dedupe-sleutel). Retourneert de tier + memo.</summary>
    public async Task<InteractionPromotionResult> PromoteAsync(
        InteractionPromotionRequest request, string runId,
        int consensusThreshold = DefaultConsensusThreshold, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = InteractionKinds.Canonicalize(request.Kind);
        var conditions = request.Conditions ?? [];

        // ── Schema-poort: de reïficatie-vorm tegen de ontologie ──────────────
        OntologyValidationResult schema;
        if (kind is null)
        {
            schema = new OntologyValidationResult(false,
                [new(OntologyViolationCode.UnknownRelation,
                    $"'{request.Kind}' is geen gereïficeerd-verplichte relatie " +
                    "(COUNTERS/MODIFIES/GRANTS/REQUIRES).")]);
        }
        else
        {
            var relation = OntologySchema.RelationByEdgeName(kind)!;
            var reified = conditions
                .Select(c => InteractionConditionKinds.ConceptType(c.OnKind))
                .Where(t => t is not null)
                .Select(t => new OntologyValidationService.ReifiedCondition(t!.Value))
                .ToList();
            schema = OntologyValidationService.ValidateReifiedInteraction(
                request.AgentType, request.PatientType, relation.Type, reified);
        }

        // ── Tombstone-poort: eerder verworpen mag niet stil heropenen ────────
        var dedupeKey = InteractionDedupe.Key(request.AgentRef, request.PatientRef, kind ?? request.Kind);
        var hasBlockingTombstone = await db.RejectionTombstones.AsNoTracking()
            .AnyAsync(t => t.DedupeKey == dedupeKey && !t.Lifted, ct);

        // Emergente card×card: beide rollen zijn kaarten (cold-start-vangnet).
        var isCardCard = OntologySchema.IsA(request.AgentType, EntityType.Card)
            && OntologySchema.IsA(request.PatientType, EntityType.Card);

        var signals = new InteractionGateSignals(
            SchemaValid: schema.IsValid,
            SchemaReason: schema.Reason,
            LexicalSupport: request.LexicalSupport,
            ConsensusCount: request.ConsensusCount,
            ConsensusThreshold: consensusThreshold,
            LlmVerdictInteracts: request.LlmVerdictInteracts,
            IsEmergentCardCardPair: isCardCard,
            HasBlockingTombstone: hasBlockingTombstone);

        var gate = InteractionPromotionGate.Evaluate(signals);

        return gate.Outcome == InteractionGateOutcome.Rejected
            ? await RejectAsync(request, kind ?? request.Kind, dedupeKey, gate, runId, ct)
            : await AcceptAsync(request, kind!, conditions, gate, runId, ct);
    }

    // ── Verwerping: tombstone (herstelpad) + decision-memo, geen graaf-knoop ──
    private async Task<InteractionPromotionResult> RejectAsync(
        InteractionPromotionRequest request, string kind, string dedupeKey,
        InteractionGateResult gate, string runId, CancellationToken ct)
    {
        // Bestaande kandidaat op deze sleutel intrekken (status → rejected) zodat
        // ze niet als graaf-knoop blijft hangen; nieuwe verwerping heeft er geen.
        var existing = await db.Interactions
            .FirstOrDefaultAsync(x => x.AgentRef == request.AgentRef
                && x.PatientRef == request.PatientRef && x.Kind == kind, ct);
        if (existing is not null)
        {
            existing.Status = InteractionStatus.Rejected;
            existing.StatusReason = gate.StatusReason;
        }

        // Tombstone alleen aanmaken als er nog geen levende op deze sleutel staat.
        var alreadyTombstoned = await db.RejectionTombstones
            .AnyAsync(t => t.DedupeKey == dedupeKey && !t.Lifted, ct);
        if (!alreadyTombstoned)
            db.RejectionTombstones.Add(new RejectionTombstone
            {
                DedupeKey = dedupeKey,
                AgentRef = request.AgentRef,
                PatientRef = request.PatientRef,
                Kind = kind,
                Reason = gate.StatusReason,
                Actor = "gate",
                RunId = runId,
            });

        await db.SaveChangesAsync(ct);
        db.InteractionDecisions.Add(new InteractionDecision
        {
            InteractionId = existing?.Id ?? 0,
            Outcome = gate.Status,
            Memo = gate.StatusReason,
            RunId = runId,
        });
        await db.SaveChangesAsync(ct);

        return new(gate.Outcome, gate.StatusReason, existing?.Id);
    }

    // ── Acceptatie: upsert Interaction + Conditions + Assertion + Decision ────
    private async Task<InteractionPromotionResult> AcceptAsync(
        InteractionPromotionRequest request, string kind,
        IReadOnlyList<InteractionConditionInput> conditions,
        InteractionGateResult gate, string runId, CancellationToken ct)
    {
        var interaction = await db.Interactions
            .Include(x => x.Conditions)
            .FirstOrDefaultAsync(x => x.AgentRef == request.AgentRef
                && x.PatientRef == request.PatientRef && x.Kind == kind, ct);

        if (interaction is null)
        {
            interaction = new Interaction
            {
                AgentRef = request.AgentRef,
                PatientRef = request.PatientRef,
                Kind = kind,
                CreatedByRunId = runId,
            };
            db.Interactions.Add(interaction);
        }
        else
        {
            // Idempotente her-projectie: condities volledig herbouwen uit het verzoek.
            db.InteractionConditions.RemoveRange(interaction.Conditions);
            interaction.Conditions.Clear();
        }

        interaction.Status = gate.Status;
        interaction.StatusReason = gate.StatusReason;
        interaction.GovernedByRef = request.GovernedByRef;
        interaction.PromotedAt = gate.Outcome == InteractionGateOutcome.Promoted
            ? DateTimeOffset.UtcNow : null;

        foreach (var c in conditions)
        {
            var onKind = InteractionConditionKinds.Canonicalize(c.OnKind);
            if (onKind is null) continue; // schema is al geldig bevonden; skip rest defensief
            interaction.Conditions.Add(new InteractionCondition
            {
                Interaction = interaction,
                InteractionId = interaction.Id,
                OnKind = onKind,
                SubjectRole = InteractionRoles.IsValid(c.SubjectRole) ? c.SubjectRole : null,
                Value = c.Value,
                Operator = c.Operator,
            });
        }

        // SaveChanges vóór de Assertion: de interaction-Id (identity) is de subject-ref.
        await db.SaveChangesAsync(ct);

        var run = await db.MiningRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(),
            Subject = interaction.Ref.Format(),
            FactKind = FactKinds.Interaction,
            MiningRunId = runId,
            DerivedFromRef = request.DerivedFromRef,
            Model = run?.LlmModel,
            PromptVersion = run?.PromptVersion,
            Verifier = "llm+lexical",
            Verdict = "SUPPORTED",
        });
        db.InteractionDecisions.Add(new InteractionDecision
        {
            InteractionId = interaction.Id,
            Outcome = gate.Status,
            Memo = gate.StatusReason,
            RunId = runId,
        });
        await db.SaveChangesAsync(ct);

        return new(gate.Outcome, gate.StatusReason, interaction.Id);
    }

    /// <summary>Herstelpad (rode draad #236): hef de tombstone op de dedupe-sleutel
    /// op (unconsolidate) — een expliciete beheerdersactie, nooit automatisch. De
    /// grafsteen blijft als audit-spoor bestaan (Lifted), maar blokkeert de sleutel
    /// niet langer, zodat een herbeoordeling opnieuw door de poort mag.</summary>
    public async Task<int> LiftTombstonesAsync(string dedupeKey, CancellationToken ct = default)
    {
        var tombstones = await db.RejectionTombstones
            .Where(t => t.DedupeKey == dedupeKey && !t.Lifted)
            .ToListAsync(ct);
        foreach (var t in tombstones)
        {
            t.Lifted = true;
            t.LiftedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return tombstones.Count;
    }
}
