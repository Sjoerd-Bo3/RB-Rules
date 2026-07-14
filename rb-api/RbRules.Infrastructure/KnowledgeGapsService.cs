using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record GapCoverage(
    int Cards, int CardsWithoutEmbedding, int CardsWithoutMechanics,
    int RuleChunks, int RuleChunksWithoutEmbedding,
    int PrimerTopics, int PrimerTopicsMissing, int PrimerDrafts,
    int OpenMechanicCandidates, int TracesConsidered);

public record GapQuestion(
    string Signal, string Question, string? QuestionType, DateTimeOffset CreatedAt);

public record GapSourceStatus(
    string Id, string Name, short TrustTier, int Documents, int Chunks,
    DateTimeOffset? LastChecked, DateTimeOffset? LastChangeAt);

/// <summary>Drift Postgres ↔ Neo4j (#108): per knooptype de verwachte
/// telling (Postgres, sync-predicaten) naast de werkelijke graph-telling.
/// Niet beschikbaar (Neo4j plat) is een geldige meting: GraphAvailable=false
/// met de reden in Detail — de rest van het rapport blijft gewoon staan.</summary>
public record GapDrift(
    bool GraphAvailable, string? Detail, IReadOnlyList<GraphDriftEntry> Entries);

/// <summary>Verouderingssignaal (#119): kennis die door een verwerkte
/// regelwijziging is hertoetst — een primer-doc terug in de reviewqueue
/// (kind "primer") of een claim die verouderd raakte (kind "claim").</summary>
public record GapAgingSignal(string Kind, string Title, string Reason, DateTimeOffset At);

/// <summary>Dekkingssignaal (#145): één regel per onvolledige set — "set X
/// mist N nummers" — met doorklik naar het set-dekking-overzicht.</summary>
public record GapSetCoverageSignal(string SetId, string Name, int Missing, int BaseTotal);

/// <summary>Verwerkingssignaal (#171, GapAgingSignal-stijl): een bron waarvan
/// de laatste scan mislukte, een vervolgstap (classify/claims) hing of
/// faalde, of die nog nooit gescand is — met doorklik naar het bron-dossier.
/// Bewust géén rij voor "leeg" (scan ok, niets opgeleverd) — dat kan
/// legitiem zijn en is geen signaal.</summary>
public record GapSourceProcessingSignal(
    string SourceId, string Name, string Status, string Reason, DateTimeOffset? At);

public record KnowledgeGapsReport(
    GapCoverage Coverage,
    IReadOnlyList<GapQuestion> Questions,
    IReadOnlyList<GapSourceStatus> Sources,
    GapDrift Drift,
    IReadOnlyList<GapAgingSignal> Aging,
    IReadOnlyList<GapSetCoverageSignal> SetCoverage,
    IReadOnlyList<GapSourceProcessingSignal> SourceProcessing);

/// <summary>Kennis-gaten-rapport (#52): meet waar de kennisbank dun is in
/// plaats van te raden. Vier invalshoeken: dekking (kaarten zonder
/// embedding/mechanics, secties zonder embedding, ontbrekende primer-
/// concepten), vraag-signalen (lege retrieval, AI-uitval, negatieve
/// feedback uit de ask-traces en correcties), bron-versheid (bronnen die
/// al lang niets nieuws leverden) en graph-drift (#108: loopt de
/// Neo4j-projectie achter op Postgres). Alleen reads — het rapport wordt
/// bij elke aanvraag vers berekend, er is niets om te verversen of cachen.</summary>
public class KnowledgeGapsService(RbRulesDbContext db, BrainGraphService graph)
{
    /// <summary>Hoeveel recente traces meewegen in de vraag-signalen.</summary>
    private const int TraceWindow = 200;

