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
/// <param name="IsCardOwnKeywordPair">Is dit paar een kaart met haar EIGEN keyword
/// (#249)? De aanroeper weet dat (hij kent <c>Card.Mechanics</c>); de poort weigert
/// het dan als al-deterministisch-bekend feit. Default false — het is een
/// aanvullende guard, geen verplichting voor bestaande aanroepers.</param>
/// <param name="KindAnchorSupport">Poort A (#330): draagt een dragende bewijs-eenheid
/// een lexicaal anker van de geclaimde soort (<see cref="InteractionKindAnchors"/>)?
/// BEWUST zonder default (#300-les): wie dit request bouwt, heeft het bewijs in
/// handen en moet de poort berekenen — de typechecker dwingt dat af.</param>
/// <param name="PatientWordFormSupport">Poort B (#330): staat het keyword-doel van
/// een toekennende claim in keyword-vorm in het bewijs (<see cref="KeywordWordForm"/>)?
/// Zelfde verplichting als <paramref name="KindAnchorSupport"/>; geef true wanneer de
/// poort niet van toepassing is (<see cref="KeywordWordForm.Applies"/> is false).</param>
public sealed record InteractionPromotionRequest(
    string AgentRef, EntityType AgentType,
    string PatientRef, EntityType PatientType,
    string Kind,
    string DerivedFromRef,
    string? GovernedByRef,
    IReadOnlyList<InteractionConditionInput> Conditions,
    bool LexicalSupport,
    int ConsensusCount,
    bool LlmVerdictInteracts,
    bool KindAnchorSupport,
    bool PatientWordFormSupport,
    bool IsCardOwnKeywordPair = false);

/// <summary>Eén conditie-invoer (window/status/cost) op een promotie-verzoek.</summary>
public sealed record InteractionConditionInput(
    string OnKind, string? SubjectRole, string Value, string? Operator);

