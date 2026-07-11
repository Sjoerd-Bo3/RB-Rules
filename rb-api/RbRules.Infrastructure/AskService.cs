using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record Citation(
    int N, string SourceName, string Url, string? Section, int Trust,
    string? Text = null, string? PdfUrl = null, int? Page = null);

public record AskCard(
    string RiftboundId, string Name, string? Type, string? Supertype,
    string[] Domains, int? Energy, int? Might, string? TextPlain,
    string[]? Mechanics, string? ImageUrl, bool Banned);

public record AskResult(
    string Answer, IReadOnlyList<Citation> Citations,
    IReadOnlyList<AskCard> Cards, string QuestionType);

/// <summary>Rulings-Q&A met hybride retrieval (audit-fix: niet meer alleen
/// vector): vector-zoek + Postgres full-text, gefuseerd met RRF; daarna
/// kaartfeiten + geverifieerde rulings + antwoord via rb-ai met [n]-citaten.</summary>
public class AskService(RbRulesDbContext db, EmbeddingService embeddings, RbAiClient ai)
{
    private const int TopK = 8;
    private const int RrfK = 60;

    // De "ruling-skill": toon en spelregels — de structuur komt per vraagtype
    // uit QuestionRouter.StructureFor (interne router, geen extra LLM-call).
    private const string BasePrompt = """
        Je bent de rulings-assistent van Riftbound Rules Companion. Je geeft
        oordelen zoals een toernooi-scheidsrechter: beslist, neutraal en
        controleerbaar. Antwoord in het Nederlands; laat Engelse speltermen
        (Deflect, showdown, exhaust, Hidden, …) onvertaald. Antwoord in
        markdown en volg exact de structuur van het opgegeven vraagtype.

        REGELS:
        - Baseer je uitsluitend op de meegegeven context-fragmenten en
          kaartgegevens. Ontbreekt het antwoord daarin: Zekerheid = Onzeker,
          en zeg wat er nodig is. Nooit gokken zonder dat label.
        - Officiële bronnen (lagere trust = betrouwbaarder) gaan vóór community.
          GEVERIFIEERDE RULINGS zijn gezaghebbend en gaan vóór alles.
        - Kaartgegevens in de context zijn gezaghebbend voor stats/mechanieken.
        - Kort is beter: geen inleiding, geen herhaling van de vraag.

        Bij een meegestuurde foto (board state): begin de Uitleg met stap 1 =
        een feitelijke beschrijving van wat je op de foto ziet (welke kaarten,
        zones, exhausted/ready) en benoem expliciet wat je NIET zeker kunt
        lezen; betrek daarna alleen zekere waarnemingen in het oordeel.
        """;