    public async Task<KnowledgeGapsReport> BuildAsync(CancellationToken ct = default)
    {
        // ── Dekking — zelfde predicaten als de pijplijnen zelf ──────────
        var canonical = db.Cards.Where(c => c.VariantOf == null);
        var cardsTotal = await canonical.CountAsync(ct);
        var cardsWithoutEmbedding = await canonical.CountAsync(c => c.Embedding == null, ct);
        var cardsWithoutMechanics = await canonical.CountAsync(
            c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != "", ct);

        var chunksTotal = await db.RuleChunks.CountAsync(ct);
        var chunksWithoutEmbedding = await db.RuleChunks.CountAsync(c => c.Embedding == null, ct);

        var primerTopics = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer")
            .Select(k => new { k.Topic, k.Status })
            .ToListAsync(ct);
        var haveTopic = primerTopics.Select(t => t.Topic).ToHashSet();
        var topicsMissing = PrimerTopics.All.Count(t => !haveTopic.Contains(t.Key));
        var primerDrafts = primerTopics.Count(t => t.Status != "approved");

        var openCandidates = await db.MechanicKeywords.CountAsync(k => k.Status == "candidate", ct);

        // ── Vraag-signalen — het kompas voor de volgende harvest ────────
        var traces = await db.AskTraces.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(TraceWindow)
            .Select(t => new
            {
                t.Question, t.QuestionType, t.Sections, t.ContextCards,
                t.PrimerDocs, t.Ok, t.CreatedAt,
            })
            .ToListAsync(ct);

        var questions = new List<GapQuestion>();
        foreach (var t in traces)
        {
            // "Lege retrieval": de prompt had níets — geen secties, geen
            // kaartcontext, geen primer. Hier weet de bank aantoonbaar niets.
            if (t.Ok && string.IsNullOrEmpty(t.Sections)
                     && string.IsNullOrEmpty(t.ContextCards)
                     && string.IsNullOrEmpty(t.PrimerDocs))
                questions.Add(new("lege-retrieval", t.Question, t.QuestionType, t.CreatedAt));
            else if (!t.Ok)
                questions.Add(new("ai-uitval", t.Question, t.QuestionType, t.CreatedAt));
        }

        // Negatieve feedback ("gemeld als onjuist") met de gestelde vraag erbij.
        var negative = await db.Corrections.AsNoTracking()
            .Where(c => c.Ref == "down" && c.Question != null)
            .OrderByDescending(c => c.CreatedAt)
            .Take(100)
            .Select(c => new { c.Question, c.CreatedAt })
            .ToListAsync(ct);
        questions.AddRange(negative.Select(c =>
            new GapQuestion("negatieve-feedback", c.Question!, null, c.CreatedAt)));
        questions = [.. questions.OrderByDescending(q => q.CreatedAt)];

        // ── Bron-versheid: wanneer leverde elke bron voor het laatst iets ─
        var docCounts = await db.Documents.AsNoTracking()
            .GroupBy(d => d.SourceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var chunkCounts = await db.RuleChunks.AsNoTracking()
            .GroupBy(c => c.SourceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var lastChanges = await db.Changes.AsNoTracking()
            .GroupBy(c => c.SourceId)
            .Select(g => new { g.Key, Last = g.Max(c => c.DetectedAt) })
            .ToDictionaryAsync(g => g.Key, g => g.Last, ct);

        var sources = (await db.Sources.AsNoTracking()
                .Where(s => s.Enabled)
                .OrderBy(s => s.TrustTier).ThenBy(s => s.Id)
                .ToListAsync(ct))
            .Select(s => new GapSourceStatus(
                s.Id, s.Name, s.TrustTier,
                docCounts.GetValueOrDefault(s.Id),
                chunkCounts.GetValueOrDefault(s.Id),
                s.LastChecked,
                lastChanges.TryGetValue(s.Id, out var last) ? last : null))
            .ToList();

        return new(
            new GapCoverage(
                cardsTotal, cardsWithoutEmbedding, cardsWithoutMechanics,
                chunksTotal, chunksWithoutEmbedding,
                PrimerTopics.All.Count, topicsMissing, primerDrafts,
                openCandidates, traces.Count),
            questions,
            sources,
            await BuildDriftAsync(ct),
            await BuildAgingAsync(ct),
            await BuildSetCoverageAsync(ct),
            await BuildSourceProcessingAsync(ct));
    }

    /// <summary>Begrensd venster op run_log voor het verwerkingssignaal
    /// (#171) — zelfde stijl als <see cref="TraceWindow"/>: recent genoeg om
    /// elke actieve bron te dekken, dit rapport gaat om de actuele staat.</summary>
    private const int ProcessingLogWindow = 3000;

    /// <summary>Verwerkingssignalen (#171): per ingeschakelde bron het
    /// compleetheidssignaal (<see cref="SourceDossierCompleteness"/>) — alleen
    /// de bronnen die aandacht vragen (onvolledig/nooit gescand) komen als
    /// rij terug, "leeg" en "volledig" zijn geen signaal.</summary>
    private async Task<IReadOnlyList<GapSourceProcessingSignal>> BuildSourceProcessingAsync(
        CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.Enabled)
            .Select(s => new { s.Id, s.Name, s.TrustTier })
            .ToListAsync(ct);
        if (sources.Count == 0) return [];
        var sourceIds = sources.Select(s => s.Id).ToHashSet();

        var lastScanBySource = (await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == "scan" && l.Ref != null && sourceIds.Contains(l.Ref!))
                .OrderByDescending(l => l.CreatedAt)
                .Take(ProcessingLogWindow)
                .Select(l => new { l.Ref, l.Status, l.Detail, l.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(l => l.Ref!)
            .ToDictionary(g => g.Key, g => g.First()); // al gesorteerd, dus First() = nieuwste

        var errorRows = await db.RunLogs.AsNoTracking()
            .Where(l => (l.Kind == "claims" || l.Kind == "classify")
                        && l.Status == "error" && l.Ref != null)
            .OrderByDescending(l => l.CreatedAt)
            .Take(ProcessingLogWindow)
            .Select(l => new { l.Kind, l.Ref })
            .ToListAsync(ct);
        var claimErrorSources = errorRows
            .Where(l => l.Kind == "claims" && sourceIds.Contains(l.Ref!))
            .Select(l => l.Ref!)
            .ToHashSet();

        // Classify-fouten dragen "change:{id}" — terugvertalen naar de bron
        // via Change.SourceId (run_log kent de bron zelf niet op dit kind).
        var changeSourceById = await db.Changes.AsNoTracking()
            .Where(c => sourceIds.Contains(c.SourceId))
            .Select(c => new { c.Id, c.SourceId })
            .ToDictionaryAsync(c => $"change:{c.Id}", c => c.SourceId, ct);
        var classifyErrorSources = errorRows
            .Where(l => l.Kind == "classify" && l.Ref != null
                        && changeSourceById.ContainsKey(l.Ref!))
            .Select(l => changeSourceById[l.Ref!])
            .ToHashSet();

        // Pending: nieuwste document per bron nog niet claims-gemined —
        // materialiseer bewust (klein: documenten van ingeschakelde bronnen),
        // "First() na OrderBy in een GroupBy-Select" is geen bewezen
        // vertaalbare LINQ-constructie.
        var latestDocBySource = (await db.Documents.AsNoTracking()
                .Where(d => sourceIds.Contains(d.SourceId))
                .Select(d => new { d.SourceId, d.RetrievedAt, d.ClaimsMinedAt })
                .ToListAsync(ct))
            .GroupBy(d => d.SourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.RetrievedAt).First());

        var signals = new List<GapSourceProcessingSignal>();
        foreach (var s in sources)
        {
            lastScanBySource.TryGetValue(s.Id, out var scan);
            var anyFailed = claimErrorSources.Contains(s.Id) || classifyErrorSources.Contains(s.Id);
            var anyPending = s.TrustTier >= 3
                && latestDocBySource.TryGetValue(s.Id, out var doc) && doc.ClaimsMinedAt is null;
            // opbrengstTotaal doet er hier niet toe: "leeg" en "volledig"
            // filteren we toch allebei weg, dus een dummy 0 volstaat.
            var status = SourceDossierCompleteness.Evaluate(scan?.Status, anyFailed, anyPending, 0);
            if (status is not (SourceDossierCompleteness.Onvolledig
                or SourceDossierCompleteness.NooitGescand)) continue;

            var reason = status == SourceDossierCompleteness.NooitGescand
                ? "nog nooit gescand"
                : scan?.Status == "error"
                    ? $"laatste scan mislukte: {scan?.Detail ?? "geen detail"}"
                    : anyFailed
                        ? "een vervolgstap (classify/claims-mining) faalde"
                        : "een vervolgstap (claims-mining) loopt nog";
            signals.Add(new(s.Id, s.Name, status, reason, scan?.CreatedAt));
        }

        return [.. signals.OrderByDescending(s => s.At ?? DateTimeOffset.MinValue)];
    }

    /// <summary>Set-dekking als gaten-signaal (#145): één regel per
    /// onvolledige set. De volledige uitsplitsing (welke nummers precies)
    /// staat op het set-dekking-overzicht; hier alleen het signaal —
    /// zelfde stijl als de verouderingssignalen (#119).</summary>
    private async Task<IReadOnlyList<GapSetCoverageSignal>> BuildSetCoverageAsync(
        CancellationToken ct)
    {
        var ids = await db.Cards.AsNoTracking()
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);
        var names = await db.CardSets.AsNoTracking()
            .ToDictionaryAsync(s => s.SetId, s => s.Name, ct);
        return [.. Domain.SetCoverage.Aggregate(ids)
            .Where(r => r.MissingNumbers.Count > 0 && r.BaseTotal is not null)
            .Select(r => new GapSetCoverageSignal(
                r.SetId, names.GetValueOrDefault(r.SetId, r.SetId),
                r.MissingNumbers.Count, r.BaseTotal!.Value))];
    }

    /// <summary>Verouderingssignalen (#119): wat heeft een verwerkte
    /// regelwijziging teruggelegd voor review? Primer-drafts dragen hun reden
    /// als kanttekening vooraan in de tekst (bewust zonder migratie),
    /// verouderde claims als StatusReason-prefix — beide herkenbaar en hier
    /// bijeengeraapt zodat de beheerder veroudering meet in plaats van raadt.</summary>
    private async Task<IReadOnlyList<GapAgingSignal>> BuildAgingAsync(CancellationToken ct)
    {
        var agingDocs = (await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer" && k.Status == "draft")
                .Select(k => new { k.Title, k.Body, k.UpdatedAt })
                .ToListAsync(ct))
            .SelectMany(k => KnowledgeRecheck.MarkerReasons(k.Body)
                .Select(reason => new GapAgingSignal("primer", k.Title, reason, k.UpdatedAt)));

        var agingClaims = (await db.Claims.AsNoTracking()
                .Where(c => c.Status == "superseded" && c.StatusReason != null
                            && c.StatusReason.StartsWith(KnowledgeRecheck.ClaimReasonPrefix))
                .OrderByDescending(c => c.LastSeen)
                .Take(50)
                .Select(c => new { c.TopicType, c.TopicRef, c.StatusReason, c.LastSeen })
                .ToListAsync(ct))
            .Select(c => new GapAgingSignal(
                "claim", $"{c.TopicType}: {c.TopicRef}", c.StatusReason!, c.LastSeen));

        return [.. agingDocs.Concat(agingClaims).OrderByDescending(a => a.At)];
    }

    /// <summary>Graph-drift (#108, docs/BRAIN.md §4): telt per knooptype wat
    /// Postgres nú zou projecteren (exact de predicaten van GraphSyncService)
    /// en zet dat naast de werkelijke Neo4j-tellingen. Best-effort: zonder
    /// Neo4j geen drift-cijfers maar wél een rapport — de fout is de data.</summary>
    private async Task<GapDrift> BuildDriftAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, int> graphCounts;
        try
        {
            graphCounts = await graph.CountsByLabelAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, ex.Message, []);
        }

