using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Infrastructure.GraphRag;

// ─────────────────────────────────────────────────────────────────────────────
//  Live-adapters achter de fase-4-poorten (§4). INTEGRATIE-FOLLOW-UP: Neo4j/GDS en
//  live-pgvector draaien niet in CI, dus deze adapters worden bij de eerste run met
//  flag aan geverifieerd. Alle IO degradeert netjes (lege/neutrale uitkomst) zodat
//  BreinRetrievalService.EnrichAsync nooit door een graaf/embedding-uitval een 500
//  veroorzaakt — de orchestrator draait dan gewoon op de wél-beschikbare kanalen.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Gazetteer uit Postgres (fase-1 canonicals, #225): één ingang per
/// <see cref="CanonicalEntity"/> (levend, niet-getombstoned) met zijn alias-lexicon.
/// Kaartnamen/sectienamen zijn een gedocumenteerde uitbreiding; de canonicals dekken
/// de mechanic/keyword/concept-vocabulaire waar de meeste interactie-vragen op
/// linken. Uitval → een lege gazetteer (geen mentions → geen ankers → Local/leeg).</summary>
public sealed class PostgresGazetteerSource(
    IDbContextFactory<RbRulesDbContext> dbFactory,
    ILogger<PostgresGazetteerSource> logger) : IGazetteerSource
{
    public async Task<Gazetteer> BuildAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.CanonicalEntities.AsNoTracking()
                .Where(e => e.Status != CanonicalEntityStatus.Merged)
                .Select(e => new { e.Kind, e.CanonicalLabel, e.AltLabels })
                .ToListAsync(ct).ConfigureAwait(false);

            var entries = rows.Select(r => new GazetteerEntry(
                CanonicalRef(r.Kind, r.CanonicalLabel), r.CanonicalLabel, r.AltLabels ?? []));
            return Gazetteer.Build(entries);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "gazetteer-opbouw mislukt — lege gazetteer (brein-retrieval degradeert)");
            return Gazetteer.Build([]);
        }
    }

    /// <summary>Zelfde BrainRef-mapping als <see cref="CanonicalEntity.Ref"/> (kind →
    /// Mechanic/Concept/Tag), maar zonder de hele entiteit te laden.</summary>
    private static BrainRef CanonicalRef(string kind, string label) => kind switch
    {
        CanonicalEntityKinds.Mechanic => BrainRef.Mechanic(label),
        CanonicalEntityKinds.Concept => BrainRef.Concept(label),
        _ => BrainRef.Tag(label),
    };
}

