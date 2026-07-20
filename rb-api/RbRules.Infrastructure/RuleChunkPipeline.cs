using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitslag per bron. <paramref name="FailureSummary"/> gevuld = deze bron is
/// NIET geïndexeerd omdat de embed-stap faalde; <paramref name="Chunks"/> is dan 0 en
/// de bestaande regelindex van die bron staat er onveranderd (#282).</summary>
/// <param name="Capped">Chunks waarvan alléén de embed-invoer is ingekort omdat de
/// chunk boven het tekenbudget uitkwam (#293). De opgeslagen <c>RuleChunk.Text</c>
/// blijft volledig — de bezoeker ziet dus nooit een afgekapte regeltekst; het is de
/// vector die op de eerste N tekens gebaseerd is. Hoort desondanks in de melding:
/// stil invoerverlies is precies wat #282/#284 wegnamen.</param>
/// <param name="CappedLongest">De langste ORIGINELE chunk-tekst van deze bron (#302) —
/// het getal dat zegt hoe ver eroverheen we zaten, en dus of het budget knelt of ruim
/// zit. 0 als er niets gekapt is.</param>
public record RuleIndexResult(
    string SourceId, int Chunks, string FailureSummary = "", int Capped = 0,
    int CappedLongest = 0)
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
        if (failed.Count > 0)
            head += $" · {failed.Count} bron(nen) overgeslagen — "
                + string.Join("; ", failed.Select(f => $"{f.SourceId}: {f.FailureSummary}"));
        // Afkappen is geen fout maar wel invoerverlies, dus het staat er altijd bij
        // (#293) — óók in een verder geslaagde run.
        var capped = results.Sum(r => r.Capped);
        if (capped == 0) return head;
        // De langste ORIGINELE chunk erbij (#302): "alleen de embed-invoer afgekapt"
        // zegt niet of het om 6001 of om 20000 tekens ging, en dat is juist wat een
        // beheerder nodig heeft om te wegen of het budget knelt.
        var longest = results.Max(r => r.CappedLongest);
        return head + $" · {capped} chunk(s) te lang voor het embed-budget "
            + $"(langste invoer {longest}), alleen de embed-invoer afgekapt "
            + "(opgeslagen tekst blijft volledig)";
    }

    /// <summary>Kapte deze run ergens? Bepaalt de <c>warn</c>-status van de
    /// run_log-regel (#299) — zonder die status hangt de melding onder "ok" en toont
    /// het beheer-paneel haar nooit.</summary>
    public static bool AnyCapped(this IReadOnlyList<RuleIndexResult> results) =>
        results.Any(r => r.Capped > 0);
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
            // boven het tekenbudget uitkomen; Card Errata zit in de praktijk al op
            // 3908 tekens). EmbedBatching gaf zo'n uitschieter een eigen verzoek in
            // plaats van hem weg te laten — maar dat alléén redt hem niet als de chunk
            // zélf boven de klip ligt (#293), en met de alles-of-niets-regel hieronder
            // zou die ene chunk de hele regelindex van deze bron permanent blokkeren.
            // Dus kappen we de embed-INVOER op het budget. c.Text blijft ongemoeid: de
            // opgeslagen en getoonde regeltekst is volledig, alleen de vector kijkt
            // naar de eerste N tekens.
            // Sinds #301 kapt EmbeddingService sowieso, ongeacht de aanroeper — deze
            // kap is daar een no-op naast. Hij blijft staan omdat de pijplijn moet
            // weten WELKE chunk gekapt is, om dat per rij vast te leggen
            // (RuleChunk.EmbeddingTruncatedAt, #299).
            var capped = EmbedBatching.CapItems(
                [.. chunks.Select(c => c.Text)], _settings.BatchChars);
            var texts = capped.Texts;
            var tally = new EmbedOutcomeTally();
            foreach (var range in EmbedBatching.Split(texts, _settings.BatchSize, _settings.BatchChars))
            {
                var (offset, count) = range.GetOffsetAndLength(chunks.Count);
                var result = await embeddings.TryEmbedAsync(
                    [.. texts.Skip(offset).Take(count)], ct);
                tally.Add(result.Outcome, count, result.Error);
                if (!result.Ok) break; // deze bron is verloren; niet de volgende
                // LET OP bij het uitbreiden van deze lus: `texts` is de (mogelijk
                // GEKAPTE) embed-invoer en `chunks` zijn de te PERSISTEREN entiteiten.
                // Alleen de vector mag hier overgezet worden — een `chunks[i].Text =
                // texts[i]` zou de afkapping de database in schrijven en daarmee de
                // regels-browser (§-permalinks) een half afgebroken regeltekst tonen.
                // Die invariant is aan deze kant NIET door een test afgedekt: EF
                // InMemory kent geen ExecuteDeleteAsync, dus het geslaagde swap-pad
                // hieronder is in RuleChunkPipelineTests niet te draaien. De
                // kaart-pijplijn bewaakt hetzelfde patroon wél
                // (EmbedOutcomeTests.Embed_KaartBovenHetBudget…), dus lees die test
                // als de bedoeling en houd deze lus daarmee in de pas.
                for (var k = 0; k < count; k++)
                {
                    chunks[offset + k].Embedding = result.Vectors![k];
                    chunks[offset + k].EmbeddingModel = EmbeddingConfig.Model;
                    // Provenance op de RIJ (#299) — LET OP: dit is de kaplengte, niet
                    // de tekst. `chunks[i].Text` blijft het volledige origineel; alleen
                    // de vector kijkt naar de eerste N tekens, en dat staat vanaf nu
                    // ook op de rij in plaats van alleen in een verouderende
                    // run_log-regel.
                    chunks[offset + k].EmbeddingTruncatedAt =
                        texts[offset + k].Length < chunks[offset + k].Text.Length
                            ? texts[offset + k].Length
                            : null;
                }
            }

            if (tally.HasFailures)
            {
                // ALLES-OF-NIETS per bron: half-geëmbedde chunks inswappen zou de
                // bestaande, complete index vervangen door een gatenkaas. Beter de
                // oude index laten staan en het melden. De chunks zijn nooit aan de
                // context toegevoegd, dus er valt niets terug te draaien — ze
                // verdwijnen met de lus-iteratie.
                results.Add(new(src.Id, 0, tally.Summary, capped.CappedCount,
                    capped.CappedCount > 0 ? capped.LongestOriginal : 0));
                continue;
            }

            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await db.RuleChunks.Where(c => c.SourceId == src.Id).ExecuteDeleteAsync(ct);
                db.RuleChunks.AddRange(chunks);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            results.Add(new(src.Id, chunks.Count, Capped: capped.CappedCount,
                CappedLongest: capped.CappedCount > 0 ? capped.LongestOriginal : 0));
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
                // "warn" bij kapping (#299), zelfde afweging als in
                // CardEmbeddingPipeline: geen fout (de bron is geïndexeerd), maar ook
                // geen "ok" — onder "ok" bleef de melding onzichtbaar in beheer.
                Status = anyFailed ? "error" : results.AnyCapped() ? "warn" : "ok",
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