    public async Task<AskResult> AskAsync(
        string question, IReadOnlyList<RbAiClient.AiImage>? images = null,
        CancellationToken ct = default)
    {
        // 0. Interne router: vraagtype stuurt structuur en bronnen-bias.
        var mentionsCard = await MentionsCardAsync(question, ct);
        var type = QuestionRouter.Classify(question, mentionsCard);

        // 1. Vector-kanaal
        var qv = await embeddings.EmbedOneAsync(question, ct);
        var vectorHits = await db.RuleChunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(TopK * 2)
            .Select(c => new { c.Id, c.SourceId })
            .ToListAsync(ct);

        // 2. Full-text-kanaal (Engels — de bronnen zijn Engels)
        var textHits = await db.RuleChunks
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", question)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", question)))
            .Take(TopK * 2)
            .Select(c => new { c.Id, c.SourceId })
            .ToListAsync(ct);

        // 3. RRF-fusie, met bron-bias per vraagtype: toernooivragen tillen
        // Tournament Rules-chunks op, gewone rulings de Core Rules.
        var sourceBias = type switch
        {
            QuestionType.Toernooi => "tournament",
            QuestionType.Ruling or QuestionType.Definitie => "core",
            _ => null,
        };
        var scores = new Dictionary<long, double>();
        void Accumulate(IEnumerable<(long Id, string SourceId)> hits)
        {
            var rank = 0;
            foreach (var (id, sourceId) in hits)
            {
                var bonus = sourceBias != null &&
                    sourceId.Contains(sourceBias, StringComparison.OrdinalIgnoreCase) ? 0.008 : 0;
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (RrfK + rank + 1) + bonus;
                rank++;
            }
        }
        Accumulate(vectorHits.Select(h => (h.Id, h.SourceId)));
        Accumulate(textHits.Select(h => (h.Id, h.SourceId)));

        var topIds = scores.OrderByDescending(kv => kv.Value).Take(TopK).Select(kv => kv.Key).ToList();
        if (topIds.Count == 0)
            return new("Er is nog geen geïndexeerde regeltekst — draai eerst de regel-index op /admin.", [], [], type.ToString());

        var chunks = await db.RuleChunks
            .Where(c => topIds.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.Text, c.SectionCode, c.Page, c.DocumentId,
                s.Name, s.Url, s.TrustTier,
            })
            .ToListAsync(ct);
        var ordered = topIds.Select(id => chunks.First(c => c.Id == id)).ToList();

        // PDF-bestands-URL's voor deeplinks (…rules.pdf#page=N).
        var docIds = ordered.Select(c => c.DocumentId).Distinct().ToList();
        var fileUrls = await db.Documents
            .Where(d => docIds.Contains(d.Id) && d.FileUrl != null)
            .ToDictionaryAsync(d => d.Id, d => d.FileUrl, ct);

        var citations = ordered.Select((c, i) => new Citation(
            i + 1, c.Name, c.Url, c.SectionCode, c.TrustTier,
            Text: c.Text, PdfUrl: fileUrls.GetValueOrDefault(c.DocumentId), Page: c.Page)).ToList();

        var context = string.Join("\n\n", ordered.Select((c, i) =>
            $"[{i + 1}] ({c.Name}, trust {c.TrustTier}{(c.SectionCode is null ? "" : $", §{c.SectionCode}")})\n{c.Text}"));

        // 4. Kaartfeiten (incl. mechanieken en ban-status) voor herkende kaarten
        var cardBlock = await CardFactsAsync(question, ct);

        // 4b. Legaliteitsvragen krijgen de actuele banlijst als gezaghebbend blok.
        var banBlock = "";
        if (type == QuestionType.Legaliteit)
        {
            var bans = await db.BanEntries
                .OrderBy(b => b.Name)
                .Take(40)
                .Select(b => $"- {b.Name} ({b.Kind})")
                .ToListAsync(ct);
            banBlock = bans.Count == 0
                ? "\n\nBANLIJST (gezaghebbend): momenteel leeg — er zijn geen bans bekend."
                : "\n\nBANLIJST (gezaghebbend, actueel):\n" + string.Join("\n", bans);
        }

        // 5. Geverifieerde rulings (self-learning override-laag) — semantisch
        // gematcht op de vraag; zonder embedding vallen we terug op recentste.
        var rulings = await db.Corrections
            .Where(c => c.Status == "verified" && c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(3)
            .Select(c => c.Text)
            .ToListAsync(ct);
        if (rulings.Count == 0)
            rulings = await db.Corrections
                .Where(c => c.Status == "verified")
                .OrderByDescending(c => c.VerifiedAt)
                .Take(3)
                .Select(c => c.Text)
                .ToListAsync(ct);
        var rulingBlock = rulings.Count == 0
            ? ""
            : "\n\nGEVERIFIEERDE RULINGS (gezaghebbend):\n" +
              string.Join("\n", rulings.Select(r => $"- {r}"));

        // Met foto: het sterkere model — board-state-analyse vraagt echt zicht.
        var answer = await ai.AskAsync(
            $"Context-fragmenten:\n{context}{cardBlock}{banBlock}{rulingBlock}\n\nVraag: {question}",
            $"{BasePrompt}\n\n{QuestionRouter.StructureFor(type)}",
            task: images is { Count: > 0 } ? "hard" : "cheap",
            images: images, ct: ct)
            ?? "AI is niet beschikbaar — probeer het later opnieuw.";

        // Betrokken kaarten (herkend in vraag én antwoord) voor de kaart-
        // uitklap op de ruling-pagina.
        var cards = await MatchCardsAsync($"{question}\n{answer}", ct);
        return new(answer, citations, cards, type.ToString());
    }

    private async Task<bool> MentionsCardAsync(string question, CancellationToken ct)
    {
        var q = question.ToLowerInvariant();
        var names = await db.Cards
            .Where(c => c.VariantOf == null)
            .Select(c => c.Name)
            .ToListAsync(ct);
        return names.Any(n => n.Length >= 4 && q.Contains(n.ToLowerInvariant()));
    }

    private async Task<List<AskCard>> MatchCardsAsync(string text, CancellationToken ct)
    {
        var t = text.ToLowerInvariant();
        var names = await db.Cards
            .Where(c => c.VariantOf == null)
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);
        var hits = names
            .Where(c => c.Name.Length >= 4 && t.Contains(c.Name.ToLowerInvariant()))
            .Select(c => c.RiftboundId)
            .Take(6)
            .ToList();
        if (hits.Count == 0) return [];

        var cards = await db.Cards.Where(c => hits.Contains(c.RiftboundId)).ToListAsync(ct);
        var banned = await db.BanEntries
            .Where(b => b.CardRiftboundId != null && hits.Contains(b.CardRiftboundId))
            .Select(b => b.CardRiftboundId!)
            .ToListAsync(ct);
        return [.. cards.Select(c => new AskCard(
            c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains, c.Energy, c.Might,
            c.TextPlain, c.Mechanics, c.ImageUrl, banned.Contains(c.RiftboundId)))];
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
            (c.TextPlain is null ? "" :
                $"Tekst: {CardText.HumanizeIcons(c.TextPlain[..Math.Min(c.TextPlain.Length, 240)])}"));

        return "\n\nKaartgegevens (gezaghebbend voor stats/mechanieken):\n" + string.Join("\n", lines);
    }
}
