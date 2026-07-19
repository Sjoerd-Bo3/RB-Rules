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
        // Per tabel getypeerd tellen (IQueryable<T> is invariant, dus geen
        // gemeenschappelijke IEmbeddable-lijst); het filter zelf woont in de
        // gedeelde MissingNewRows/LegacyRows-builders, dus één bron van waarheid.
        var missingNew =
            await MissingNewRows(db.Cards).CountAsync(ct)
            + await MissingNewRows(db.RuleChunks).CountAsync(ct)
            + await MissingNewRows(db.Corrections).CountAsync(ct)
            + await MissingNewRows(db.Claims).CountAsync(ct)
            + await MissingNewRows(db.KnowledgeDocs).CountAsync(ct);
        var legacy =
            await LegacyRows(db.Cards).CountAsync(ct)
            + await LegacyRows(db.RuleChunks).CountAsync(ct)
            + await LegacyRows(db.Corrections).CountAsync(ct)
            + await LegacyRows(db.Claims).CountAsync(ct)
            + await LegacyRows(db.KnowledgeDocs).CountAsync(ct);
        return (missingNew, legacy);
    }

    /// <summary>De échte "provenance-incomplete embedding"-queries per tabel
    /// (dezelfde <see cref="MissingNewRows{T}"/>-builder als de audit-teller),
    /// blootgesteld zodat de EF-vertaalbaarheidstest de productie-query zelf op
    /// SQL-vertaalbaarheid controleert i.p.v. een handgeschreven kopie
    /// (#233-review).</summary>
    internal IEnumerable<IQueryable> MissingNewEmbeddingQueries() =>
    [
        MissingNewRows(db.Cards), MissingNewRows(db.RuleChunks),
        MissingNewRows(db.Corrections), MissingNewRows(db.Claims),
        MissingNewRows(db.KnowledgeDocs),
    ];

    // Rij mét content-hash (door de nieuwe pipeline geraakt) maar zónder geldig
    // model = een echte provenance-fout (gate telt mee). Filter direct op de
    // entiteit-kolommen (géén tussenrecord-projectie — die vertaalt niet naar
    // Npgsql, #233-review).
    private static IQueryable<T> MissingNewRows<T>(DbSet<T> set) where T : class, IEmbeddable =>
        set.AsNoTracking().Where(x =>
            x.Embedding != null && x.EmbeddingContentHash != null
            && (x.EmbeddingModel == null || x.EmbeddingModel != EmbeddingConfig.Model));

    // Geëmbed maar nog geen content-hash = legacy, wacht op backfill.
    private static IQueryable<T> LegacyRows<T>(DbSet<T> set) where T : class, IEmbeddable =>
        set.AsNoTracking().Where(x => x.Embedding != null && x.EmbeddingContentHash == null);

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
