using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Reasoning;

namespace RbRules.Infrastructure;

// ── DTO's voor de Brein-verkenner (#236) ─────────────────────────────────────
// Read-only projecties over de bestaande brein-tabellen (assertions, canonical_
// entity, interaction, reasoning_conflict, mining_run, eval_baseline, answer_
// trace). Additief: geen enkel bestaand model/gedrag verandert; embeddings
// blijven buiten elke projectie (kosten + privacy).

/// <summary>De tegel-tellingen op het Brein-overzicht (#236, spec §6/§7). Eén rij
/// per teller zodat de admin-console ze als klikbare tegels toont; de extra
/// deel-tellingen (kandidaat/tombstone, gepromoveerd, open-conflict) geven de
/// tegel een status-subtekst zonder een tweede call.</summary>
public sealed record BrainOverviewCounts(
    int Assertions,
    int CanonicalEntities,
    int CanonicalEntitiesCandidate,
    int CanonicalEntitiesMerged,
    int Interactions,
    int InteractionsPromoted,
    int Conflicts,
    int ConflictsOpen,
    int MiningRuns,
    int EvalBaselines,
    int AnswerTraces);

/// <summary>Eén afgeronde brein-jobrun uit het run_log-grootboek (Kind="job",
/// Ref=jobnaam): status + detailregel + tijdstip. Null in de cockpit = die job
/// draaide nog nooit. Overleeft een herstart (grootboek, niet de in-memory
/// JobRunner-snapshot) en toont dus ook de automatische scheduler-runs.</summary>
public sealed record BrainJobRunItem(string Name, string Status, string? Detail, DateTimeOffset At);

/// <summary>De operationele brein-cockpit (brein-jobs-ui): per-stap-tellingen +
/// laatste-run per brein-job + de /ask-retrieval-flag. READ-ONLY en additief bovenop
/// het bestaande overzicht. De pipeline is 1→2→3: extractie (interacties + mechanic-
/// predicaten mineren) → projectie (naar Neo4j) → reasoner (afgeleide edges +
/// conflicts). De flag zelf komt uit de omgeving (BreinRetrievalSettings), niet uit
/// de DB — hij is geen knop maar een env-schakelaar op de VM.</summary>
public sealed record BrainCockpit(
    // Stap 1 — Extractie
    int Interactions,
    int MechanicPredicates,
    BrainJobRunItem? MineInteractionsRun,
    BrainJobRunItem? MinePredicatesRun,
    // Stap 2 — Projectie (breinprojectie → Neo4j)
    int CanonicalEntities,
    BrainJobRunItem? ProjectionRun,
    // Stap 3 — Reasoner (reason → afgeleide edges + conflicts)
    int Conflicts,
    int ConflictsOpen,
    BrainJobRunItem? ReasonRun,
    // /ask-retrieval (env-flag BREIN_RETRIEVAL_ENABLED, default uit)
    bool RetrievalEnabled);

/// <summary>Eén canonieke entiteit in de verkenner: canoniek label + alias-lexicon
/// + merge-status. <see cref="MergedIntoLabel"/> is het label van de overlevende
/// entiteit (voor een tombstone) — anders null.</summary>
public sealed record BrainEntityItem(
    long Id, string Kind, string CanonicalLabel, string[] AltLabels, string? Definition,
    string Status, long? MergedIntoId, string? MergedIntoLabel, string CreatedByRunId,
    DateTimeOffset CreatedAt);

/// <summary>Eén gereïficeerde conditie (window/status/cost) op een interactie.</summary>
public sealed record BrainConditionItem(string OnKind, string? SubjectRole, string Value, string? Operator);

/// <summary>Ankerkolommen per assertion voor de provenance-anker-lookup: net genoeg
/// om in-memory het nieuwste anker per subject te kiezen zonder een niet-vertaalbare
/// server-side greatest-n-per-group.</summary>
public sealed record AssertionAnchorRow(string Subject, string Id, DateTimeOffset AssertedAt);

/// <summary>Eén gereïficeerde interactie met haar condities en tier. <see
/// cref="AssertionRef"/> is de provenance-ankerref (assertion:{ulid}) als er een
/// Assertion met subject interaction:{id} bestaat — de doorklik naar de keten.</summary>
public sealed record BrainInteractionItem(
    long Id, string Kind, string AgentRef, string PatientRef, string? GovernedByRef,
    string Status, string? StatusReason, string CreatedByRunId, DateTimeOffset DetectedAt,
    DateTimeOffset? PromotedAt, IReadOnlyList<BrainConditionItem> Conditions,
    string SubjectRef, string? AssertionRef);

