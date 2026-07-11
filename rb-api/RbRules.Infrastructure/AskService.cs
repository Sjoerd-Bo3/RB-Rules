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
    string[]? Mechanics, string? ImageUrl, bool Banned,
    string? SetName, DateOnly? LegalFrom, string Legality);

public record AskTurn(string Question, string Answer);

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
        - Schrijf 'Oordeel' en 'Zekerheid' exact als losse regels in de vorm
          `**Oordeel:** …` en `**Zekerheid:** …` — géén ##-koppen daarvoor,
          geen scheidingslijnen (---). De overige kopjes wél als ### koppen.
        - Baseer je uitsluitend op de meegegeven context-fragmenten en
          kaartgegevens. Ontbreekt het antwoord daarin: Zekerheid = Onzeker,
          en zeg wat er nodig is. Nooit gokken zonder dat label.
        - Officiële bronnen (lagere trust = betrouwbaarder) gaan vóór community.
          GEVERIFIEERDE RULINGS zijn gezaghebbend en gaan vóór alles.
        - Kaartgegevens in de context zijn gezaghebbend voor stats/mechanieken.
        - Een kaartfeit met "NOG NIET LEGAAL" betekent: die kaart zit in een
          set die nog niet is verschenen. Benoem dat expliciet (setnaam +
          datum) zodra je die kaart noemt of aanbeveelt — zeker bij meta-,
          lijst- of legaliteitsvragen — en presenteer zo'n kaart nooit als nu
          speelbaar.
        - Kort is beter: geen inleiding, geen herhaling van de vraag.

        CITATEN — de site toont de meegegeven fragmenten zelf als uitklapbare
        citatielijst onder het antwoord; die lijst is de enige plek waar de
        regelsecties staan. Verwijs in de lopende tekst met [1], [2], … (de
        nummers van de fragmenten) op de plek waar je op een fragment leunt.
        Bouw NOOIT een eigen "Regelbasis"-sectie, markdown-tabel of opsomming
        die alleen §-nummers/bronnen herhaalt — dat dubbelt de lijst onderaan.

        Bij een meegestuurde foto (board state): begin de Uitleg met stap 1 =
        een feitelijke beschrijving van wat je op de foto ziet (welke kaarten,
        zones, exhausted/ready) en benoem expliciet wat je NIET zeker kunt
        lezen; betrek daarna alleen zekere waarnemingen in het oordeel.

        WIDGETS — het antwoord wordt gerenderd met interactieve blokken.
        Plaats markers op een eigen regel:
        - [[rule:466.2.c]] direct onder de stap die op die sectie leunt —
          de site toont daar een uitklapbaar regelblok met volledige tekst,
          ouderregels en PDF-link. Alleen voor §-codes uit de context.
        - [[card:Exacte Kaartnaam]] bij de eerste inhoudelijke vermelding
          van een kaart — de site toont een kaart-widget met beeld en stats.
          Alleen voor kaarten uit de kaartgegevens.
        Spaarzaam en functioneel (2 tot 5 per antwoord), nooit dezelfde
        marker twee keer; verwijzen met [n]/§ in de lopende tekst blijft
        daarnaast gewoon nodig.
        """;

    private const int MaxHistoryTurns = 3;
    private const int MaxHistoryAnswerChars = 900;

    public async Task<AskResult> AskAsync(
        string question, IReadOnlyList<RbAiClient.AiImage>? images = null,
        IReadOnlyList<AskTurn>? history = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Doorvragen (#41): eerdere rondes gaan gecapt mee; de retrieval
        // kijkt naar follow-up + eerdere vragen zodat nieuwe relevante §'s
        // bijgeladen worden.
        var turns = (history ?? []).TakeLast(MaxHistoryTurns).ToList();
        var retrievalText = turns.Count == 0
            ? question
            : string.Join("\n", turns.Select(t => t.Question).Append(question));

        // 0. Naam-match in SQL (review-fix #43: geen full-table naar de client)
        // + interne router: vraagtype stuurt structuur en bronnen-bias.
        var qLower = $"{string.Join("\n", turns.Select(t => t.Question))}\n{question}"
            .ToLowerInvariant();
        var mentionsCard = await db.Cards
            .AnyAsync(c => c.VariantOf == null && c.Name.Length >= 4 &&
                           qLower.Contains(c.Name.ToLower()), ct);
        var type = QuestionRouter.Classify(question, mentionsCard);

        // 0b. #66: query-herformulering via één goedkope LLM-call — typo's
        // corrigeren, NL→EN speltermen, zoekqueries en lexicale termen. Bij
        // doorvragen (#41) gaat de gespreks-historie mee als context.
        // Uitval of onzin-output = verwacht pad: rewrite blijft null en we
        // zoeken met de rauwe vraag(+historie), het gedrag van vóór #66.
        QueryRewrite? rewrite = null;
        var rewriteRaw = await ai.AskAsync(
            QueryRewriter.BuildPrompt(retrievalText), QueryRewriter.SystemPrompt, ct: ct);
        if (rewriteRaw is not null) rewrite = QueryRewriter.Parse(rewriteRaw);
        var searchText = rewrite?.NormalizedQuestion ?? retrievalText;

        // 1. Vector-kanaal: de genormaliseerde zoekzin plus de extra
        // zoekqueries uit de rewrite, in één Ollama-batch geëmbed. Elke query
        // is een eigen ranked list voor de RRF-fusie hieronder.
        string[] embedTexts = rewrite is null
            ? [retrievalText]
            : [searchText, .. rewrite.SearchQueries
                .Where(q => !q.Equals(searchText, StringComparison.OrdinalIgnoreCase))];
        var queryVectors = await embeddings.EmbedAsync(embedTexts, ct);
        var qv = queryVectors[0];
        var vectorLists = new List<List<(long Id, string SourceId)>>();
        foreach (var vec in queryVectors)
        {
            var hits = await db.RuleChunks
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(vec))
                .Take(TopK * 2)
                .Select(c => new { c.Id, c.SourceId })
                .ToListAsync(ct);
            vectorLists.Add([.. hits.Select(h => (h.Id, h.SourceId))]);
        }

        // 2. Full-text-kanaal (Engels — de bronnen zijn Engels; de rewrite
        // levert een Engelse zoekzin, dus die matcht hier beter dan NL).
        var textHits = await db.RuleChunks
            .Where(c => EF.Functions.ToTsVector("english", c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", searchText)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", searchText)))
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
        foreach (var list in vectorLists) Accumulate(list);
        Accumulate(textHits.Select(h => (h.Id, h.SourceId)));

        var topIds = scores.OrderByDescending(kv => kv.Value).Take(TopK).Select(kv => kv.Key).ToList();
        if (topIds.Count == 0)
        {
            sw.Stop();
            await RecordMetricAsync(sw.ElapsedMilliseconds, type, images, ok: false);
            return new("Er is nog geen geïndexeerde regeltekst — draai eerst de regel-index op /admin.",
                [], [], type.ToString(), Ok: false);
        }

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

        // 3b. Spelbegrip-primer (kennislaag 1, #49): goedgekeurde concept-docs
        // die semantisch bij de vraag passen — geeft het model de flow van het
        // spel, niet alleen losse §'s.
        var primerDocs = await db.KnowledgeDocs.AsNoTracking()
            .Where(k => k.Kind == "primer" && k.Status == "approved" && k.Embedding != null)
            .OrderBy(k => k.Embedding!.CosineDistance(qv))
            .Take(3)
            .Select(k => new { k.Title, k.Body })
            .ToListAsync(ct);
        var primerBlock = primerDocs.Count == 0
            ? ""
            : "\n\nSPELBEGRIP (achtergrond, gedistilleerd uit de officiële regels; " +
              "de regels zelf blijven normatief):\n" +
              string.Join("\n\n", primerDocs.Select(p => $"[{p.Title}]\n{p.Body}"));

        // 4. Kaartcontext — altijd semantisch (naam + mechaniek-keyword + buren),
        // zodat "wat is Deflect?" bewijs uit kaartteksten krijgt, ook als de
        // regels het keyword niet expliciet definiëren. Lijstvragen (#67)
        // krijgen daarnaast een lexicaal kanaal op kaarttekst met ruimere limiet.
        var cardContext = await CardContextAsync(question, qLower, qv, type, rewrite, ct);
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

        // Doorvraag-gesprek (#41): eerdere rondes gecapt in de prompt.
        var historyBlock = turns.Count == 0
            ? ""
            : "\n\nGESPREK TOT NU TOE (bouw hierop voort; herhaal niet wat al gezegd is):\n" +
              string.Join("\n", turns.Select(t =>
                  $"Vraag: {t.Question}\nAntwoord: {(t.Answer.Length > MaxHistoryAnswerChars ? t.Answer[..MaxHistoryAnswerChars] + "…" : t.Answer)}"));
        var questionLabel = turns.Count == 0 ? "Vraag" : "Vervolgvraag";

        // Met foto: het sterkere model — board-state-analyse vraagt echt zicht.
        var aiAnswer = await ai.AskAsync(
            $"Context-fragmenten:\n{context}{primerBlock}{cardBlock}{banBlock}{rulingBlock}{historyBlock}\n\n{questionLabel}: {question}",
            $"{BasePrompt}\n\n{QuestionRouter.StructureFor(type)}",
            task: images is { Count: > 0 } ? "hard" : "cheap",
            images: images, ct: ct);
        var answer = aiAnswer ?? RbAiClient.UnavailableAnswer;

        // Betrokken kaarten (herkend in vraag én antwoord) voor de kaart-
        // uitklap op de ruling-pagina.
        var cards = await MatchCardsAsync($"{qLower}\n{answer.ToLowerInvariant()}", ct);
        sw.Stop();

        // Duurmeting voedt de echte "gemiddeld ±Xs"-indicatie op de vraag-
        // pagina (#59: uit het endpoint — de service meet hier toch al).
        await RecordMetricAsync(sw.ElapsedMilliseconds, type, images, ok: aiAnswer is not null);

        // Denkstappen-trace voor het beheer (#40) — best-effort.
        try
        {
            db.AskTraces.Add(new AskTrace
            {
                Question = question.Length > 500 ? question[..500] : question,
                QuestionType = type.ToString(),
                RewrittenQuery = rewrite is null ? null :
                    $"{rewrite.NormalizedQuestion} | queries: " +
                    $"{string.Join("; ", rewrite.SearchQueries)} | termen: " +
                    $"{string.Join("; ", rewrite.LexicalTerms)}",
                SourceBias = sourceBias,
                MentionsCard = mentionsCard,
                MechanicMatches = string.Join(", ", cardContext.Mechanics),
                Sections = string.Join(", ", citations
                    .Where(c => c.Section != null).Select(c => $"§{c.Section}")),
                ContextCards = string.Join(", ", cardContext.CardNames),
                PrimerDocs = string.Join(", ", primerDocs.Select(p => p.Title)),
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

    /// <summary>Duurmeting per vraag — best-effort en bewust zonder request-
    /// token: een afgebroken response mag een al gegeven antwoord niet uit de
    /// statistiek houden.</summary>
    private async Task RecordMetricAsync(
        long elapsedMs, QuestionType type, IReadOnlyList<RbAiClient.AiImage>? images, bool ok)
    {
        try
        {
            db.AskMetrics.Add(new AskMetric
            {
                DurationMs = (int)Math.Min(elapsedMs, int.MaxValue),
                QuestionType = type.ToString(),
                HadImage = images is { Count: > 0 },
                Ok = ok,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // meting mag een antwoord nooit blokkeren
        }
    }

    private async Task<List<AskCard>> MatchCardsAsync(string lowerText, CancellationToken ct)
    {
        // Naam-match in SQL (#43); ban-status per variantgroep (#44);
        // zonder embedding-vectoren — de kaart-uitklap toont feiten (#43).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Name.Length >= 4 &&
                        lowerText.Contains(c.Name.ToLower()))
            .Take(6)
            .WithoutEmbedding()
            .ToListAsync(ct);
        if (cards.Count == 0) return [];

        var banned = await BanLookup.BannedCanonicalIdsAsync(db, ct);
        // Set-legaliteit (#68): de kaartwidget labelt kaarten uit een nog
        // niet verschenen set als "nog niet legaal".
        var setDates = await SetDatesAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return [.. cards.Select(c =>
        {
            var legalFrom = c.SetId is null ? null : setDates.GetValueOrDefault(c.SetId);
            return new AskCard(
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains, c.Energy, c.Might,
                c.TextPlain, c.Mechanics, c.ImageUrl, BanLookup.IsBanned(banned, c),
                c.SetLabel ?? c.SetId, legalFrom,
                SetLegality.Key(SetLegality.StatusFor(legalFrom, today)));
        })];
    }

    /// <summary>Releasedatum per set (handvol rijen) — voor de legaliteits-
    /// status in kaartfeiten en kaart-widgets (#22/#68).</summary>
    private Task<Dictionary<string, DateOnly?>> SetDatesAsync(CancellationToken ct) =>
        db.CardSets.AsNoTracking().ToDictionaryAsync(s => s.SetId, s => s.PublishedOn, ct);

    private sealed record CardContextResult(
        string Block, IReadOnlyList<string> Mechanics, IReadOnlyList<string> CardNames);

    /// <summary>Ruimere limiet voor lijst-/opsommingsvragen (#67).</summary>
    private const int ListCardLimit = 40;

    /// <summary>Kaartcontext via drie kanalen: exacte naam-matches, herkende
    /// mechaniek-keywords ("wat is Deflect?" → kaarten mét Deflect) en
    /// semantische buren van de vraag. Zo is er áltijd kaart-bewijs, ook als
    /// de regels-PDF een keyword niet expliciet definieert. Lijstvragen (#67)
    /// krijgen een vierde, lexicaal kanaal: ILIKE op kaarttekst per zoekterm
    /// uit de rewrite (#66), met ruimere limiet en een expliciete
    /// afkap-melding richting de prompt ("eerste N van M").</summary>
    private async Task<CardContextResult> CardContextAsync(
        string question, string qLower, Vector qv, QuestionType type,
        QueryRewrite? rewrite, CancellationToken ct)
    {
        var isList = type == QuestionType.Lijst;

        // 1. Exacte naam-matches, in SQL (#43).
        var nameHits = await db.Cards
            .Where(c => c.VariantOf == null && c.Name.Length >= 4 &&
                        qLower.Contains(c.Name.ToLower()))
            .Take(3)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);

        // 2. Mechaniek-keywords in de vraag (én in de rewrite: "deflekt" →
        // "Deflect") → voorbeeldkaarten + telling.
        var mechanicCorpus = rewrite is null
            ? question
            : $"{question}\n{rewrite.NormalizedQuestion}\n{string.Join('\n', rewrite.SearchQueries)}";
        var allMechanics = (await db.Cards
                .Where(c => c.Mechanics != null && c.VariantOf == null)
                .Select(c => c.Mechanics!)
                .ToListAsync(ct))
            .SelectMany(m => m)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchedMechanics = allMechanics
            .Where(m => m.Length >= 3 && Regex.IsMatch(
                mechanicCorpus, $@"\b{Regex.Escape(m)}\b", RegexOptions.IgnoreCase))
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
                .Take(isList ? ListCardLimit : 4)
                .Select(c => c.RiftboundId)
                .ToListAsync(ct);
            mechanicCardIds.AddRange(examples);
            mechanicBlocks.Add($"Mechaniek '{m}' komt voor op {count} kaarten; voorbeelden hieronder.");
        }

        // 2b. Lexicaal kanaal voor lijstvragen (#67): ILIKE op kaarttekst per
        // zoekterm uit de rewrite. Eén query per term (bewezen vertaalbaar,
        // zie CardEndpoints); rangschikking op aantal geraakte termen gebeurt
        // in-memory. Zonder rewrite is de ruimere semantiek het vangnet.
        var lexicalIds = new List<string>();
        if (isList && rewrite is { LexicalTerms.Length: > 0 })
        {
            var matchCounts = new Dictionary<string, int>();
            foreach (var term in rewrite.LexicalTerms)
            {
                var pattern = "%" + EscapeLike(term) + "%";
                var idsForTerm = await db.Cards
                    .Where(c => c.VariantOf == null && c.TextPlain != null &&
                                EF.Functions.ILike(c.TextPlain!, pattern, "\\"))
                    .Select(c => c.RiftboundId)
                    .ToListAsync(ct);
                foreach (var id in idsForTerm)
                    matchCounts[id] = matchCounts.GetValueOrDefault(id) + 1;
            }
            lexicalIds = [.. matchCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)];
        }

        // 3. Semantische buren van de vraag (altijd, als vangnet; ruimer bij
        // lijstvragen — zeker als de rewrite geen lexicale termen opleverde).
        var semanticIds = await db.Cards
            .Where(c => c.Embedding != null && c.VariantOf == null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(isList ? 12 : 4)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);

        var cap = isList ? ListCardLimit : 8;
        var merged = nameHits.Concat(lexicalIds).Concat(mechanicCardIds)
            .Concat(semanticIds).Distinct().ToList();
        var ids = merged.Take(cap).ToList();
        if (ids.Count == 0) return new("", matchedMechanics, []);

        // Afkap-melding (#67): nooit stilzwijgend inkorten — de prompt moet
        // "eerste N van M" kunnen zeggen.
        if (isList && lexicalIds.Count > 0)
        {
            var includedLexical = lexicalIds.Count(ids.Contains);
            if (includedLexical < lexicalIds.Count)
                mechanicBlocks.Add(
                    $"LET OP: de tekst-zoek vond {lexicalIds.Count} kaarten die aan de " +
                    $"zoektermen voldoen; hieronder staan er {includedLexical}. Meld in het " +
                    $"antwoord expliciet dat dit de eerste {includedLexical} van " +
                    $"{lexicalIds.Count} gevonden kaarten zijn.");
        }

        var cards = await FetchPromptCardsAsync(ids, ct);
        var banned = await BanLookup.BannedCanonicalIdsAsync(db, ct);

        // Set-legaliteit (#68): kaarten uit een nog niet verschenen set krijgen
        // een expliciet "NOG NIET LEGAAL"-feit mee als bewijs voor het model.
        var setDates = await SetDatesAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var cardsById = cards.ToDictionary(c => c.RiftboundId);
        var lines = ids
            .Where(cardsById.ContainsKey)
            .Select(id => cardsById[id])
            .Select(c =>
            {
                var fact = SetLegality.PromptFact(
                    c.SetId is null ? null : setDates.GetValueOrDefault(c.SetId),
                    today, c.SetLabel ?? c.SetId);
                return "- " + CardText.DescribeForPrompt(c, BanLookup.IsBanned(banned, c))
                     + (fact is null ? "" : $" {fact}");
            });

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

    /// <summary>Kaarten voor de prompt, geprojecteerd zónder embedding —
    /// 1024 floats × tot 40 kaarten hoort niet over de lijn (#67).</summary>
    private async Task<List<Card>> FetchPromptCardsAsync(List<string> ids, CancellationToken ct)
    {
        var rows = await db.Cards.AsNoTracking()
            .Where(c => ids.Contains(c.RiftboundId))
            .Select(c => new
            {
                c.RiftboundId, c.Name, c.Type, c.Supertype, c.Domains,
                c.Energy, c.Might, c.TextPlain, c.Mechanics, c.ImageUrl, c.VariantOf,
            })
            .ToListAsync(ct);
        return [.. rows.Select(r => new Card
        {
            RiftboundId = r.RiftboundId, Name = r.Name, Type = r.Type,
            Supertype = r.Supertype, Domains = r.Domains, Energy = r.Energy,
            Might = r.Might, TextPlain = r.TextPlain, Mechanics = r.Mechanics,
            ImageUrl = r.ImageUrl, VariantOf = r.VariantOf,
        })];
    }

    /// <summary>LIKE/ILIKE-metatekens in een zoekterm onschadelijk maken
    /// (escape-teken is backslash, zie de ILike-aanroep).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
