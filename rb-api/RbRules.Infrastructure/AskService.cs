using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record Citation(
    int N, string SourceName, string Url, string? Section, int Trust,
    string? Text = null, string? PdfUrl = null, int? Page = null,
    IReadOnlyList<ParentSection>? Parents = null);

public record AskCard(
    string RiftboundId, string Name, string? Type, string? Supertype,
    string[] Domains, int? Energy, int? Might, string? TextPlain,
    string[]? Mechanics, string? ImageUrl, bool Banned);

public record AskResult(
    string Answer, IReadOnlyList<Citation> Citations,
    IReadOnlyList<AskCard> Cards, string QuestionType,
    bool Ok = true);

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 0. Kaartnamenlijst één keer per request (review-fix: werd 3× geladen)
        // + interne router: vraagtype stuurt structuur en bronnen-bias.
        var cardNames = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new CardName(c.RiftboundId, c.Name))
            .ToListAsync(ct);
        var qLower = question.ToLowerInvariant();
        var mentionsCard = cardNames.Any(n => Matches(qLower, n.Name));
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
            return new("Er is nog geen geïndexeerde regeltekst — draai eerst de regel-index op /admin.",
                [], [], type.ToString(), Ok: false);

        var chunks = await db.RuleChunks
            .Where(c => topIds.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Id, c.Text, c.SectionCode, c.Page, c.DocumentId, c.SourceId,
                s.Name, s.Url, s.TrustTier,
            })
            .ToListAsync(ct);
        // Dictionary-lookup: een chunk kan tussen de twee query's verdwenen
        // zijn (her-index) — dan overslaan i.p.v. InvalidOperationException.
        var chunksById = chunks.ToDictionary(c => c.Id);
        var ordered = topIds.Where(chunksById.ContainsKey).Select(id => chunksById[id]).ToList();

        // PDF-bestands-URL's voor deeplinks (…rules.pdf#page=N).
        var docIds = ordered.Select(c => c.DocumentId).Distinct().ToList();
        var fileUrls = await db.Documents
            .Where(d => docIds.Contains(d.Id) && d.FileUrl != null)
            .ToDictionaryAsync(d => d.Id, d => d.FileUrl, ct);

        // Ouderketen per citatie (#39): een subregel als 466.2.c is zonder
        // § 466 en § 466.2 onleesbaar.
        var parentKeys = ordered
            .Where(c => c.SectionCode != null)
            .Select(c => (c.SourceId, Code: c.SectionCode!))
            .ToList();
        var parents = await RuleParentLookup.FetchAsync(db, parentKeys, ct);

        var citations = ordered.Select((c, i) => new Citation(
            i + 1, c.Name, c.Url, c.SectionCode, c.TrustTier,
            Text: c.Text, PdfUrl: fileUrls.GetValueOrDefault(c.DocumentId), Page: c.Page,
            Parents: c.SectionCode is null
                ? null
                : parents.GetValueOrDefault((c.SourceId, c.SectionCode)))).ToList();

        var context = string.Join("\n\n", ordered.Select((c, i) =>
            $"[{i + 1}] ({c.Name}, trust {c.TrustTier}{(c.SectionCode is null ? "" : $", §{c.SectionCode}")})\n{c.Text}"));

        // 4. Kaartcontext — altijd semantisch (naam + mechaniek-keyword + buren),
        // zodat "wat is Deflect?" bewijs uit kaartteksten krijgt, ook als de
        // regels het keyword niet expliciet definiëren.
        var cardContext = await CardContextAsync(question, qLower, qv, cardNames, ct);
        var cardBlock = cardContext.Block;

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
        var aiAnswer = await ai.AskAsync(
            $"Context-fragmenten:\n{context}{cardBlock}{banBlock}{rulingBlock}\n\nVraag: {question}",
            $"{BasePrompt}\n\n{QuestionRouter.StructureFor(type)}",
            task: images is { Count: > 0 } ? "hard" : "cheap",
            images: images, ct: ct);
        var answer = aiAnswer ?? "AI is niet beschikbaar — probeer het later opnieuw.";

        // Betrokken kaarten (herkend in vraag én antwoord) voor de kaart-
        // uitklap op de ruling-pagina.
        var cards = await MatchCardsAsync($"{qLower}\n{answer.ToLowerInvariant()}", cardNames, ct);
        sw.Stop();

        // Denkstappen-trace voor het beheer (#40) — best-effort.
        try
        {
            db.AskTraces.Add(new AskTrace
            {
                Question = question.Length > 500 ? question[..500] : question,
                QuestionType = type.ToString(),
                SourceBias = sourceBias,
                MentionsCard = mentionsCard,
                MechanicMatches = string.Join(", ", cardContext.Mechanics),
                Sections = string.Join(", ", citations
                    .Where(c => c.Section != null).Select(c => $"§{c.Section}")),
                ContextCards = string.Join(", ", cardContext.CardNames),
                VerifiedRulings = rulings.Count,
                Model = images is { Count: > 0 } ? "hard" : "cheap",
                HadImage = images is { Count: > 0 },
                DurationMs = (int)sw.ElapsedMilliseconds,
                Ok = aiAnswer is not null,
            });
            // Bewaar alleen de recente historie.
            var cutoff = await db.AskTraces
                .OrderByDescending(t => t.CreatedAt)
                .Skip(200)
                .Select(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cutoff != default)
                await db.AskTraces.Where(t => t.CreatedAt <= cutoff).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // trace mag een antwoord nooit blokkeren
        }

        return new(answer, citations, cards, type.ToString(), Ok: aiAnswer is not null);
    }

    private sealed record CardName(string RiftboundId, string Name);

    /// <summary>Kaartnaam-match: substring op lowercase, minimaal 4 tekens
    /// (review-fix: één matcher voor alle drie de kanalen).</summary>
    private static bool Matches(string lowerText, string cardName) =>
        cardName.Length >= 4 && lowerText.Contains(cardName.ToLowerInvariant());

    private async Task<List<AskCard>> MatchCardsAsync(
        string lowerText, List<CardName> names, CancellationToken ct)
    {
        var hits = names
            .Where(c => Matches(lowerText, c.Name))
            .Select(c => c.RiftboundId)
            .Take(6)
            .ToList();
        if (hits.Count == 0) return [];

        var cards = await db.Cards.AsNoTracking()
            .Where(c => hits.Contains(c.RiftboundId))
            .ToListAsync(ct);
        var banned = await db.BanEntries
            .Where(b => b.CardRiftboundId != null && hits.Contains(b.CardRiftboundId))
            .Select(b => b.CardRiftboundId!)
            .ToListAsync(ct);
        return [.. cards.Select(c => new AskCard(
            c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains, c.Energy, c.Might,
            c.TextPlain, c.Mechanics, c.ImageUrl, banned.Contains(c.RiftboundId)))];
    }

    private sealed record CardContextResult(
        string Block, IReadOnlyList<string> Mechanics, IReadOnlyList<string> CardNames);

    /// <summary>Kaartcontext via drie kanalen: exacte naam-matches, herkende
    /// mechaniek-keywords ("wat is Deflect?" → kaarten mét Deflect) en
    /// semantische buren van de vraag. Zo is er áltijd kaart-bewijs, ook als
    /// de regels-PDF een keyword niet expliciet definieert.</summary>
    private async Task<CardContextResult> CardContextAsync(
        string question, string qLower, Vector qv, List<CardName> names, CancellationToken ct)
    {
        // 1. Exacte naam-matches (gezaghebbend voor de genoemde kaarten).
        var nameHits = names
            .Where(c => Matches(qLower, c.Name))
            .Take(3)
            .Select(c => c.RiftboundId)
            .ToList();

        // 2. Mechaniek-keywords in de vraag → voorbeeldkaarten + telling.
        var allMechanics = (await db.Cards
                .Where(c => c.Mechanics != null && c.VariantOf == null)
                .Select(c => c.Mechanics!)
                .ToListAsync(ct))
            .SelectMany(m => m)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchedMechanics = allMechanics
            .Where(m => m.Length >= 3 && Regex.IsMatch(
                question, $@"\b{Regex.Escape(m)}\b", RegexOptions.IgnoreCase))
            .Take(2)
            .ToList();

        var mechanicBlocks = new List<string>();
        var mechanicCardIds = new List<string>();
        foreach (var m in matchedMechanics)
        {
            var count = await db.Cards.CountAsync(
                c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(m), ct);
            var examples = await db.Cards
                .Where(c => c.VariantOf == null && c.Mechanics != null && c.Mechanics.Contains(m))
                .OrderBy(c => c.RiftboundId)
                .Take(4)
                .Select(c => c.RiftboundId)
                .ToListAsync(ct);
            mechanicCardIds.AddRange(examples);
            mechanicBlocks.Add($"Mechaniek '{m}' komt voor op {count} kaarten; voorbeelden hieronder.");
        }

        // 3. Semantische buren van de vraag (altijd, als vangnet).
        var semanticIds = await db.Cards
            .Where(c => c.Embedding != null && c.VariantOf == null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(4)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);

        var ids = nameHits.Concat(mechanicCardIds).Concat(semanticIds).Distinct().Take(8).ToList();
        if (ids.Count == 0) return new("", matchedMechanics, []);

        var cards = await db.Cards.Where(c => ids.Contains(c.RiftboundId)).ToListAsync(ct);
        var bannedIds = await db.BanEntries
            .Where(b => b.CardRiftboundId != null && ids.Contains(b.CardRiftboundId))
            .Select(b => b.CardRiftboundId!)
            .ToListAsync(ct);

        var cardsById = cards.ToDictionary(c => c.RiftboundId);
        var lines = ids
            .Where(cardsById.ContainsKey)
            .Select(id => cardsById[id])
            .Select(c =>
                $"- {c.Name} — {string.Join(" ", new[] { c.Supertype, c.Type }.Where(s => s != null))}. " +
                $"Domains: {string.Join(", ", c.Domains)}. Energy {c.Energy?.ToString() ?? "—"}, Might {c.Might?.ToString() ?? "—"}. " +
                (c.Mechanics is { Length: > 0 } m ? $"Mechanieken: {string.Join(", ", m)}. " : "") +
                (bannedIds.Contains(c.RiftboundId) ? "STAAT OP DE BANLIJST. " : "") +
                (c.TextPlain is null ? "" :
                    $"Tekst: {CardText.HumanizeIcons(c.TextPlain[..Math.Min(c.TextPlain.Length, 240)])}"));

        var header = mechanicBlocks.Count > 0
            ? string.Join("\n", mechanicBlocks) + "\n"
            : "";
        var block = "\n\nKaartgegevens (gezaghebbend voor stats/mechanieken; " +
               "kaartteksten zijn bewijs voor hoe keywords werken):\n" +
               header + string.Join("\n", lines);
        var includedNames = ids
            .Where(cardsById.ContainsKey)
            .Select(id => cardsById[id].Name)
            .ToList();
        return new(block, matchedMechanics, includedNames);
    }
}
