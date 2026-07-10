using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record Citation(int N, string SourceName, string Url, string? Section, int Trust);
public record AskResult(string Answer, IReadOnlyList<Citation> Citations);

/// <summary>Rulings-Q&A met hybride retrieval (audit-fix: niet meer alleen
/// vector): vector-zoek + Postgres full-text, gefuseerd met RRF; daarna
/// kaartfeiten + geverifieerde rulings + antwoord via rb-ai met [n]-citaten.</summary>
public class AskService(RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
{
    private const int TopK = 8;
    private const int RrfK = 60;

    private const string SystemPrompt = """
        Je bent een Riftbound TCG regels-assistent. Beantwoord de vraag op basis
        van de meegegeven context-fragmenten. Citeer bronnen met [n] en noem het
        sectienummer (§) waar beschikbaar. Als de context het antwoord niet
        bevat, zeg dat eerlijk. Officiële bronnen (lagere trust = betrouwbaarder)
        gaan vóór community. GEVERIFIEERDE RULINGS zijn gezaghebbend en gaan
        vóór alles. Antwoord in het Nederlands.
        """;

    public async Task<AskResult> AskAsync(string question, CancellationToken ct = default)
    {
        // 1. Vector-kanaal
        var qv = await embeddings.EmbedOneAsync(question, ct);
        var vectorHits = await db.RuleChunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(TopK * 2)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // 2. Full-text-kanaal (Engels — de bronnen zijn Engels)
        var textHits = await db.RuleChunks
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", question)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", question)))
            .Take(TopK * 2)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // 3. RRF-fusie
        var scores = new Dictionary<long, double>();
        void Accumulate(List<long> ids)
        {
            for (var rank = 0; rank < ids.Count; rank++)
                scores[ids[rank]] = scores.GetValueOrDefault(ids[rank]) + 1.0 / (RrfK + rank + 1);
        }
        Accumulate(vectorHits);
        Accumulate(textHits);

        var topIds = scores.OrderByDescending(kv => kv.Value).Take(TopK).Select(kv => kv.Key).ToList();
        if (topIds.Count == 0)
            return new("Er is nog geen geïndexeerde regeltekst — draai eerst de regel-index op /admin.", []);

        var chunks = await db.RuleChunks
            .Where(c => topIds.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.Text, c.SectionCode,
                s.Name, s.Url, s.TrustTier,
            })
            .ToListAsync(ct);
        var ordered = topIds.Select(id => chunks.First(c => c.Id == id)).ToList();

        var citations = ordered.Select((c, i) =>
            new Citation(i + 1, c.Name, c.Url, c.SectionCode, c.TrustTier)).ToList();

        var context = string.Join("\n\n", ordered.Select((c, i) =>
            $"[{i + 1}] ({c.Name}, trust {c.TrustTier}{(c.SectionCode is null ? "" : $", §{c.SectionCode}")})\n{c.Text}"));

        // 4. Kaartfeiten (incl. mechanieken en ban-status) voor herkende kaarten
        var cardBlock = await CardFactsAsync(question, ct);

        // 5. Geverifieerde rulings (self-learning override-laag)
        var rulings = await db.Corrections
            .Where(c => c.Status == "verified")
            .OrderByDescending(c => c.VerifiedAt)
            .Take(3)
            .Select(c => c.Text)
            .ToListAsync(ct);
        var rulingBlock = rulings.Count == 0
            ? ""
            : "\n\nGEVERIFIEERDE RULINGS (gezaghebbend):\n" +
              string.Join("\n", rulings.Select(r => $"- {r}"));

        var answer = await ai.AskAsync(
            $"Context-fragmenten:\n{context}{cardBlock}{rulingBlock}\n\nVraag: {question}",
            SystemPrompt, ct: ct)
            ?? "AI is niet beschikbaar — probeer het later opnieuw.";

        return new(answer, citations);
    }

    private async Task<string> CardFactsAsync(string question, CancellationToken ct)
    {
        var q = question.ToLowerInvariant();
        var names = await db.Cards
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);
        var hits = names
            .Where(c => c.Name.Length >= 3 && q.Contains(c.Name.ToLowerInvariant()))
            .Take(3)
            .Select(c => c.RiftboundId)
            .ToList();
        if (hits.Count == 0) return "";

        var cards = await db.Cards.Where(c => hits.Contains(c.RiftboundId)).ToListAsync(ct);
        var bannedNames = await db.BanEntries
            .Where(b => hits.Contains(b.CardRiftboundId!))
            .Select(b => b.CardRiftboundId)
            .ToListAsync(ct);

        var lines = cards.Select(c =>
            $"- {c.Name} — {string.Join(" ", new[] { c.Supertype, c.Type }.Where(s => s != null))}. " +
            $"Domains: {string.Join(", ", c.Domains)}. Energy {c.Energy?.ToString() ?? "—"}, Might {c.Might?.ToString() ?? "—"}. " +
            (c.Mechanics is { Length: > 0 } m ? $"Mechanieken: {string.Join(", ", m)}. " : "") +
            (bannedNames.Contains(c.RiftboundId) ? "⚠ STAAT OP DE BANLIJST. " : "") +
            (c.TextPlain is null ? "" : $"Tekst: {c.TextPlain[..Math.Min(c.TextPlain.Length, 240)]}"));

        return "\n\nKaartgegevens (gezaghebbend voor stats/mechanieken):\n" + string.Join("\n", lines);
    }
}
