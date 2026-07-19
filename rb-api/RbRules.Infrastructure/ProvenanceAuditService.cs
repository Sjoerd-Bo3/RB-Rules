using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Ring-A-provenance-audit (fase 0a, #233): de deterministische, €0,
/// LLM-loze check die faalmodus #4 (ontbrekende provenance) tot een harde gate
/// maakt. Telt afgeleide feiten zonder <see cref="Assertion"/> en embedding-rijen
/// zonder complete herkomst, gesplitst in "nieuw" (ná de fase-0a-cutoff — moet 0
/// zijn, dát is de gate) en "legacy" (vóór de cutoff — geïnventariseerd voor
/// backfill, blokkeert de gate niet). De queries zijn bewust EF-vertaalbaar
/// (conventie; ToQueryString-net in ProvenanceAuditQueryTests).</summary>
public class ProvenanceAuditService(RbRulesDbContext db)
{
    /// <param name="newFactCutoff">Grens tussen legacy en nieuw: feiten met een
    /// detectie-tijd op/na deze waarde moeten volledige provenance hebben.</param>
    public async Task<ProvenanceAudit.Report> AuditAsync(
        DateTimeOffset newFactCutoff, CancellationToken ct = default)
    {
        // Afgeleide feiten zonder Assertion (subject-match op de BrainRef-string).
        // Correlated NOT EXISTS — standaard EF, vertaalt naar een subquery.
        var relationsNew = await db.Relations.AsNoTracking()
            .Where(r => r.DetectedAt >= newFactCutoff)
            .Where(r => !db.Assertions.Any(a =>
                a.FactKind == FactKinds.Relation && a.Subject == RelationSubjectPrefix + r.Id))
            .CountAsync(ct);
        var relationsLegacy = await db.Relations.AsNoTracking()
            .Where(r => r.DetectedAt < newFactCutoff)
            .Where(r => !db.Assertions.Any(a =>
                a.FactKind == FactKinds.Relation && a.Subject == RelationSubjectPrefix + r.Id))
            .CountAsync(ct);

        var interactionsNew = await db.CardInteractions.AsNoTracking()
            .Where(i => i.DetectedAt >= newFactCutoff)
            .Where(i => !db.Assertions.Any(a =>
                a.FactKind == FactKinds.CardInteraction && a.Subject == InteractionSubjectPrefix + i.Id))
            .CountAsync(ct);
        var interactionsLegacy = await db.CardInteractions.AsNoTracking()
            .Where(i => i.DetectedAt < newFactCutoff)
            .Where(i => !db.Assertions.Any(a =>
                a.FactKind == FactKinds.CardInteraction && a.Subject == InteractionSubjectPrefix + i.Id))
            .CountAsync(ct);

        // Embeddings: een rij die al een content-hash draagt (dus door de nieuwe
        // pipeline geraakt) MOET een geldig model dragen — de dim is structureel
        // 1024 (getypte vector-kolom), dus die kan niet mismatchen. Legacy-rijen
        // (embedding gezet, hash nog null) tellen als backlog, niet als gate-fout.
        var (embMissingNew, embLegacy) = await AuditEmbeddingsAsync(ct);

        return new ProvenanceAudit.Report(
            FactsMissingAssertion: relationsNew + interactionsNew,
            EmbeddingsMissingProvenance: embMissingNew,
            LegacyBacklog: relationsLegacy + interactionsLegacy + embLegacy);
    }

    private async Task<(int MissingNew, int Legacy)> AuditEmbeddingsAsync(CancellationToken ct)
    {
        var missingNew = 0;
        var legacy = 0;

        // Elke IQueryable is deferred, dus tweemaal met een andere Where
        // hergebruiken levert gewoon twee losse COUNT-queries op.
        foreach (var q in EmbeddingSources())
        {
            // Rij mét content-hash maar zónder geldig model = een echte
            // provenance-fout in de nieuwe pipeline (gate telt mee).
            missingNew += await q
                .Where(x => x.Hash != null && (x.Model == null || x.Model != EmbeddingConfig.Model))
                .CountAsync(ct);
            // Geëmbed maar nog geen content-hash = legacy, wacht op backfill.
            legacy += await q
                .Where(x => x.Hash == null)
                .CountAsync(ct);
        }
        return (missingNew, legacy);
    }

    // Elke embedding-dragende tabel als uniforme (embedded, model, hash)-projectie.
    private IEnumerable<IQueryable<EmbeddingRow>> EmbeddingSources() =>
    [
        db.Cards.AsNoTracking().Where(c => c.Embedding != null)
            .Select(c => new EmbeddingRow(c.EmbeddingModel, c.EmbeddingContentHash)),
        db.RuleChunks.AsNoTracking().Where(c => c.Embedding != null)
            .Select(c => new EmbeddingRow(c.EmbeddingModel, c.EmbeddingContentHash)),
        db.Corrections.AsNoTracking().Where(c => c.Embedding != null)
            .Select(c => new EmbeddingRow(c.EmbeddingModel, c.EmbeddingContentHash)),
        db.Claims.AsNoTracking().Where(c => c.Embedding != null)
            .Select(c => new EmbeddingRow(c.EmbeddingModel, c.EmbeddingContentHash)),
        db.KnowledgeDocs.AsNoTracking().Where(c => c.Embedding != null)
            .Select(c => new EmbeddingRow(c.EmbeddingModel, c.EmbeddingContentHash)),
    ];

    private sealed record EmbeddingRow(string? Model, string? Hash);

    // Subject-prefixen (BrainRef.Format-vorm) — de sleutel waarmee een Assertion
    // aan zijn feit hangt. Als string-constante zodat de EF-vertaling een simpele
    // concat wordt.
    public const string RelationSubjectPrefix = "relation:";
    public const string InteractionSubjectPrefix = "card_interaction:";
}

/// <summary>Canonieke <see cref="Assertion.FactKind"/>-waarden (fase 0a, #233):
/// het discriminator-vocabulaire dat de audit-teller aan de feit-tabellen koppelt.
/// Eén bron zodat schrijver en audit nooit uiteenlopen.</summary>
public static class FactKinds
{
    public const string Relation = "relation";
    public const string CardInteraction = "card_interaction";
    public const string Mechanic = "mechanic";
    public const string Embedding = "embedding";
}