/// <summary>De opgeloste entiteit achter een interactie-ref, voor hover + doorklik
/// in de verkenner (#243). <see cref="Kind"/> = "card" (klik → <see cref="Href"/>,
/// hover → <see cref="Label"/> + <see cref="ImageUrl"/>) of "mechanic" (hover →
/// <see cref="Label"/> + <see cref="Description"/>; geen detailpagina). Refs die
/// niet resolven staan niet in de map — de UI toont die als kale ref.</summary>
public sealed record BrainRefEntity(
    string Ref, string Kind, string Label, string? ImageUrl, string? Href, string? Description);

/// <summary>Interacties-pagina mét de ref→entiteit-lookup zodat rb-web hover en
/// doorklik toont zonder tweede fetch (dun endpoint, logica hier — CONVENTIONS).
/// Zelfde velden als <see cref="Paged{T}"/> plus <see cref="Entities"/>.</summary>
public sealed record BrainInteractionsPage(
    int Total, int Page, int PageSize,
    IReadOnlyList<BrainInteractionItem> Items,
    IReadOnlyDictionary<string, BrainRefEntity> Entities);

/// <summary>Eén PROV-O-mining-run in de provenance-keten (WAS_GENERATED_BY-doel).</summary>
public sealed record BrainMiningRunItem(
    string Id, string Kind, string? LlmModel, string? PromptVersion, string? EmbeddingModel,
    string? VocabSnapshot, string? GitSha, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    int Candidates, int Verified, int Rejected);

/// <summary>Eén Assertion in de provenance-keten van een feit: WAS_GENERATED_BY
/// (<see cref="Run"/>), DERIVED_FROM (<see cref="DerivedFromRef"/>) en VERIFIED_BY
/// (<see cref="Verifier"/>/<see cref="Verdict"/>/<see cref="EvidenceSpan"/>).</summary>
public sealed record BrainAssertionItem(
    string Id, string Subject, string FactKind, string MiningRunId, string DerivedFromRef,
    long? DerivedFromDocumentId, string? Model, string? PromptVersion, string? Verifier,
    string? Verdict, string? EvidenceSpan, DateTimeOffset? ValidFrom, DateTimeOffset AssertedAt,
    BrainMiningRunItem? Run);

/// <summary>De provenance-keten van één feit-ref: alle Assertions die het subject
/// beweren, elk met hun run. Lege <see cref="Assertions"/> = geldig subject maar (nog)
/// geen herkomst vastgelegd (nette lege staat), niet een fout.</summary>
public sealed record BrainProvenanceChain(string Subject, IReadOnlyList<BrainAssertionItem> Assertions);

/// <summary>Eén reasoning-tegenspraak + haar routering (misconception/reviewqueue/
/// escalation). Voor het misvattingen-kanaal: <see cref="Channel"/> = misconception.</summary>
public sealed record BrainConflictRow(
    long Id, string PatternId, string Kind, string Channel, string SubjectRef, string? CounterRef,
    string? Memo, string Status, string RunId, DateTimeOffset DetectedAt);

/// <summary>Eén AnswerTrace in de lijst (recente antwoorden, voor de viewer-picker).</summary>
public sealed record BrainAnswerTraceListItem(
    string Id, string Question, string QuestionType, string RetrievalMode, string PrimaryChannel,
    int SupportCount, DateTimeOffset CreatedAt);

/// <summary>Eén dragend feit onder een antwoord (USED_ASSERTION {wAtQueryTime}).</summary>
public sealed record BrainAnswerTraceSupportItem(
    int CitationN, string SubjectRef, string Tier, double TrustWeightAtQuery, string? WidgetMarker);

/// <summary>De herspeelbare AnswerTrace: subgraaf/paden (de dragende refs), trust-
/// gewichten-toen en de epoch-stempels (graphEpoch/model/prompt/embeddingRev).</summary>
public sealed record BrainAnswerTraceDetail(
    string Id, string Question, string QuestionType, string RetrievalMode, string? FallbackReason,
    double Beta, string PrimaryChannel, string? GateMemo, string? GraphEpoch, string? LlmModel,
    string? PromptVersion, string? EmbeddingRev, DateTimeOffset CreatedAt,
    IReadOnlyList<BrainAnswerTraceSupportItem> Supports);

