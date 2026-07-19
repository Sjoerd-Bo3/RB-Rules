using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record MiningResult(int Mined, int Remaining, int Failed, int NewCandidates);

/// <summary>F3: mine mechanieken/triggers/effects uit kaartteksten via rb-ai.
/// Idempotent: alleen kaarten met tekst die nog niet gemined zijn
/// (Mechanics == null). Herhaalbaar per set-release. Evolutie (#52): het
/// vocabulaire = seed + geaccepteerde keywords, en elke run rapporteert
/// bracketed termen buiten dat vocabulaire als kandidaat voor de reviewqueue.</summary>
public class MechanicMiningService(RbRulesDbContext db, RbAiClient ai)
{
    private const int BatchSize = 8;

    public async Task<MiningResult> RunAsync(
        int maxBatches = 25, DateTimeOffset? deadline = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var accepted = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Status == "accepted")
            .OrderBy(k => k.Term)
            .Select(k => k.Term)
            .ToListAsync(ct);

        var todo = await db.Cards
            .Where(c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != ""
                        && c.VariantOf == null)
            .OrderBy(c => c.RiftboundId)
            .Take(maxBatches * BatchSize)
            .ToListAsync(ct);

        var mined = 0;
        var failed = 0;
        var done = 0;
        foreach (var batch in todo.Chunk(BatchSize))
        {
            // Nachtrun-deadline (#245): stop netjes op venster-einde — nog niet
            // gemijnde kaarten blijven Mechanics==null en komen de volgende run terug.
            if (deadline is { } dl && DateTimeOffset.UtcNow >= dl) break;
            done += batch.Length;
            progress?.Invoke($"kaartteksten analyseren via LLM: {done}/{todo.Count} in deze run");
            var raw = await ai.AskAsync(
                MechanicMiner.BuildPrompt(batch), MechanicMiner.GetSystemPrompt(accepted), ct: ct);
            var parsed = raw is null ? [] : MechanicMiner.ParseBatch(raw, accepted);
            var byId = parsed.ToDictionary(p => p.Id);

            foreach (var card in batch)
            {
                if (byId.TryGetValue(card.RiftboundId, out var m))
                {
                    card.Mechanics = m.Mechanics;
                    card.Triggers = m.Triggers;
                    card.Effects = m.Effects;
                    mined++;
                }
                else
                {
                    failed++; // blijft null → volgende run opnieuw
                }
            }
            await db.SaveChangesAsync(ct);
        }

        progress?.Invoke("keyword-kandidaten zoeken in kaartteksten");
        var newCandidates = await HarvestKeywordCandidatesAsync(accepted, ct);

        // Zelfde predicaat als de todo-query (review-fix: zonder het
        // VariantOf-filter werd Remaining nooit 0 zolang er varianten bestaan).
        var remaining = await db.Cards.CountAsync(
            c => c.Mechanics == null && c.TextPlain != null && c.TextPlain != ""
                 && c.VariantOf == null, ct);
        return new(mined, remaining, failed, newCandidates);
    }

    /// <summary>Scant álle canonieke kaartteksten op bracketed termen buiten
    /// het vocabulaire (deterministisch, geen LLM) en zet nieuwe termen als
    /// kandidaat in de reviewqueue. Occurrences worden bijgewerkt zodat de
    /// beheerder op impact kan sorteren; eerder verworpen termen blijven
    /// verworpen en komen dus niet opnieuw de queue in.</summary>
    private async Task<int> HarvestKeywordCandidatesAsync(
        IReadOnlyList<string> accepted, CancellationToken ct)
    {
        var vocabulary = MechanicMiner.Vocabulary(accepted);
        var texts = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.TextPlain != null && c.TextPlain != "")
            .Select(c => c.TextPlain!)
            .ToListAsync(ct);

        // Aantal kaarten per term (case-insensitive; eerst geziene spelling wint).
        var counts = new Dictionary<string, (string Term, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        foreach (var term in MechanicMiner.ExtractKeywordCandidates(text, vocabulary))
        {
            counts[term] = counts.TryGetValue(term, out var c)
                ? (c.Term, c.Count + 1)
                : (term, 1);
        }
        if (counts.Count == 0) return 0;

        var known = await db.MechanicKeywords.ToListAsync(ct);
        var knownByTerm = known.ToDictionary(k => k.Term, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var (term, count) in counts.Values)
        {
            if (knownByTerm.TryGetValue(term, out var row))
            {
                row.Occurrences = count; // status blijft staan (ook rejected)
            }
            else
            {
                db.MechanicKeywords.Add(new MechanicKeyword { Term = term, Occurrences = count });
                added.Add(term);
            }
        }
        if (added.Count > 0)
        {
            // Zichtbaar in "Recente activiteit": hier is beheer-actie gewenst.
            db.RunLogs.Add(new RunLog
            {
                Kind = "mine", Ref = "keywords", Status = "new",
                Detail = $"{added.Count} nieuwe keyword-kandidaten: {string.Join(", ", added.OrderBy(t => t))}",
            });
        }
        await db.SaveChangesAsync(ct);
        return added.Count;
    }
}
