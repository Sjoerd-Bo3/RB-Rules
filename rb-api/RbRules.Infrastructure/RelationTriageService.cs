using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary><paramref name="CapHit"/> (#190): machine-leesbaar of er na deze
/// run nog vers werk (meer unreviewed/niet-getriagede voorstellen dan
/// <c>maxItems</c> toeliet) klaarligt — paden draineren hierop in plaats van
/// op tekst in <paramref name="Message"/> te matchen. Gefaalde items (rb-ai
/// weg, onparseerbare output) tellen niet als vers werk (#190-review): ze
/// blijven ongemarkeerd en komen de volgende run vanzelf terug, maar
/// verhogen CapHit niet — die is al bepaald vóór de run op het totale
/// aantal eligible items.</summary>
public record RelationTriageRunResult(
    int Judged, int Accepted, int Rejected, int Unsure, int Skipped, string Message,
    bool CapHit = false);

public enum RelationDecisionOutcome { Applied, NotFound }

/// <summary>LLM-triage voor de relatie-reviewqueue (#199 v1): per open
/// voorstel (Status "unreviewed", nog geen <see cref="Relation.Recommendation"/>)
/// één retrieval-gegronde LLM-beoordeling — accept/reject/unsure + motivering.
/// GEEN autoriteitspad (zie <see cref="RelationTriage"/>): de aanbeveling
/// verandert nooit zelf <see cref="Relation.Status"/>. Een mens-beoordeeld
/// voorstel (Status niet meer "unreviewed") wordt nooit her-getriaged — een
/// mens-oordeel wint altijd.
///
/// Context is bewust goedkoop (#199-eis): geen embeddings/semantische
/// zoekopdracht, alleen een paar regelsecties op basis van de betrokken
/// kaart-/mechaniek-/concept-/claim-namen — hetzelfde soort retrieval als
/// <see cref="RelationMiningService"/>.BuildMechanicsContextAsync, alleen nu
/// toegepast op de refs van een AL VOORGESTELDE relatie in plaats van op een
/// mining-anker.
///
/// <see cref="DecideAsync"/> is het BESTAANDE accept-/reject-pad (#116/#124),
/// nu de enige plek die Status+ReviewedAt zet — de losse AdminEndpoints-acties
/// én <see cref="BulkDecideAsync"/> (#199, de bulk-actie per aanbevelingsgroep)
/// roepen hem aan, zodat er geen tweede plek is die kan uiteenlopen ("geen
/// nieuw autoriteitspad").</summary>
public class RelationTriageService(RbRulesDbContext db, RbAiClient ai)
{
    /// <summary>Cap per triage-run (#199-eis: "cap ~40/run") — zelfde
    /// "niet alles in één keer"-afweging als de andere gecapte mining-jobs
    /// (vgl. <see cref="CorrectionReevaluationService.DefaultRepairCap"/>).</summary>
    public const int DefaultCap = 40;

    private const int SectionSnippetChars = 220;
    private const int ResponseSnippetLength = 400;

    public async Task<RelationTriageRunResult> RunAsync(
        int maxItems = DefaultCap, Action<string>? progress = null, CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 200);

        // Kandidaten: nog niet getriaged (Recommendation null) én nog niet
        // door een mens beoordeeld (Status "unreviewed") — een mens-oordeel
        // wint altijd, dus geaccepteerde/verworpen voorstellen komen hier
        // nooit meer langs, ook niet als Recommendation toevallig null bleef.
        var query = db.Relations.Where(r => r.Status == "unreviewed" && r.Recommendation == null);

        var totalEligible = await query.CountAsync(ct);
        if (totalEligible == 0)
            return new(0, 0, 0, 0, 0, "geen relatievoorstellen te triageren", CapHit: false);

        var candidates = await query
            .OrderBy(r => r.DetectedAt).ThenBy(r => r.Id)
            .Take(maxItems)
            .ToListAsync(ct);
        // #190: vóór verwerking bepaald — gefaalde items in dít batch tellen
        // dus niet mee (vers-werk-semantiek, zelfde contract als
        // RelationMiningService/ClarificationMiningService/CorrectionReevaluationService).
        var capHit = totalEligible > candidates.Count;

        var judged = 0;
        var accepted = 0;
        var rejected = 0;
        var unsure = 0;
        var skipped = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var relation = candidates[i];
            progress?.Invoke($"relatie-triage {i + 1}/{candidates.Count} ({judged} beoordeeld)");

            var contextLines = await BuildContextAsync(relation, ct);
            var prompt = RelationTriage.BuildPrompt(
                relation.FromRef, relation.ToRef, relation.Kind, relation.Explanation, contextLines);

            string? raw;
            try
            {
                raw = await ai.AskAsync(prompt, RelationTriage.SystemPrompt, ct: ct);
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
                    Kind = "relationtriage", Ref = $"relation:{relation.Id}", Status = "error",
                    Detail = "rb-ai niet beschikbaar — voorstel overgeslagen, komt de volgende run terug",
                });
                skipped++;
                continue;
            }

            var result = RelationTriage.Parse(raw);
            if (result.Outcome == RelationTriageOutcome.Unusable)
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "relationtriage", Ref = $"relation:{relation.Id}", Status = "error",
                    Detail = "LLM-antwoord onbruikbaar — geen parseerbaar oordeel. "
                             + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
                });
                skipped++;
                continue;
            }

            relation.Recommendation = result.Recommendation;
            relation.RecommendationReason = ComposeReason(result);
            relation.RecommendedAt = DateTimeOffset.UtcNow;
            judged++;
            switch (result.Recommendation)
            {
                case "accept": accepted++; break;
                case "reject": rejected++; break;
                default: unsure++; break;
            }
            await db.SaveChangesAsync(ct);
        }

        var message = $"{judged} beoordeeld ({accepted} accept, {rejected} reject, {unsure} unsure), "
            + $"{skipped} overgeslagen"
            + (capHit ? $" — cap van {maxItems} bereikt, rest volgt bij de volgende run" : "");
        db.RunLogs.Add(new RunLog { Kind = "relationtriage", Ref = null, Status = "ok", Detail = message });
        await db.SaveChangesAsync(ct);
        return new(judged, accepted, rejected, unsure, skipped, message, capHit);
    }

    /// <summary>Refs blijven bewust geen eigen kolom (#199: het datamodel is
    /// drie nullable velden) — in de motivering gevouwen zodat de UI ze toch
    /// naast het bewijs kan tonen.</summary>
    private static string ComposeReason(RelationTriageJudgement result) =>
        result.Refs is { Count: > 0 }
            ? $"{result.Reason} (refs: {string.Join(", ", result.Refs)})"
            : result.Reason!;

    /// <summary>Het bestaande accept-/reject-pad (#116/#124): Status +
    /// ReviewedAt, optionele beheerder-notitie. De aanbeveling
    /// (Recommendation/RecommendationReason/RecommendedAt) blijft ongemoeid
    /// staan — herkomst van de beslissing blijft zichtbaar (#199 eis 4).</summary>
    public async Task<RelationDecisionOutcome> DecideAsync(
        long id, string decision, string? note, CancellationToken ct = default)
    {
        var relation = await db.Relations.FindAsync([id], ct);
        if (relation is null) return RelationDecisionOutcome.NotFound;
        ApplyDecision(relation, decision, note);
        await db.SaveChangesAsync(ct);
        return RelationDecisionOutcome.Applied;
    }

    /// <summary>Bulk-actie per aanbevelingsgroep (#199, "de machine sorteert
    /// voor, de mens klikt"): loopt <see cref="ApplyDecision"/> — dus
    /// letterlijk hetzelfde accept-/reject-pad als <see cref="DecideAsync"/>
    /// — na voor élk voorstel met deze aanbeveling dat nog "unreviewed" is,
    /// in één transactie (multi-row). Geen notitie: een bulk-klik draagt geen
    /// per-item tekst. Retourneert het aantal geraakte voorstellen.</summary>
    public async Task<int> BulkDecideAsync(
        string recommendation, string decision, CancellationToken ct = default)
    {
        if (decision is not ("accept" or "reject")) return 0;
        var normalized = recommendation.Trim().ToLowerInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var relations = await db.Relations
            .Where(r => r.Status == "unreviewed" && r.Recommendation == normalized)
            .ToListAsync(ct);
        foreach (var relation in relations) ApplyDecision(relation, decision, note: null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return relations.Count;
    }

    private static void ApplyDecision(Relation relation, string decision, string? note)
    {
        relation.Status = decision == "accept" ? "accepted" : "rejected";
        relation.ReviewedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(note)) relation.ReviewNote = note.Trim();
    }

    /// <summary>Goedkope, per-ref context (#199-eis: geen embeddings): een
    /// leesbare beschrijving + waar mogelijk een §-snippet, per soort ref.
    /// Alleen de mechanic-tak gebruikt ILike (zelfde patroon als
    /// RelationMiningService.BuildMechanicsContextAsync); de rest is exacte
    /// lookup op een sleutel die de ref zelf al draagt.</summary>
    private async Task<List<string>> BuildContextAsync(Relation relation, CancellationToken ct)
    {
        var lines = new List<string>();
        await DescribeRefAsync(relation.FromRef, lines, ct);
        await DescribeRefAsync(relation.ToRef, lines, ct);
        return lines;
    }

    private async Task DescribeRefAsync(string brainRef, List<string> lines, CancellationToken ct)
    {
        if (!BrainRef.TryParse(brainRef, out var parsed)) return;

        switch (parsed.Kind)
        {
            case BrainRefKind.Card:
            {
                var card = await db.Cards.AsNoTracking()
                    .Where(c => c.RiftboundId == parsed.Key)
                    .Select(c => new { c.Name, c.TextPlain })
                    .FirstOrDefaultAsync(ct);
                if (card is not null && !string.IsNullOrWhiteSpace(card.TextPlain))
                    lines.Add($"- {brainRef} — card '{card.Name}': {Snippet(card.TextPlain)}");
                break;
            }
            case BrainRefKind.Mechanic:
            {
                var pattern = $"%{EscapeLike(parsed.Key)}%";
                var chunk = await db.RuleChunks.AsNoTracking()
                    .Where(c => c.SectionCode != null && c.SectionCode != ""
                                && EF.Functions.ILike(c.Text, pattern))
                    .OrderBy(c => c.SourceId).ThenBy(c => c.ChunkIndex)
                    .Select(c => new { c.SourceId, Code = c.SectionCode!, c.Text })
                    .FirstOrDefaultAsync(ct);
                if (chunk is not null)
                    lines.Add($"- {brainRef} — §{chunk.Code}: {Snippet(chunk.Text)}");
                break;
            }
            case BrainRefKind.Section:
            {
                var slash = parsed.Key.IndexOf('/');
                if (slash <= 0) break;
                var sourceId = parsed.Key[..slash];
                var code = parsed.Key[(slash + 1)..];
                var text = await db.RuleChunks.AsNoTracking()
                    .Where(c => c.SourceId == sourceId && c.SectionCode == code)
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => c.Text)
                    .FirstOrDefaultAsync(ct);
                if (text is not null) lines.Add($"- {brainRef} — §{code}: {Snippet(text)}");
                break;
            }
            case BrainRefKind.Concept:
            {
                var doc = await db.KnowledgeDocs.AsNoTracking()
                    .Where(k => k.Kind == "primer" && k.Topic == parsed.Key)
                    .Select(k => new { k.Title, k.Body })
                    .FirstOrDefaultAsync(ct);
                if (doc is not null)
                    lines.Add($"- {brainRef} — concept '{doc.Title}': {Snippet(doc.Body)}");
                break;
            }
            case BrainRefKind.Claim:
            {
                if (!long.TryParse(parsed.Key, out var claimId)) break;
                var claim = await db.Claims.AsNoTracking()
                    .Where(c => c.Id == claimId)
                    .Select(c => new { c.TopicRef, c.Statement })
                    .FirstOrDefaultAsync(ct);
                if (claim is not null)
                    lines.Add($"- {brainRef} — community-claim over '{claim.TopicRef}': {Snippet(claim.Statement)}");
                break;
            }
            // Andere refKinds komen niet voor als relatie-eindpunt
            // (RelationMiner biedt ze nooit aan) — geen extra context.
        }
    }

    private static string Snippet(string text)
    {
        var flat = string.Join(' ',
            text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SectionSnippetChars ? flat : flat[..SectionSnippetChars] + "…";
    }

    /// <summary>LIKE-metatekens escapen (AdminOverviewService/RelationMiningService-patroon).</summary>
    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