public sealed record BrainTierCount(string Key, int Count);

/// <summary>Het observability-rapport voor de Brein-tegels (fase 7, spec §7). De
/// <see cref="Report"/> draagt de deterministische Postgres-rollups (mining-precisie,
/// canonieke drift/duplicatie-schuld); de Neo4j/GDS-delen (graph-drift, community-
/// stabiliteit) blijven leeg tot de graph-jobs draaien (nette lege staat, geen live
/// Neo4j vereist). <see cref="InteractionTiers"/> en <see cref="ConflictChannels"/>
/// zijn de goedkope tier-verdelingen die de hypothese-/misvatting-gezondheid tonen.</summary>
public sealed record BrainObservability(
    ObservabilityReport Report,
    IReadOnlyList<BrainTierCount> InteractionTiers,
    IReadOnlyList<BrainTierCount> ConflictChannels);

/// <summary>Read-only inspectie-laag over het Poracle-brein (#236, inzicht-thread):
/// de Brein-verkenner in de admin-console. Puur leesbaar en additief — géén schrijf-
/// pad, géén mining/reasoner/retrieval-executie (aparte increments), géén live-Neo4j-
/// afhankelijkheid (alles komt uit Postgres, de SoT voor de ABox). Elke projectie
/// laat embeddings buiten beschouwing en dun-houdt de endpoints: de logica leeft
/// hier (docs/CONVENTIONS.md). Nette lege staten zijn de norm: geen brein-data →
/// lege lijsten/nul-tellingen, geen fout.</summary>
public class BrainExplorerService(RbRulesDbContext db)
{
    private const int PageSize = 60;

    private static int ClampPage(int page) => Math.Clamp(page, 1, 100_000);
    private static int Skip(int page) => Math.Max(0, page - 1) * PageSize;

    /// <summary>De tegel-tellingen (§6/§7). Zes kern-tellers + drie status-subtekst-
    /// tellers, in één ronde per teller — nul overal = het brein is nog niet gevuld.</summary>
    public async Task<BrainOverviewCounts> OverviewAsync(CancellationToken ct = default) => new(
        Assertions: await db.Assertions.CountAsync(ct),
        CanonicalEntities: await db.CanonicalEntities.CountAsync(ct),
        CanonicalEntitiesCandidate: await db.CanonicalEntities
            .CountAsync(e => e.Status == CanonicalEntityStatus.Candidate, ct),
        CanonicalEntitiesMerged: await db.CanonicalEntities
            .CountAsync(e => e.Status == CanonicalEntityStatus.Merged, ct),
        Interactions: await db.Interactions.CountAsync(ct),
        InteractionsPromoted: await db.Interactions
            .CountAsync(i => i.Status == InteractionStatus.Promoted, ct),
        Conflicts: await db.ReasoningConflicts.CountAsync(ct),
        ConflictsOpen: await db.ReasoningConflicts
            .CountAsync(c => c.Status == ReasoningConflictStatus.Open, ct),
        MiningRuns: await db.MiningRuns.CountAsync(ct),
        EvalBaselines: await db.EvalBaselines.CountAsync(ct),
        AnswerTraces: await db.AnswerTraces.CountAsync(ct));

    /// <summary>De operationele cockpit (brein-jobs-ui): per-stap-tellingen + de
    /// laatste afronding per brein-job + de /ask-retrieval-flag. Read-only en
    /// additief. <paramref name="retrievalEnabled"/> komt van de endpoint (uit de
    /// <c>BreinRetrievalSettings</c>-singleton, env), niet uit de DB. De laatste-run
    /// per job komt uit het run_log-grootboek (<see cref="RunLog"/> Kind="job",
    /// Ref=jobnaam) — dat overleeft een herstart en dekt ook de scheduler-runs.</summary>
    public async Task<BrainCockpit> CockpitAsync(bool retrievalEnabled, CancellationToken ct = default)
    {
        // De vier brein-jobs waarvan de cockpit de laatste afronding toont.
        var brainJobs = new[]
        {
            "breinmine-interacties", "breinmine-predicaten", "breinprojectie", "reason",
        };
        // Greatest-n-per-group (nieuwste run per Ref) kan Npgsql niet server-side
        // vertalen — de kandidaatrijen vertaalbaar (Where+Select) ophalen en
        // in-memory het nieuwste per job kiezen (zelfde patroon als de assertion-
        // ankers in InteractionsAsync). Begrensd: enkele jobs × hun run-historie.
        var runRows = await db.RunLogs.AsNoTracking()
            .Where(r => r.Kind == "job" && r.Ref != null && brainJobs.Contains(r.Ref))
            .Select(r => new { r.Ref, r.Status, r.Detail, r.CreatedAt })
            .ToListAsync(ct);
        var lastByJob = runRows
            .GroupBy(r => r.Ref!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var r = g.OrderByDescending(x => x.CreatedAt).First();
                    return new BrainJobRunItem(r.Ref!, r.Status, r.Detail, r.CreatedAt);
                },
                StringComparer.Ordinal);

