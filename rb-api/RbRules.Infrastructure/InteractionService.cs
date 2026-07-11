using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record InteractionMineResult(int Candidates, int Verified);
public record ResolveResult(string Answer, IReadOnlyList<Citation> Citations);

/// <summary>S3: interactie-mining (kandidaten → LLM-verificatie → opslag +
/// graph-edges) en de resolver ("hoe werkt kaart A tegen kaart B?").</summary>
public class InteractionService(
    RbRulesDbContext db, RbAiClient ai, EmbeddingService embeddings, IDriver driver)
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
                await session.RunAsync(
                    """
                    UNWIND $pairs AS p
                    MATCH (a:Card {id: p.a}), (b:Card {id: p.b})
                    MERGE (a)-[r:INTERACTS_WITH]->(b)
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
    /// gecanonicaliseerd groeps-id.</summary>
    public async Task<List<InteractionNeighbor>> NeighborsAsync(
        string canonicalId, int take, CancellationToken ct = default)
    {
        var groupIds = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == canonicalId || c.VariantOf == canonicalId)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);
        if (groupIds.Count == 0) groupIds = [canonicalId];

        var rows = await db.CardInteractions.AsNoTracking()
            .Where(x => groupIds.Contains(x.CardAId) || groupIds.Contains(x.CardBId))
            .OrderBy(x => x.Kind)
            .Take(take)
            .ToListAsync(ct);

        var groupSet = groupIds.ToHashSet();
        var otherIds = rows
            .Select(r => groupSet.Contains(r.CardAId) ? r.CardBId : r.CardAId)
            .ToList();
        // Projectie zonder embedding-vectoren (#43).
        var others = await db.Cards.AsNoTracking()
            .Where(c => otherIds.Contains(c.RiftboundId))
            .Select(c => new Card
            {
                RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf,
            })
            .ToDictionaryAsync(c => c.RiftboundId, c => c, ct);

        return VariantGrouping.InteractionNeighbors(rows, groupSet, others);
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
        var qv = await embeddings.EmbedOneAsync(searchText[..Math.Min(searchText.Length, 1500)], ct);
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

        var answer = await ai.AskAsync(
            $"Kaarten:\n{cardBlock}\n\nRelevante regelsecties:\n{context}\n\n" +
            "Vraag: hoe werken deze kaarten op elkaar in? Loop de interactie " +
            "stap voor stap door en citeer per stap de relevante regelsectie met [n]/§.",
            """
            Je bent een Riftbound TCG judge. Leg kaart-interacties stap voor stap
            uit met [n]-citaten naar de meegegeven regelsecties (noem §-nummers).
            Gebruik de effectieve (post-errata) kaartteksten. Als de regels geen
            uitsluitsel geven, zeg dat eerlijk. Antwoord in het Nederlands.
            """,
            task: "hard", ct: ct)
            ?? "AI is niet beschikbaar — probeer het later opnieuw.";

        return new(answer, citations);
    }
}