/// <summary>De uitkomst van één promotie-poort-run: de tier + memo, plus (als er
/// een interactie werd vastgelegd) het id. <paramref name="DegradedBy"/> is de
/// soort-poort (#330, <see cref="InteractionGatePorts"/>) die een zou-promoveren-
/// claim naar Candidate degradeerde — de aanroeper telt erop (ADR-20).</summary>
public sealed record InteractionPromotionResult(
    InteractionGateOutcome Outcome, string StatusReason, long? InteractionId,
    string? DegradedBy = null);

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
            HasBlockingTombstone: hasBlockingTombstone,
            // Rollen-poort (#249): self-loop is hier gratis vast te stellen; de
            // kaart↔eigen-keyword-vlag komt van de aanroeper (die kent Card.Mechanics).
            RolesDistinct: !string.Equals(request.AgentRef, request.PatientRef, StringComparison.Ordinal),
            IsCardOwnKeywordPair: request.IsCardOwnKeywordPair,
            // Soort-poorten (#330): berekend door de aanroeper (die het bewijs heeft),
            // verplichte velden op het request zodat niemand ze kan overslaan.
            KindAnchorSupport: request.KindAnchorSupport,
            PatientWordFormSupport: request.PatientWordFormSupport);

        var gate = InteractionPromotionGate.Evaluate(signals);

        // ── Demotiegarantie (#313, verbreed in #332): géén her-mine-uitkomst die
        // ZWAKKER is dan de bestaande status verlaagt die status automatisch — de
        // uitkomst zegt iets over dít voorstel, niet over het eerder vastgestelde
        // feit. Werkt op status-ORDE (InteractionStatus.Strength: verified >
        // promoted > werk-tiers), niet op een hardcoded == promoted, zodat ook de
        // toekomstige verified-tier elke poort-uitkomst overleeft, inclusief een
        // zou-promoveren. Wel zichtbaar (ADR-20): een beslissings-memo — en géén
        // tombstone, want een oordeel dat de rij niet mag verlagen mag haar
        // sleutel ook niet duurzaam sluiten (#324b-symmetrie). Degradaties komen
        // uit de audit + reviewqueue, nooit uit de poort zelf. De werk-tiers
        // onderling volgen het normale verloop (Strength is daar gelijk): een
        // candidate wordt bij een gegrond negatief verdict nog gewoon verworpen,
        // mét flip-flop-suppressie.
        var existing = await db.Interactions.AsNoTracking()
            .Where(x => x.AgentRef == request.AgentRef
                && x.PatientRef == request.PatientRef && x.Kind == (kind ?? request.Kind))
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync(ct);
        if (existing is not null
            && InteractionStatus.Strength(gate.Status) < InteractionStatus.Strength(existing.Status))
        {
            db.InteractionDecisions.Add(new InteractionDecision
            {
                InteractionId = existing.Id,
                Outcome = gate.Status,
                Memo = $"{gate.StatusReason} — bestaande {existing.Status}-rij NIET " +
                    "gedemoveerd (invariant #313, verbreed in #332)",
                RunId = runId,
            });
            await db.SaveChangesAsync(ct);
            return new(gate.Outcome, gate.StatusReason, existing.Id, gate.DegradedBy);
        }

        return gate.Outcome == InteractionGateOutcome.Rejected
            ? await RejectAsync(request, kind ?? request.Kind, dedupeKey, gate, runId, ct)
            : await AcceptAsync(request, kind!, conditions, gate, runId, ct);
    }

    // ── Verwerping: tombstone (herstelpad) + decision-memo, geen graaf-knoop ──
    private async Task<InteractionPromotionResult> RejectAsync(
        InteractionPromotionRequest request, string kind, string dedupeKey,
        InteractionGateResult gate, string runId, CancellationToken ct)
    {
        // Beide schrijfstappen (feit/tombstone + provenance-beslissing) in één
        // transactie: een crash tussen de twee mag geen verworpen-zonder-memo of
        // gedemote-zonder-beslissing achterlaten (rode draad #236: geen onzichtbare
        // half-state).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Bestaande interactie op deze sleutel demoten (status → rejected) zodat ze
        // niet als graaf-knoop blijft hangen en de projectie ze overslaat. Sinds
        // #332 bereikt alleen een werk-tier-rij (candidate/hypothese/rejected, of
        // een onbekende legacy-status) dit pad — een promoted/verified-rij is al
        // door de demotiegarantie in PromoteAsync onderschept. PromotedAt wissen
        // blijft als vangnet voor zo'n legacy-rij (geen misleidende timestamp op
        // een verworpen knoop).
        var existing = await db.Interactions
            .FirstOrDefaultAsync(x => x.AgentRef == request.AgentRef
                && x.PatientRef == request.PatientRef && x.Kind == kind, ct);
        if (existing is not null)
        {
            existing.Status = InteractionStatus.Rejected;
            existing.StatusReason = gate.StatusReason;
            existing.PromotedAt = null;
        }

        // Grafsteen alleen als de poort de verwerping duurzaam-gegrond acht
        // (deterministische steun; nooit op een losstaand LLM-verdict of een
        // transiënte schema-fout, #226-review) én er nog geen levende op deze
        // sleutel staat.
        if (gate.WritesTombstone)
        {
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
        }

        await db.SaveChangesAsync(ct);
        db.InteractionDecisions.Add(new InteractionDecision
        {
            InteractionId = existing?.Id ?? 0,
            Outcome = gate.Status,
            Memo = gate.StatusReason,
            RunId = runId,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new(gate.Outcome, gate.StatusReason, existing?.Id);
    }

    // ── Acceptatie: upsert Interaction + Conditions + Assertion + Decision ────
    private async Task<InteractionPromotionResult> AcceptAsync(
        InteractionPromotionRequest request, string kind,
        IReadOnlyList<InteractionConditionInput> conditions,
        InteractionGateResult gate, string runId, CancellationToken ct)
    {
        // Feit (SoT) + provenance-Assertion + beslissing in één transactie: een
        // crash/FK-fout tussen de twee SaveChanges mag geen gepromoveerd feit
        // zonder Assertion achterlaten (0a-dubbele-write-guard; rode draad #236).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var interaction = await db.Interactions
            .Include(x => x.Conditions)
            .FirstOrDefaultAsync(x => x.AgentRef == request.AgentRef
                && x.PatientRef == request.PatientRef && x.Kind == kind, ct);

        // De demotiegarantie (#313/#332) is al in PromoteAsync afgedwongen, vóór de
        // dispatch: hier komt alleen een uitkomst binnen die de bestaande status
        // niet verlaagt (gelijk of sterker) — of een nieuwe rij.
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

        // Verifier/Verdict eerlijk labelen naar de tier (KNOWLEDGE.md-labeling +
        // rode draad): alleen een gepromoveerd feit draagt deterministische steun en
        // is SUPPORTED; een candidate wacht op corroboratie en een
        // model_hypothesized_unruled is per definitie een onbewezen hypothese — die
        // mogen niet als "llm+lexical/SUPPORTED" verschijnen (#226-review).
        var (verifier, verdict) = gate.Outcome switch
        {
            InteractionGateOutcome.Promoted =>
                (request.LexicalSupport ? "llm+lexical" : "llm+consensus", "SUPPORTED"),
            InteractionGateOutcome.Candidate => ("llm", "CANDIDATE"),
            InteractionGateOutcome.ModelHypothesizedUnruled => ("llm", "HYPOTHESIZED"),
            _ => ("llm", "UNVERIFIED"),
        };

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
            Verifier = verifier,
            Verdict = verdict,
        });
        db.InteractionDecisions.Add(new InteractionDecision
        {
            InteractionId = interaction.Id,
            Outcome = gate.Status,
            Memo = gate.StatusReason,
            RunId = runId,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new(gate.Outcome, gate.StatusReason, interaction.Id, gate.DegradedBy);
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