        return new BrainCockpit(
            Interactions: await db.Interactions.CountAsync(ct),
            MechanicPredicates: await db.MechanicPredicates.CountAsync(ct),
            MineInteractionsRun: lastByJob.GetValueOrDefault("breinmine-interacties"),
            MinePredicatesRun: lastByJob.GetValueOrDefault("breinmine-predicaten"),
            CanonicalEntities: await db.CanonicalEntities.CountAsync(ct),
            ProjectionRun: lastByJob.GetValueOrDefault("breinprojectie"),
            Conflicts: await db.ReasoningConflicts.CountAsync(ct),
            ConflictsOpen: await db.ReasoningConflicts
                .CountAsync(c => c.Status == ReasoningConflictStatus.Open, ct),
            ReasonRun: lastByJob.GetValueOrDefault("reason"),
            RetrievalEnabled: retrievalEnabled);
    }

    /// <summary>Canonieke entiteiten met alias-lexicon en merge-status, gepagineerd.
    /// Optioneel gefilterd op kind (mechanic/keyword/concept) en status (candidate/
    /// canonical/merged). Tombstones krijgen het label van hun overlevende doel mee.</summary>
    public async Task<Paged<BrainEntityItem>> EntitiesAsync(
        string? kind, string? status, int page, CancellationToken ct = default)
    {
        page = ClampPage(page);
        var query = db.CanonicalEntities.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(e => e.Kind == kind);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(e => e.Kind).ThenBy(e => e.CanonicalLabel).ThenBy(e => e.Id)
            .Skip(Skip(page)).Take(PageSize)
            .Select(e => new
            {
                e.Id, e.Kind, e.CanonicalLabel, e.AltLabels, e.Definition, e.Status,
                e.MergedIntoId, e.CreatedByRunId, e.CreatedAt,
            })
            .ToListAsync(ct);

        // Merge-doel-labels in één ronde (tombstones verwijzen naar de overlevende).
        var targetIds = rows.Where(r => r.MergedIntoId is not null)
            .Select(r => r.MergedIntoId!.Value).Distinct().ToList();
        var targetLabels = targetIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.CanonicalEntities.AsNoTracking()
                .Where(e => targetIds.Contains(e.Id))
                .Select(e => new { e.Id, e.CanonicalLabel })
                .ToDictionaryAsync(e => e.Id, e => e.CanonicalLabel, ct);

        var items = rows.Select(r => new BrainEntityItem(
            r.Id, r.Kind, r.CanonicalLabel, r.AltLabels, r.Definition, r.Status,
            r.MergedIntoId,
            r.MergedIntoId is { } tid ? targetLabels.GetValueOrDefault(tid) : null,
            r.CreatedByRunId, r.CreatedAt)).ToList();
        return new(total, page, PageSize, items);
    }

    /// <summary>Gereïficeerde interacties met condities, tier en provenance-anker,
    /// gepagineerd (nieuwste eerst). Optioneel op status (tier) gefilterd. De
    /// AssertionRef verwijst naar de keten die <see cref="ProvenanceChainAsync"/>
    /// uitklapt — of null als het feit (nog) geen Assertion draagt.</summary>
    public async Task<BrainInteractionsPage> InteractionsAsync(
        string? status, int page, CancellationToken ct = default)
    {
        page = ClampPage(page);
        var query = db.Interactions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.Status == status);

        var total = await query.CountAsync(ct);
        var rows = await query
            .Include(i => i.Conditions)
            .OrderByDescending(i => i.DetectedAt).ThenBy(i => i.Id)
            .Skip(Skip(page)).Take(PageSize)
            .ToListAsync(ct);

        // Provenance-ankers: welke interaction-subjects dragen een Assertion?
        // Greatest-n-per-group (nieuwste assertion per subject) kan Npgsql niet
        // server-side vertalen (First() uit een geordende GroupBy) — dus alleen
        // de ankerkolommen ophalen (vertaalbare Where+Select) en in-memory
        // groeperen. Het aantal rijen is begrensd door paginagrootte ×
        // assertions-per-subject.
        var subjects = rows.Select(i => i.Ref.Format()).ToList();
        var assertionBySubject = subjects.Count == 0
            ? new Dictionary<string, string>()
            : (await AssertionAnchorQuery(db, subjects).ToListAsync(ct))
                .GroupBy(a => a.Subject)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AssertedAt).First().Id);

        var items = rows.Select(i =>
        {
            var subjectRef = i.Ref.Format();
            var assertionId = assertionBySubject.GetValueOrDefault(subjectRef);
            return new BrainInteractionItem(
                i.Id, i.Kind, i.AgentRef, i.PatientRef, i.GovernedByRef, i.Status, i.StatusReason,
                i.CreatedByRunId, i.DetectedAt, i.PromotedAt,
                i.Conditions
                    .OrderBy(c => c.OnKind).ThenBy(c => c.Id)
                    .Select(c => new BrainConditionItem(c.OnKind, c.SubjectRole, c.Value, c.Operator))
                    .ToList(),
                subjectRef,
                assertionId is null ? null : BrainRef.Assertion(assertionId).Format());
        }).ToList();

        // Ref → entiteit voor hover + doorklik (#243): resolve de kaart-/mechanic-
        // refs van déze pagina in twee vertaalbare batch-queries. Read-only, geen
        // tweede client-fetch — zelfde lijn als de rest van de verkenner.
        var entities = await ResolveRefsAsync(
            rows.SelectMany(i => new[] { i.AgentRef, i.PatientRef, i.GovernedByRef }), ct);
        return new BrainInteractionsPage(total, page, PageSize, items, entities);
    }

    /// <summary>Resolvet de distinct kaart-/mechanic-refs van een interactiepagina
    /// naar weergave-info (#243): kaarten → naam + afbeelding + doorklik-href
    /// (/cards/{RiftboundId}); mechanics → canoniek label + definitie (geen
    /// detailpagina). Twee EF-vertaalbare batch-queries (ANY op RiftboundId resp.
    /// CanonicalLabel). Tombstone-entiteiten (merged) tellen niet mee; onopgeloste
    /// kaarten en andere ref-soorten komen niet in de map (UI toont kale ref).</summary>
    private async Task<IReadOnlyDictionary<string, BrainRefEntity>> ResolveRefsAsync(
        IEnumerable<string?> refs, CancellationToken ct)
    {
        var parsed = new Dictionary<string, BrainRef>();
        foreach (var r in refs)
        {
            if (string.IsNullOrWhiteSpace(r) || parsed.ContainsKey(r)) continue;
            if (BrainRef.TryParse(r, out var p)) parsed[r] = p;
        }
        if (parsed.Count == 0) return new Dictionary<string, BrainRefEntity>();

        var cardIds = parsed.Values.Where(p => p.Kind == BrainRefKind.Card)
            .Select(p => p.Key).Distinct().ToList();
        var mechLabels = parsed.Values.Where(p => p.Kind == BrainRefKind.Mechanic)
            .Select(p => p.Key).Distinct().ToList();

        var cardById = (await db.Cards.AsNoTracking()
                .Where(c => cardIds.Contains(c.RiftboundId))
                .Select(c => new { c.RiftboundId, c.Name, c.ImageUrl })
                .ToListAsync(ct))
            .ToDictionary(c => c.RiftboundId);

        // Keyword-/mechanic-refs zijn opgeslagen als mechanic:{CanonicalLabel}
        // (BreinInteractionMiningService) — resolve dus op CanonicalLabel, niet op
        // CanonicalEntity.Ref (dat voor keyword tag: zou zijn).
        var mechByLabel = (await db.CanonicalEntities.AsNoTracking()
                .Where(e => e.MergedIntoId == null && mechLabels.Contains(e.CanonicalLabel))
                .Select(e => new { e.CanonicalLabel, e.Definition })
                .ToListAsync(ct))
            .GroupBy(e => e.CanonicalLabel)
            .ToDictionary(g => g.Key, g => g.First().Definition);

        var map = new Dictionary<string, BrainRefEntity>();
        foreach (var (r, p) in parsed)
        {
            if (p.Kind == BrainRefKind.Card && cardById.TryGetValue(p.Key, out var c))
                map[r] = new BrainRefEntity(r, "card", c.Name, c.ImageUrl, $"/cards/{c.RiftboundId}", null);
            else if (p.Kind == BrainRefKind.Mechanic)
                map[r] = new BrainRefEntity(r, "mechanic", p.Key, null, null,
                    mechByLabel.GetValueOrDefault(p.Key));
        }
        return map;
    }

    /// <summary>De vertaalbare (Where+Select) ankersubquery achter
    /// <see cref="InteractionsAsync"/>: de assertion-kolommen per subject die we
    /// nodig hebben om in-memory het nieuwste anker te kiezen. Als eigen methode
    /// zodat een regressietest de échte productiequery via ToQueryString kan
    /// bewijzen (greatest-n-per-group hoort NIET server-side — Npgsql vertaalt
    /// First() uit een geordende GroupBy niet).</summary>
    internal static IQueryable<AssertionAnchorRow> AssertionAnchorQuery(
        RbRulesDbContext db, IReadOnlyList<string> subjects) =>
        db.Assertions.AsNoTracking()
            .Where(a => subjects.Contains(a.Subject))
            .Select(a => new AssertionAnchorRow(a.Subject, a.Id, a.AssertedAt));

    /// <summary>De provenance-keten van één feit-ref (assertions/{ref}, §6). Accepteert
    /// een subject-ref (bv. interaction:42, relation:5, card:…) én een directe
    /// assertion:{ulid}. Ongeldige ref → null (404); geldige-maar-lege → lege keten
    /// (nette lege staat). Elke Assertion draagt haar WAS_GENERATED_BY-run mee.</summary>
    public async Task<BrainProvenanceChain?> ProvenanceChainAsync(
        string refText, CancellationToken ct = default)
    {
        if (!BrainRef.TryParse(refText, out var parsed)) return null;
        var canonical = parsed.Format();

        // assertion:{ulid} → keten van precies die ene assertion; anders alle
        // assertions die dit subject beweren.
        var query = parsed.Kind == BrainRefKind.Assertion
            ? db.Assertions.AsNoTracking().Where(a => a.Id == parsed.Key)
            : db.Assertions.AsNoTracking().Where(a => a.Subject == canonical);

        var assertions = await query
            .Include(a => a.MiningRun)
            .OrderByDescending(a => a.AssertedAt)
            .ToListAsync(ct);

        var items = assertions.Select(a => new BrainAssertionItem(
            a.Id, a.Subject, a.FactKind, a.MiningRunId, a.DerivedFromRef, a.DerivedFromDocumentId,
            a.Model, a.PromptVersion, a.Verifier, a.Verdict, a.EvidenceSpan, a.ValidFrom, a.AssertedAt,
            a.MiningRun is null ? null : new BrainMiningRunItem(
                a.MiningRun.Id, a.MiningRun.Kind, a.MiningRun.LlmModel, a.MiningRun.PromptVersion,
                a.MiningRun.EmbeddingModel, a.MiningRun.VocabSnapshot, a.MiningRun.GitSha,
                a.MiningRun.StartedAt, a.MiningRun.CompletedAt, a.MiningRun.Candidates,
                a.MiningRun.Verified, a.MiningRun.Rejected))).ToList();

        // Voor een assertion:{ulid}-ref rapporteren we het feit-subject als subject
        // van de keten; voor een subject-ref de ref zelf.
        var subject = parsed.Kind == BrainRefKind.Assertion
            ? assertions.FirstOrDefault()?.Subject ?? canonical
            : canonical;
        return new BrainProvenanceChain(subject, items);
    }

    /// <summary>Reasoning-tegenspraken + routering, gepagineerd (nieuwste eerst).
    /// Optioneel op status (open/reviewed/resolved/dismissed) gefilterd; het
    /// misvattingen-kanaal is de subset met Channel = misconception.</summary>
    public async Task<Paged<BrainConflictRow>> ConflictsAsync(
        string? status, int page, CancellationToken ct = default)
    {
        page = ClampPage(page);
        var query = db.ReasoningConflicts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.DetectedAt).ThenBy(c => c.Id)
            .Skip(Skip(page)).Take(PageSize)
            .Select(c => new BrainConflictRow(
                c.Id, c.PatternId, c.Kind, c.Channel, c.SubjectRef, c.CounterRef, c.Memo,
                c.Status, c.RunId, c.DetectedAt))
            .ToListAsync(ct);
        return new(total, page, PageSize, items);
    }

    /// <summary>Recente AnswerTraces voor de viewer-picker (nieuwste eerst, top 100).</summary>
    public async Task<IReadOnlyList<BrainAnswerTraceListItem>> AnswerTracesAsync(
        CancellationToken ct = default) =>
        await db.AnswerTraces.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(100)
            .Select(t => new BrainAnswerTraceListItem(
                t.Id, t.Question, t.QuestionType, t.RetrievalMode, t.PrimaryChannel,
                t.Supports.Count, t.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Eén herspeelbare AnswerTrace (answertrace/{id}, §4/§6): de dragende
    /// subgraaf/paden (Supports) met trust-toen + epoch-stempels. Null als de trace
    /// niet bestaat.</summary>
    public async Task<BrainAnswerTraceDetail?> AnswerTraceAsync(string id, CancellationToken ct = default)
    {
        var trace = await db.AnswerTraces.AsNoTracking()
            .Include(t => t.Supports)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trace is null) return null;

        return new BrainAnswerTraceDetail(
            trace.Id, trace.Question, trace.QuestionType, trace.RetrievalMode, trace.FallbackReason,
            trace.Beta, trace.PrimaryChannel, trace.GateMemo, trace.GraphEpoch, trace.LlmModel,
            trace.PromptVersion, trace.EmbeddingRev, trace.CreatedAt,
            trace.Supports
                .OrderBy(s => s.CitationN).ThenBy(s => s.Id)
                .Select(s => new BrainAnswerTraceSupportItem(
                    s.CitationN, s.SubjectRef, s.Tier, s.TrustWeightAtQuery, s.WidgetMarker))
                .ToList());
    }

    /// <summary>De observability-rollups (fase 7, §7). Deterministische Postgres-
    /// aggregaties: mining-precisie per (soort × model), canonieke drift + duplicatie-
    /// schuld (open merge-kandidaten), en de tier-verdelingen van interacties en
    /// conflict-kanalen. De Neo4j/GDS-delen (graph-drift, community-stabiliteit,
    /// hypothese-precisie tegen een gouden set) blijven leeg — die vergen de graph-
    /// jobs, wat een aparte increment is; hier tonen we een nette lege staat.</summary>
    public async Task<BrainObservability> ObservabilityAsync(CancellationToken ct = default)
    {
        var runs = await db.MiningRuns.AsNoTracking().ToListAsync(ct);

        // Canonieke drift per kind (Live/Candidate/Canonical/Tombstone/Singleton).
        var entityRows = await db.CanonicalEntities.AsNoTracking()
            .Select(e => new { e.Kind, e.Status, e.AltLabels })
            .ToListAsync(ct);
        var byKind = entityRows
            .GroupBy(e => e.Kind, StringComparer.Ordinal)
            .Select(g =>
            {
                var canonical = g.Count(e => e.Status == CanonicalEntityStatus.Canonical);
                var candidate = g.Count(e => e.Status == CanonicalEntityStatus.Candidate);
                var tombstones = g.Count(e => e.Status == CanonicalEntityStatus.Merged);
                var live = canonical + candidate;
                var singletons = g.Count(e =>
                    e.Status != CanonicalEntityStatus.Merged && (e.AltLabels?.Length ?? 0) == 0);
                return new CanonicalKindDrift(g.Key, live, candidate, canonical, tombstones, singletons);
            })
            .ToList();
        var duplicationDebt = await db.MergeCandidates
            .CountAsync(m => m.Status == MergeCandidateStatus.Open, ct);
        var canonicalDrift = CanonicalDriftSnapshot.Build(byKind, duplicationDebt, DateTimeOffset.UtcNow);

        var report = ObservabilityReport.Build(
            DateTimeOffset.UtcNow, canonicalDrift: canonicalDrift, miningRuns: runs);

        var interactionTiers = (await db.Interactions.AsNoTracking()
                .GroupBy(i => i.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new BrainTierCount(g.Key, g.Count))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

        var conflictChannels = (await db.ReasoningConflicts.AsNoTracking()
                .GroupBy(c => c.Channel)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new BrainTierCount(g.Key, g.Count))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

        return new BrainObservability(report, interactionTiers, conflictChannels);
    }
}
