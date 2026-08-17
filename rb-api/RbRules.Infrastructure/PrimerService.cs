using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary><paramref name="Untranslated"/> (#266): geschreven docs zonder
/// bruikbare Nederlandse weergave (AI-uitval of een vertaling die de
/// speltermen-waarborg niet haalde) — die tonen op /primer het Engels.</summary>
public record PrimerResult(int Written, int Skipped, int Failed, int Untranslated = 0);

/// <summary>Kennislaag 1 (docs/KNOWLEDGE.md): destilleert per concept een
/// primer-doc uit de regelindex — samenhangend spelbegrip mét §-verwijzingen.
/// Nieuwe/gewijzigde docs zijn draft; de beheerder keurt ze in /admin, pas
/// daarna doen ze mee in de /ask-context.</summary>
public class PrimerService(
    RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai,
    ILogger<PrimerService> logger)
{
    private const int ChunksPerTopic = 10;

    // #187: afgeleide/gesynthetiseerde kennis wordt in de brontaal (Engels)
    // opgeslagen, dicht bij de officiële bewoording (docs/CONVENTIONS.md). De
    // UI en /ask-antwoorden blijven Nederlands — dat scheidt AskService.
    // BasePrompt af, deze primer-tekst is context, geen eindantwoord.
    // #266: /primer is óók UI, dus krijgt elk doc er een Nederlandse
    // weergavetekst bij (PrimerTranslation). Die is puur presentatie: de
    // Engelse body hieronder blijft canoniek en is wat embedt en retrievet.
    private const string SystemPrompt = """
        You write a concise game-understanding document for Riftbound TCG
        players, based on the official rule sections provided. Requirements:
        - 200 to 350 words, in English, close to the official wording
        - Explain the FLOW (what happens when, and why), not just isolated
          facts; mention the most common misconception if there is one
        - Reference sections inline as (§123.4) where you base something on
          them
        - No introduction or closing remarks, no markdown headers — just
          running text in short paragraphs
        - Base yourself exclusively on the given sections; don't claim
          anything that isn't in them
        """;

    public async Task<PrimerResult> GenerateAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        // Kosten-grootboek (#328): tokens optellen over álle LLM-calls van
        // deze run (genereren + vertalen); null zolang geen enkele call usage
        // meldde — onbekend is niet 0.
        var runStart = DateTimeOffset.UtcNow;
        AiUsage? runUsage = null;
        void Tally(AiUsage? u) => runUsage = u is null ? runUsage
            : runUsage is null ? u
            : new AiUsage(runUsage.InputTokens + u.InputTokens, runUsage.OutputTokens + u.OutputTokens);
        var written = 0;
        var skipped = 0;
        var failed = 0;
        var untranslated = 0;
        var n = 0;
        foreach (var topic in PrimerTopics.All)
        {
            n++;
            var existing = await db.KnowledgeDocs.FirstOrDefaultAsync(
                k => k.Kind == "primer" && k.Topic == topic.Key, ct);
            if (existing is { Status: "approved" } && !force)
            {
                skipped++;
                continue;
            }

            progress?.Invoke($"primer {n}/{PrimerTopics.All.Count}: {topic.Title}");

            // Relevante secties voor dit concept (semantisch).
            var qv = await embeddings.EmbedOneAsync($"{topic.Title}. {topic.Query}", ct);
            var chunks = await db.RuleChunks.AsNoTracking()
                .Where(c => c.Embedding != null && c.SectionCode != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(ChunksPerTopic)
                .Select(c => new { c.SectionCode, c.Text })
                .ToListAsync(ct);
            if (chunks.Count == 0) { failed++; continue; }

            var context = string.Join("\n\n", chunks.Select(c => $"§{c.SectionCode}: {c.Text}"));
            var answer = await ai.AskWithUsageAsync(
                $"Concept: {topic.Title}\n\nOfficiële regelsecties:\n{context}",
                SystemPrompt, ct: ct);
            Tally(answer?.Usage);
            var body = answer?.Answer;
            if (string.IsNullOrWhiteSpace(body)) { failed++; continue; }

            // Nederlandse weergave (#266) meteen bij de generatie, zodat ze
            // onderdeel is van de draft die de beheerder goedkeurt.
            progress?.Invoke($"primer {n}/{PrimerTopics.All.Count}: {topic.Title} — vertalen");
            var bodyNl = await TranslateAsync(body, ct, Tally);
            if (bodyNl is null) untranslated++;

            var refs = string.Join(", ", chunks.Select(c => c.SectionCode));
            // Embedding blijft op de canonieke Engelse tekst — de Nederlandse
            // weergave doet niet mee in retrieval of /ask-context.
            // TryEmbedAsync i.p.v. EmbedOneAsync (#299): de kap zit sinds #301 in
            // EmbeddingService, en een gekapte vector hoort op de rij te landen —
            // anders is "welke vectoren zijn partieel?" over een half jaar
            // onbeantwoordbaar. Gooit nog steeds bij uitval, zoals dit pad altijd al
            // deed; alleen de kap-feiten komen er nu bij.
            var docEmbed = await embeddings.TryEmbedAsync([$"{topic.Title}\n{body}"], ct);
            if (!docEmbed.Ok)
                throw new InvalidOperationException(
                    $"Embedding mislukt ({docEmbed.Outcome}): {docEmbed.Error ?? "onbekende fout"}");
            var doc = existing;
            if (doc is null)
            {
                doc = new KnowledgeDoc
                {
                    Kind = "primer", Topic = topic.Key, Title = topic.Title, Body = body,
                };
                db.KnowledgeDocs.Add(doc);
            }
            PrimerDraft.Apply(doc, topic.Title, body, bodyNl, refs, DateTimeOffset.UtcNow);
            doc.Embedding = docEmbed.Vectors![0];
            doc.EmbeddingModel = EmbeddingConfig.Model;
            doc.EmbeddingTruncatedAt = docEmbed.Capped > 0 ? docEmbed.CappedAt : null;
            await db.SaveChangesAsync(ct);
            written++;
        }
        // Eén platform-regel per run in het kosten-grootboek (#328) —
        // best-effort: een mislukte boeking mag de geschreven drafts niet raken.
        try
        {
            db.AiUsageEvents.Add(await AiUsageMeter.CreateEventAsync(
                db, AiUsageEvent.OriginPlatform, "primer",
                AskPathModels.Resolve("cheap"), userId: null,
                runUsage?.InputTokens, runUsage?.OutputTokens,
                (int)Math.Min((long)(DateTimeOffset.UtcNow - runStart).TotalMilliseconds, int.MaxValue),
                ok: failed == 0, ct));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "primer-run niet geboekt in ai_usage_event");
        }
        return new(written, skipped, failed, untranslated);
    }

    /// <summary>Vertaalt één primer-body naar het Nederlands, mét de
    /// speltermen-waarborg (#266): een vertaling die een Riftbound-spelterm
    /// vernederlandst of een §-verwijzing laat vallen, wordt weggegooid
    /// (null) — de pagina toont dan de canonieke Engelse tekst. Liever het
    /// Engels dan een "slagveld" naast een §-citaat. AI-uitval is hetzelfde
    /// pad: null, geen crash.</summary>
    public async Task<string?> TranslateAsync(
        string body, CancellationToken ct = default, Action<AiUsage?>? onUsage = null)
    {
        var res = await ai.AskWithUsageAsync(body, PrimerTranslation.SystemPrompt, ct: ct);
        onUsage?.Invoke(res?.Usage);
        var dutch = res?.Answer;
        if (string.IsNullOrWhiteSpace(dutch)) return null;

        var leaks = PrimerTranslation.Leaks(body, dutch);
        if (leaks.Count == 0) return dutch.Trim();
        logger.LogWarning(
            "Primer-vertaling afgekeurd, niet behouden in de vertaling: {Leaks}",
            string.Join(", ", leaks));
        return null;
    }
}