        var canonical = db.Cards.AsNoTracking().Where(c => c.VariantOf == null);

        // Facetten (Domain/Tag/Mechanic) zijn array-kolommen: distinct over
        // de elementen kan niet in één vertaalbare LINQ-query — bewust
        // gematerialiseerd (drie smalle kolommen over honderden kaarten),
        // met exacte (ordinal) vergelijking zoals Neo4j's MERGE op name.
        var facets = await canonical
            .Select(c => new { c.SetId, c.Domains, c.Tags, c.Mechanics })
            .ToListAsync(ct);

        var postgres = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Card"] = facets.Count,
            ["Set"] = facets.Where(f => f.SetId != null).Select(f => f.SetId!).Distinct().Count(),
            ["Domain"] = facets.SelectMany(f => f.Domains).Distinct().Count(),
            ["Tag"] = facets.SelectMany(f => f.Tags).Distinct().Count(),
            ["Mechanic"] = facets.SelectMany(f => f.Mechanics ?? []).Distinct().Count(),
            // Sectie-knopen vouwen chunks samen tot één per (bron, §-code).
            ["RuleSection"] = await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && r.SectionCode != "")
                .Select(r => new { r.SourceId, r.SectionCode })
                .Distinct()
                .CountAsync(ct),
            ["Concept"] = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer")
                .Select(k => k.Topic)
                .Distinct()
                .CountAsync(ct),
            // Scope-keuze uit de sync: alleen accepted/unreviewed claims.
            ["Claim"] = await db.Claims.CountAsync(
                c => c.Status == "accepted" || c.Status == "unreviewed", ct),
            ["Source"] = await db.Sources.CountAsync(ct),
            ["Erratum"] = await db.Errata.CountAsync(ct),
            ["Change"] = await db.Changes.CountAsync(ct),
        };

        return new(true, null, GraphDrift.Compare(postgres, graphCounts));
    }
}
