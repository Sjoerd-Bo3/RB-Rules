using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van een uitleg-aanvraag: <c>Found</c> = beide kaarten
/// bestaan; <c>Explanation</c> is null bij AI-uitval (verwacht pad).</summary>
public record ExplainResult(string? Explanation, bool Cached, bool Found = true);

/// <summary>"Waarom lijken deze kaarten op elkaar?" (#30) — LLM-uitleg met
/// cache op het geordende kaartpaar. Uit het endpoint getrokken (#59): de
/// paar-normalisatie, cache-lookup/-opslag en AI-call horen niet in de route.</summary>
public class SimilarityExplainService(
    RbRulesDbContext db, RbAiClient ai, RequestUserContext? userContext = null)
{
    private const string SystemPrompt = """
        Je legt in één of twee Nederlandse zinnen uit op welk semantisch vlak twee
        Riftbound-kaarten op elkaar lijken: welk gedrag, welke rol of welk
        spelplan delen ze? Wees concreet ("beide sturen units terug naar de
        base") en noem geen voor de hand liggende metadata zoals set of rarity.
        Antwoord met alleen die uitleg, zonder inleiding.
        """;

    public async Task<ExplainResult> ExplainAsync(
        string id, string otherId, CancellationToken ct = default)
    {
        var (a, b) = CardText.OrderedPair(id, otherId);
        var cached = await db.SimilarityExplanations
            .FirstOrDefaultAsync(e => e.CardAId == a && e.CardBId == b, ct);
        if (cached is not null) return new(cached.Text, Cached: true);

        // Prompt-invoer: kaartfeiten zonder embedding-vector of tracking (#43).
        var pair = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == id || c.RiftboundId == otherId)
            .WithoutEmbedding()
            .ToListAsync(ct);
        var cardA = pair.FirstOrDefault(c => c.RiftboundId == id);
        var cardB = pair.FirstOrDefault(c => c.RiftboundId == otherId);
        if (cardA is null || cardB is null) return new(null, Cached: false, Found: false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await ai.AskWithUsageAsync(
            $"Kaart 1: {CardText.DescribeForPrompt(cardA)}\n\nKaart 2: {CardText.DescribeForPrompt(cardB)}",
            SystemPrompt, ct: ct);
        sw.Stop();
        // Kosten-grootboek (#328, review): de cache-VULLING is de LLM-call en
        // die triggert de bezoeker (eerste klik op "waarom lijken ze?") — dus
        // origin user, mét attributie; een cache-hit boekt niets. Best-effort.
        try
        {
            db.AiUsageEvents.Add(await AiUsageMeter.CreateEventAsync(
                db, AiUsageEvent.OriginUser, "explain", AskPathModels.Resolve("cheap"),
                userContext?.User?.Id, res?.Usage?.InputTokens, res?.Usage?.OutputTokens,
                (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue), ok: res?.Answer != null, ct));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var entry in db.ChangeTracker.Entries<AiUsageEvent>()
                         .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added).ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
        var raw = res?.Answer;
        if (raw is null) return new(null, Cached: false);

        db.SimilarityExplanations.Add(new SimilarityExplanation
        {
            CardAId = a, CardBId = b, Text = raw.Trim(), Model = "rb-ai",
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* race met parallel verzoek — cache bestaat al */ }
        return new(raw.Trim(), Cached: false);
    }
}
