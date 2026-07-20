using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitslag per bron. <paramref name="FailureSummary"/> gevuld = deze bron is
/// NIET geïndexeerd omdat de embed-stap faalde; <paramref name="Chunks"/> is dan 0 en
/// de bestaande regelindex van die bron staat er onveranderd (#282).</summary>
public record RuleIndexResult(string SourceId, int Chunks, string FailureSummary = "")
{
    public bool Failed => FailureSummary.Length > 0;
}

/// <summary>Eén samenvattingsregel over een hele indexeer-run (#282-review). Bestaat
/// omdat de aanroepers hun eigen string bouwden en de uitval daarin wegviel: met alle
/// zes de bronnen omgevallen meldde de job letterlijk "0 sectie-chunks over 6 bronnen
/// (herbouwd)" met status ok — `Count` telde de gefaalde bronnen gewoon mee.</summary>
public static class RuleIndexResults
{
    /// <param name="rebuilt">De volledige herbouw ("rules", force:true).</param>
    /// <param name="incremental">De incrementele indexering ("rules-index", #258) —
    /// die telt alleen nieuwe/gewijzigde bronnen, dus dat hoort in de regel te staan.</param>
    public static string Summarize(
        this IReadOnlyList<RuleIndexResult> results,
        bool rebuilt = false, bool incremental = false)
    {
        var ok = results.Where(r => !r.Failed).ToList();
        var failed = results.Where(r => r.Failed).ToList();
        var scope = incremental ? " nieuwe/gewijzigde bronnen" : " bronnen";
        var head = $"{ok.Sum(r => r.Chunks)} sectie-chunks over {ok.Count}{scope}"
            + (rebuilt ? " (herbouwd)" : "");
        return failed.Count == 0
            ? head
            : head + $" · {failed.Count} bron(nen) overgeslagen — "
                + string.Join("; ", failed.Select(f => $"{f.SourceId}: {f.FailureSummary}"));
    }
}

/// <summary>Indexeert het nieuwste document per bron: sectie-parse → chunks met
/// chunk_index + section_code (audit-fixes) → embeddings. Idempotent per
/// document: al geïndexeerde documenten worden overgeslagen.
///
/// UITVAL IS DATA (#282): valt Ollama om tijdens het embedden van een bron, dan wordt
/// die bron overgeslagen — de oud-weg/nieuw-erin-swap gaat niet door, dus de
/// bestaande regelindex blijft intact — en gaat de run door met de volgende bron. De
/// uitval komt terug in het resultaat én in run_log; hij verdwijnt niet meer als
/// exception in een catch bij de aanroeper.</summary>
public class RuleChunkPipeline(
    RbRulesDbContext db, EmbeddingService embeddings, EmbeddingSettings? settings = null)
{
    private readonly EmbeddingSettings _settings = settings ?? EmbeddingSettings.Default;

    public async Task<List<RuleIndexResult>> RunAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<RuleIndexResult>();
        // IgnoredAt (#180): een genegeerde bron levert per beoordeling niets
        // op — geen her-indexering/embeddings meer (zelfde bereik-afspraak
        // als de scan-lus; bestaande rule_chunks blijven gewoon staan).
        var sources = await db.Sources
            .Where(s => s.Enabled && s.IgnoredAt == null)
            .ToListAsync(ct);

        foreach (var src in sources)
        {
            progress?.Invoke($"document van {src.Name} controleren");
            var doc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);
            if (doc is null) continue;

            // force herbouwt ook al geïndexeerde documenten (bijv. na een
            // parser-verbetering); standaard alleen nieuwe documenten.
            var alreadyIndexed = await db.RuleChunks.AnyAsync(c => c.DocumentId == doc.Id, ct);
            if (alreadyIndexed && !force) continue;

            var sections = RuleSectionParser.Parse(doc.Content);
            if (sections.Count == 0) continue;

            var chunks = sections.Select((s, i) => new RuleChunk
            {
                DocumentId = doc.Id,
                SourceId = src.Id,
                SectionCode = string.IsNullOrEmpty(s.Code) || s.Code == "intro" ? null : s.Code,
                ChunkIndex = i,
                Text = s.Text,
                Page = s.Page,
            }).ToList();

            // Eerst volledig embedden (minutenlange, fallibele netwerkstap) —
            // pas daarna oud-weg/nieuw-erin in één transactie, zodat er nooit
            // een venster zonder regelindex is (review-fix).
            //
            // Regel-secties zijn de ZWAARSTE embed-verzoeken in het systeem (richtlijn
            // RuleSectionParser.MaxSectionLength = 2400 tekens per stuk), dus juist
            // hier knijpt het tekenbudget van EmbeddingSettings (#282). Let op: 2400
            // is een streefwaarde, geen harde grens — SplitLong knipt op zinsgrens en
            // laat één zin die zelf langer is heel (een punteloze tabeldump kan dus
            // boven het tekenbudget uitkomen). EmbedBatching geeft zo'n uitschieter
            // een eigen verzoek in plaats van hem weg te laten.
            var texts = chunks.Select(c => c.Text).ToList();
            var tally = new EmbedOutcomeTally();
            foreach (var range in EmbedBatching.Split(texts, _settings.BatchSize, _settings.BatchChars))
            {
                var (offset, count) = range.GetOffsetAndLength(chunks.Count);
                var result = await embeddings.TryEmbedAsync([.. texts.GetRange(offset, count)], ct);
                tally.Add(result.Outcome, count);
                if (!result.Ok) break; // deze bron is verloren; niet de volgende
                for (var k = 0; k < count; k++)
                {
                    chunks[offset + k].Embedding = result.Vectors![k];
                    chunks[offset + k].EmbeddingModel = EmbeddingConfig.Model;
                }
            }

            if (tally.HasFailures)
            {
                // ALLES-OF-NIETS per bron: half-geëmbedde chunks inswappen zou de
                // bestaande, complete index vervangen door een gatenkaas. Beter de
                // oude index laten staan en het melden. De chunks zijn nooit aan de
                // context toegevoegd, dus er valt niets terug te draaien — ze
                // verdwijnen met de lus-iteratie.
                results.Add(new(src.Id, 0, tally.Summary));
                continue;
            }

            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await db.RuleChunks.Where(c => c.SourceId == src.Id).ExecuteDeleteAsync(ct);
                db.RuleChunks.AddRange(chunks);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            results.Add(new(src.Id, chunks.Count));
        }

        // Niets gedaan = geen nieuws (alle bronnen al geïndexeerd, de normale tick).
        if (results.Count > 0) await LogRunAsync(results, ct);
        return results;
    }

    /// <summary>Elke run mét werk landt in run_log, ongeacht de aanroeper (beheer-knop,
    /// job, scheduler-tick) — de scheduler logde uitval voorheen hooguit als
    /// "Her-index/bans overgeslagen (Ollama/rb-ai onbereikbaar?)" naar de containerlog.
    /// De ok-regel hoort er net zo goed bij (#282-review): zonder herstel-melding
    /// blijft een oude foutregel de nieuwste embed-regel en dooft het alarm in beheer
    /// nooit.</summary>
    private async Task LogRunAsync(List<RuleIndexResult> results, CancellationToken ct)
    {
        var anyFailed = results.Any(r => r.Failed);
        try
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "embed",
                Ref = "rules",
                Status = anyFailed ? "error" : "ok",
                Detail = results.Summarize()
                    + (anyFailed ? " — bestaande regelindex blijft staan" : ""),
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Loggen mag een run-afronding nooit blokkeren (conventie).
        }
    }
}
