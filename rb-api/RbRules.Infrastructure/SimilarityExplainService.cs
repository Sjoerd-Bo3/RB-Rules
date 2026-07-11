using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van een uitleg-aanvraag: <c>Found</c> = beide kaarten
/// bestaan; <c>Explanation</c> is null bij AI-uitval (verwacht pad).</summary>
public record ExplainResult(string? Explanation, bool Cached, bool Found = true);

/// <summary>"Waarom lijken deze kaarten op elkaar?" (#30) — LLM-uitleg met
/// cache op het geordende kaartpaar. Uit het endpoint getrokken (#59): de
/// paar-normalisatie, cache-lookup/-opslag en AI-call horen niet in de route.</summary>
public class SimilarityExplainService(RbRulesDbContext db, RbAiClient ai)
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

        var cardA = await db.Cards.FindAsync([id], ct);
        var cardB = await db.Cards.FindAsync([otherId], ct);
        if (cardA is null || cardB is null) return new(null, Cached: false, Found: false);

        var raw = await ai.AskAsync(
            $"Kaart 1: {CardText.DescribeForPrompt(cardA)}\n\nKaart 2: {CardText.DescribeForPrompt(cardB)}",
            SystemPrompt, ct: ct);
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
