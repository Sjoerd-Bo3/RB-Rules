using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

public record InteractionMineResult(int Candidates, int Verified);
public record ResolveResult(string Answer, IReadOnlyList<Citation> Citations);

/// <summary>S3: interactie-mining (kandidaten → LLM-verificatie → opslag +
/// graph-edges), het kaart-buren-leespad en de resolver ("hoe werkt kaart A
/// tegen kaart B?").
///
/// Status na #258: <see cref="MineAsync"/> is LEGACY — paar-lexicaal en
/// conditie-loos, inhoudelijk opgevolgd door BreinInteractionMiningService. Hij
/// staat in geen enkele keten meer (alleen nog handmatig startbaar), zodat de
/// nachtrun zijn LLM-budget niet meer aan de voorganger besteedt. Het leespad
/// (<see cref="NeighborsAsync"/>) leest sindsdien primair de gereïficeerde laag
/// en valt op de oude tabel terug zolang de opvolger nog niet genoeg dekking
/// heeft — zie daar voor de meting en het weg-criterium van die brug.
///
/// <see cref="ResolveAsync"/> heeft nooit interactie-rijen gelezen (het bouwt
/// zijn antwoord uit kaartteksten, errata en regelsecties); /api/resolve raakt
/// dus geen van beide lagen en had aan deze migratie niets te doen.</summary>
public class InteractionService(
    RbRulesDbContext db, RbAiClient ai, EmbeddingService embeddings, IDriver driver,
    RequestUserContext? userContext = null)
{
    private const int VerifyBatch = 6;

    public async Task<InteractionMineResult> MineAsync(
        int maxCandidates = 60, Action<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Invoke("kandidaat-paren zoeken op gedeelde mechanieken");
        // Canoniek + projectie zonder embedding-vectoren (#43/#57).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.Mechanics != null && c.VariantOf == null)
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, Type = c.Type,
                Supertype = c.Supertype, Domains = c.Domains,
                Mechanics = c.Mechanics, Triggers = c.Triggers, Effects = c.Effects,
                Energy = c.Energy, Might = c.Might, TextPlain = c.TextPlain,
            })
            .ToListAsync(ct);

        // Al beoordeelde paren niet opnieuw (geverifieerd of afgewezen doen we
        // simpel: alles wat al in de tabel staat slaan we over).
        var known = await db.CardInteractions
            .Select(x => new { x.CardAId, x.CardBId })
            .ToListAsync(ct);
        var knownSet = known
            .Select(k => (k.CardAId, k.CardBId))
            .ToHashSet();

        var candidates = InteractionMiner.FindCandidates(cards, maxCandidates * 3)
            .Where(c => !knownSet.Contains(
                CardText.OrderedPair(c.A.RiftboundId, c.B.RiftboundId)))
            .Take(maxCandidates)
            .ToList();

        var verified = 0;
        var judged = 0;
        foreach (var batch in candidates.Chunk(VerifyBatch))
        {
            judged += batch.Length;
            progress?.Invoke($"paren beoordelen via LLM: {judged}/{candidates.Count} ({verified} geverifieerd)");
            var raw = await ai.AskAsync(
                InteractionMiner.BuildVerifyPrompt(batch),
                InteractionMiner.VerifySystemPrompt, ct: ct);
            if (raw is null) continue;

            foreach (var v in InteractionMiner.ParseVerified(raw))
            {
                var (a, b) = CardText.OrderedPair(v.AId, v.BId);
                db.CardInteractions.Add(new CardInteraction
                {
                    CardAId = a, CardBId = b, Kind = v.Kind, Explanation = v.Explanation,
                });
                verified++;
            }
            await db.SaveChangesAsync(ct);
        }

        // Graph-edges (best-effort)
        if (verified > 0)
        {
            try
            {
                var rows = await db.CardInteractions.AsNoTracking().ToListAsync(ct);
                var pairs = rows.Select(x => (object)new Dictionary<string, object?>
                {
                    ["a"] = x.CardAId, ["b"] = x.CardBId,
                    ["kind"] = x.Kind, ["explanation"] = x.Explanation,
                }).ToList();
                await using var session = driver.AsyncSession();
                // Edge-naam uit het register (#289), niet uit een literal: dit is
                // dezelfde tweespalt-klasse die #274 voor HAS_MECHANIC/HAS_DOMAIN
                // opruimde. Interpolatie in Cypher is veilig — de waarde komt uit het
                // compile-time schema-register, nooit uit invoer.
                var interactsWith = OntologySchema.Relations[RelationType.InteractsWith].EdgeName;
                await session.RunAsync(
                    $$"""
                    UNWIND $pairs AS p
                    MATCH (a:Card {id: p.a}), (b:Card {id: p.b})
                    MERGE (a)-[r:{{interactsWith}}]->(b)
                      SET r.kind = p.kind, r.explanation = p.explanation, r.verified = true
                    """,
                    new Dictionary<string, object> { ["pairs"] = pairs });
            }
            catch
            {
                // Neo4j-uitval mag mining niet breken; Postgres is leidend.
            }
        }

        return new(candidates.Count, verified);
    }

    /// <summary>Geverifieerde interacties van een kaart, variantgroep-bewust
    /// (#57, #59 — uit de endpoints): match op alle printing-ids van de groep
    /// (rijen van vóór de groepering kunnen nog variant-ids bevatten) en
    /// canonicaliseer de buren. Null als de kaart niet bestaat.</summary>
    public async Task<List<InteractionNeighbor>?> NeighborsForCardAsync(
        string cardId, int take, CancellationToken ct = default)
    {
        // Alleen het groeps-id is nodig — niet de hele rij met embedding (#43).
        var card = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == cardId)
            .Select(c => new { c.RiftboundId, c.VariantOf })
            .FirstOrDefaultAsync(ct);
        if (card is null) return null;
        return await NeighborsAsync(card.VariantOf ?? card.RiftboundId, take, ct);
    }

    /// <summary>Als <see cref="NeighborsForCardAsync"/>, maar op een al
    /// gecanonicaliseerd groeps-id.
    ///
    /// MIGRATIEBRUG (#258) — leest BEIDE interactielagen en voegt ze samen:
    /// eerst de gereïficeerde <see cref="Interaction"/>-laag (#226, de opvolger),
    /// daarna de oude <see cref="CardInteraction"/>-laag als aanvulling. De
    /// gereïficeerde rij wint bij een dubbele buur (hij draagt rol en condities;
    /// de oude alleen vrije proza).
    ///
    /// Waarom niet gewoon omschakelen? Gemeten op productie (2026-07): de oude
    /// tabel heeft 103 rijen over 94 kaarten, de gereïficeerde 8 rijen over 5
    /// kaarten — waarvan NUL gepromoveerde kaart↔kaart-paren. Hard omschakelen
    /// zou het kaartdetail van 94 kaarten naar 0 zichtbare interacties brengen,
    /// met groene tests. Niet omdat de opvolger slechter is, maar omdat hij nog
    /// nauwelijks gedraaid heeft: 18 van de 1311 kaarten hebben een
    /// interactions_mined_at (1,4%), doordat het gros van de extracties op een
    /// 5xx van rb-ai strandt (#281).
    ///
    /// WEG-CRITERIUM voor deze brug — bewust concreet, zodat hij niet "tijdelijk"
    /// blijft: zodra (a) #281 opgelost is, (b) een volle nachtelijke mining-run
    /// de kaartendekking boven ~80% heeft gebracht, en (c) het aantal
    /// gepromoveerde kaart↔kaart-interacties dat van de oude tabel evenaart,
    /// vervalt de legacy-tak hieronder, samen met de job "interactions",
    /// <see cref="MineAsync"/> en uiteindelijk de tabel zelf. Tot die tijd is de
    /// oude tabel BEVROREN (geen enkele keten schrijft er nog in) maar wél
    /// leidend voor wat de bezoeker ziet.</summary>
    public async Task<List<InteractionNeighbor>> NeighborsAsync(
        string canonicalId, int take, CancellationToken ct = default)
    {
        var groupIds = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == canonicalId || c.VariantOf == canonicalId)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);
        if (groupIds.Count == 0) groupIds = [canonicalId];

        // Gereïficeerde laag: alleen kaart↔kaart en alleen wat de promotiepoort
        // heeft goedgekeurd. De ref-vorm is "card:{id}" (BrainRef), dus de
        // groeps-ids worden naar refs vertaald vóór de query — StartsWith en
        // List.Contains vertalen allebei naar SQL (geen client-eval).
        //
        // Het kaart↔kaart-filter hoort HIER en niet stroomafwaarts in de projectie
        // (#287-review): card↔mechanic is een bedoelde uitvoervorm van de mining
        // (BreinInteractionMiningService biedt bewust partner-keywords aan), dus
        // filteren ná de Take laat die rijen het budget opeten en gooit elke echte
        // kaart-buur voorbij de afkap STIL weg. Regressietest:
        // Leespad_VerlietstGeenKaartBuurAchterEenBergKaartKeywordRijen.
        var groupRefs = groupIds.Select(id => BrainRef.Card(id).Format()).ToList();
        var displayable = ReifiedInteractionDisplay.DisplayableStatuses;
        var cardPrefix = ReifiedInteractionDisplay.CardRefPrefix;
        var reifiedRows = await db.Interactions.AsNoTracking()
            .Include(x => x.Conditions)
            .Where(x => displayable.Contains(x.Status)
                && x.AgentRef.StartsWith(cardPrefix)
                && x.PatientRef.StartsWith(cardPrefix)
                && (groupRefs.Contains(x.AgentRef) || groupRefs.Contains(x.PatientRef)))
            .OrderByDescending(x => x.Status == InteractionStatus.Promoted)
            .ThenBy(x => x.Kind)
            .Take(take)
            .ToListAsync(ct);

        var legacyRows = await db.CardInteractions.AsNoTracking()
            .Where(x => groupIds.Contains(x.CardAId) || groupIds.Contains(x.CardBId))
            .OrderBy(x => x.Kind)
            .Take(take)
            .ToListAsync(ct);

        var groupSet = groupIds.ToHashSet();
        // Kaartnamen voor beide lagen in één query (projectie zonder
        // embedding-vectoren, #43).
        var otherIds = legacyRows
            .Select(r => groupSet.Contains(r.CardAId) ? r.CardBId : r.CardAId)
            .Concat(reifiedRows
                .SelectMany(r => new[] { r.AgentRef, r.PatientRef })
                .Where(ReifiedInteractionDisplay.IsCardRef)
                .Select(ReifiedInteractionDisplay.CardIdOf))
            .Distinct()
            .ToList();
        var others = await db.Cards.AsNoTracking()
            .Where(c => otherIds.Contains(c.RiftboundId))
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf,
            })
            .ToDictionaryAsync(c => c.RiftboundId, c => c, ct);

        var neighbors = ReifiedInteractionDisplay.Neighbors(reifiedRows, groupSet, others);
        // Legacy-aanvulling: buren die de gereïficeerde laag nog niet kent.
        var covered = neighbors.Select(n => n.OtherId).ToHashSet();
        neighbors.AddRange(VariantGrouping
            .InteractionNeighbors(legacyRows, groupSet, others)
            .Where(n => !covered.Contains(n.OtherId)));

        return neighbors.Count > take ? neighbors[..take] : neighbors;
    }

    /// <summary>Resolver: 2-3 kaartnamen → gecombineerd antwoord (effectieve
    /// teksten + mechanieken + relevante regelsecties met §-citaten).</summary>
    public async Task<ResolveResult?> ResolveAsync(string[] cardIds, CancellationToken ct = default)
    {
        // Prompt-invoer, geen updates: zonder tracking en zonder de
        // embedding-vectoren die DescribeForPrompt toch niet gebruikt (#43).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => cardIds.Contains(c.RiftboundId))
            .WithoutEmbedding()
            .ToListAsync(ct);
        if (cards.Count < 2) return null;

        var errata = await db.Errata
            .Where(e => cardIds.Contains(e.CardRiftboundId!))
            .ToListAsync(ct);

        // Uniforme prompt-beschrijving (#44) — post-errata-tekst is leidend en
        // icon-tokens gaan gehumaniseerd naar het model.
        var cardBlock = string.Join("\n", cards.Select(c =>
            "- " + CardText.DescribeForPrompt(c,
                effectiveText: errata.FirstOrDefault(e => e.CardRiftboundId == c.RiftboundId)?.NewText
                               ?? c.TextPlain)));

        // Relevante regelsecties: zoek op de gecombineerde mechanieken + teksten.
        var searchText = string.Join(" ", cards.SelectMany(c => c.Mechanics ?? [])
            .Concat(cards.Select(c => c.TextPlain ?? "")));
        // Stond hier tot #301 als `searchText[..Math.Min(searchText.Length, 1500)]` —
        // een handmatige snee met een getal dat nergens anders voorkomt en niets met
        // de gemeten Ollama-grens te maken had. Precies de ad-hoc oplossing die #301
        // opheft: één aanroepplek beschermd, de andere elf niet. De begrenzing zit nu
        // in EmbeddingService en geldt voor iedereen, dus dit mag gewoon de volledige
        // zoektekst zijn.
        var qv = await embeddings.EmbedOneAsync(searchText, ct);
        var chunks = await db.RuleChunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(qv))
            .Take(6)
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.Text, c.SectionCode, s.Name, s.Url, s.TrustTier,
            })
            .ToListAsync(ct);

        var citations = chunks.Select((c, i) =>
            new Citation(i + 1, c.Name, c.Url, c.SectionCode, c.TrustTier)).ToList();
        var context = string.Join("\n\n", chunks.Select((c, i) =>
            $"[{i + 1}] ({c.Name}{(c.SectionCode is null ? "" : $", §{c.SectionCode}")})\n{c.Text}"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await ai.AskWithUsageAsync(
            $"Kaarten:\n{cardBlock}\n\nRelevante regelsecties:\n{context}\n\n" +
            "Vraag: hoe werken deze kaarten op elkaar in? Loop de interactie " +
            "stap voor stap door en citeer per stap de relevante regelsectie met [n]/§.",
            """
            Je bent een Riftbound TCG judge. Leg kaart-interacties stap voor stap
            uit met [n]-citaten naar de meegegeven regelsecties (noem §-nummers).
            Gebruik de effectieve (post-errata) kaartteksten. Als de regels geen
            uitsluitsel geven, zeg dat eerlijk. Antwoord in het Nederlands.
            """,
            task: "hard", ct: ct);
        sw.Stop();

        // Kosten-grootboek (#328, review): /api/resolve is een gebruikers-
        // veroorzaakte, ongecachte hard-model-call en hoort dus geboekt —
        // mét user-attributie (sinds de login-poort is er altijd een account).
        // Best-effort: de boekhouding mag het antwoord nooit blokkeren.
        try
        {
            db.AiUsageEvents.Add(await AiUsageMeter.CreateEventAsync(
                db, AiUsageEvent.OriginUser, "resolve", AskPathModels.Resolve("hard"),
                userContext?.User?.Id, res?.Usage?.InputTokens, res?.Usage?.OutputTokens,
                (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue), ok: res?.Answer != null, ct));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var entry in db.ChangeTracker.Entries<AiUsageEvent>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }

        return new(res?.Answer ?? RbAiClient.UnavailableAnswer, citations);
    }
}