/// <summary>Context-embedding-similarity (de β·cos-as van de linker, §4): cosine van
/// de vraag-embedding tegen de canonieke-entiteit-embeddings (pgvector, bge-m3). Één
/// batch-lezing bij het bouwen van de scorer; de scorer zelf is een pure lookup.
/// Embedding-uitval (Ollama weg, #100) → een scorer die 0 teruggeeft (de cos-as
/// vervalt, de lexicale as draagt de link).</summary>
public sealed class PgVectorNodeSimilarity(
    IDbContextFactory<RbRulesDbContext> dbFactory,
    EmbeddingService embeddings,
    ILogger<PgVectorNodeSimilarity> logger) : INodeContextSimilarity
{
    public async Task<Func<BrainRef, double>> ForQuestionAsync(
        string question, CancellationToken ct = default)
    {
        try
        {
            var qv = await embeddings.EmbedOneAsync(question, ct).ConfigureAwait(false);

            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Top-N dichtstbijzijnde canonieke entiteiten; buiten die N is de cos-as 0
            // (de linker leunt dan op lexicaal/adjacency). Klein N: de scorer is een
            // her-weeg-signaal, geen retrieval-kanaal.
            var rows = await db.CanonicalEntities.AsNoTracking()
                .Where(e => e.Status != CanonicalEntityStatus.Merged && e.Embedding != null)
                .OrderBy(e => e.Embedding!.CosineDistance(qv))
                .Take(64)
                .Select(e => new { e.Kind, e.CanonicalLabel, e.Embedding })
                .ToListAsync(ct).ConfigureAwait(false);

            var byRef = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r.Embedding is null) continue;
                var refKey = CanonicalRef(r.Kind, r.CanonicalLabel).Format();
                // cosine-similarity ∈ [−1,1], geklemd op [0,1]. IN-MEMORY berekend:
                // Vector.CosineDistance is een EF-translator-stub die op een
                // gematerialiseerde rij een InvalidOperationException gooit (#228-review) —
                // de server-side .OrderBy hierboven vertaalt wél, maar hier tellen we
                // zelf op de float-spans.
                var sim = Math.Clamp(CosineSimilarity(r.Embedding, qv), 0, 1);
                byRef[refKey] = sim;
            }
            return refValue => byRef.GetValueOrDefault(refValue.Format(), 0.0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "node-similarity mislukt — cos-as vervalt (0), lexicale link draagt");
            return _ => 0.0;
        }
    }

    private static BrainRef CanonicalRef(string kind, string label) => kind switch
    {
        CanonicalEntityKinds.Mechanic => BrainRef.Mechanic(label),
        CanonicalEntityKinds.Concept => BrainRef.Concept(label),
        _ => BrainRef.Tag(label),
    };

    /// <summary>In-memory cosine-similarity tussen twee gematerialiseerde
    /// <see cref="Vector"/>s (dot / (|a|·|b|)). Bewust NIET via
    /// <c>Vector.CosineDistance</c> — dat is de EF-translator-stub die alleen in een
    /// LINQ-boom naar SQL vertaalt en op een echte rij een InvalidOperationException
    /// gooit. Ongelijke dimensies of een nul-vector → 0 (neutraal).</summary>
    internal static double CosineSimilarity(Pgvector.Vector a, Pgvector.Vector b)
    {
        var x = a.Memory.Span;
        var y = b.Memory.Span;
        if (x.Length != y.Length || x.Length == 0) return 0.0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += (double)x[i] * y[i];
            na += (double)x[i] * x[i];
            nb += (double)y[i] * y[i];
        }
        if (na == 0 || nb == 0) return 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>Co-mention-coherentie/graaf-truc (§4): zijn twee kandidaat-knopen via
/// een getypeerde edge verbonden? Één begrensde Neo4j-check die alle kandidaat-paren
/// tegelijk ophaalt; de teruggegeven functie is een pure set-lookup. Neo4j-uitval →
/// een functie die overal false teruggeeft (de coherentie-as vervalt, de link leunt
/// op lexicaal + cos + prior).</summary>
public sealed class Neo4jNodeAdjacency(
    IDriver driver, ILogger<Neo4jNodeAdjacency> logger) : INodeAdjacency
{
    public async Task<Func<BrainRef, BrainRef, bool>> ForCandidatesAsync(
        IReadOnlyList<BrainRef> candidates, CancellationToken ct = default)
    {
        var refs = candidates.Select(c => c.Format()).Distinct().ToList();
        if (refs.Count < 2) return (_, _) => false;

        try
        {
            await using var session = driver.AsyncSession();
            // Directe (1-hop) verbondenheid tussen kandidaat-refs, ongericht. Alleen
            // refs (geen labels/gebruikerstekst) gaan als parameter mee.
            var cursor = await session.RunAsync(
                """
                MATCH (a)-[]-(b)
                WHERE a.ref IN $refs AND b.ref IN $refs AND a.ref < b.ref
                RETURN DISTINCT a.ref AS aRef, b.ref AS bRef
                """,
                new Dictionary<string, object> { ["refs"] = refs.Cast<object>().ToList() });

            var connected = new HashSet<(string, string)>();
            foreach (var rec in await cursor.ToListAsync(ct))
            {
                var a = rec["aRef"].As<string?>();
                var b = rec["bRef"].As<string?>();
                if (a is not null && b is not null) connected.Add(Ordered(a, b));
            }
            return (x, y) => connected.Contains(Ordered(x.Format(), y.Format()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "adjacency-check mislukt — coherentie-as vervalt (geen graaf-truc)");
            return (_, _) => false;
        }
    }

    private static (string, string) Ordered(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
