using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Aantallen per verwijderde categorie plus hoeveel brondocumenten
/// weer klaarstaan voor her-mining (#187).</summary>
public record KnowledgeWipeResult(
    int Claims, int Corrections, int PrimerDocs, int Relations, int DocumentsReset)
{
    public string Message =>
        $"{Claims} claims, {Corrections} correcties, {PrimerDocs} primer-docs, "
        + $"{Relations} relaties verwijderd; {DocumentsReset} document(en) gereset voor her-mining. "
        + "Bron-/feitelijke tabellen (source, document, rule_chunk, card, errata, ban_entry, "
        + "deck, deck_card) blijven onaangeraakt. Draai daarna handmatig de jobs primer, claims, "
        + "clarify en relations (in die volgorde) om de laag Engels opnieuw op te bouwen.";
}

/// <summary>Wipe-mechanisme voor de LLM-afgeleide kennislaag (#187): de
/// mining-prompts (<see cref="ClaimMiner"/>, <see cref="PrimerService"/>,
/// <see cref="RelationMiner"/>) leverden Nederlands en leveren voortaan
/// Engels (afgeleide/gesynthetiseerde kennis in de brontaal — dicht bij de
/// officiële bewoording, docs/CONVENTIONS.md). In-place vertalen zou de
/// bestaande rijen via een extra LLM-stap moeten aanpassen (dubbel werk,
/// dubbele kans op drift/hallucinatie op al opgeslagen tekst) — schoon
/// weggooien en laten her-minen met de nieuwe Engelse prompts is eenvoudiger
/// en bewezen idempotent: de mining-services gebruiken al precies dit
/// patroon (<see cref="Document.ClaimsMinedAt"/>/<see
/// cref="Document.ClarifiedAt"/>, <see cref="KnowledgeDoc.RelationsMinedAt"/>).
///
/// Scope — EXACT Sjoerds definitieve wipe-lijst (issue #187, comment
/// 2026-07-14): alles regenereerbaar of bewust opgegeven.
/// <list type="bullet">
/// <item><see cref="Claim"/> — cascadeert naar <see cref="ClaimSource"/>
/// (FK <c>OnDelete(Cascade)</c>, zie <see cref="RbRulesDbContext"/>);
/// embeddings zijn een kolom op de rij zelf, geen aparte tabel.</item>
/// <item><see cref="Correction"/> — ALLE rijen, ook de weinige door mensen
/// ingevoerde/geverifieerde (chat-ruling:admin/user, review-notitie-
/// promotie): die zijn Nederlands en zeldzaam; Sjoerd accepteert dat
/// verlies expliciet voor een schone Engelse start.</item>
/// <item><see cref="KnowledgeDoc"/> met <c>Kind == "primer"</c> — nooit
/// andere kinds, mocht die ooit bestaan.</item>
/// <item><see cref="Relation"/> — de <c>Explanation</c> is ook
/// LLM-Nederlands; volledig wippen (i.p.v. per-rij een Engelse
/// her-uitleg genereren) is de eenvoudigste correcte optie: relaties zijn
/// goedkoop te her-minen (idempotente dedupe op from/to/kind) en een tweede
/// "alleen de uitleg vertalen"-codepad zou puur voor deze eenmalige wipe
/// bestaan (YAGNI).</item>
/// </list>
///
/// NOOIT aangeraakt (bron of mensenwerk, al Engels): <see cref="Source"/>/
/// <see cref="Document"/>/<see cref="RuleChunk"/>, <see cref="Card"/>,
/// <see cref="Erratum"/>, <see cref="BanEntry"/>, <see cref="Deck"/>/
/// <see cref="DeckCard"/>. Bewezen met een test die exact deze tabellen
/// seedt en na de wipe ongewijzigd telt (<see cref="WipeAsync"/> raakt ze
/// nergens in de code). <see cref="RelationKind"/> (het kandidaat-
/// vocabulaire) blijft ook staan: dat is beheerder-gereviewde taxonomie
/// (candidate/accepted/rejected), geen mining-output op zichzelf — een
/// her-mine na de wipe hergebruikt hem gewoon (rejected kinds blijven
/// geweerd, precies zoals nu).
///
/// De reset van <see cref="Document.ClaimsMinedAt"/>/<see
/// cref="Document.ClarifiedAt"/> is geen bijzaak maar noodzakelijk: zonder
/// reset denken <see cref="ClaimMiningService"/>/<see
/// cref="ClarificationMiningService"/> dat elk brondocument al verwerkt is
/// (de marker overleeft de wipe, want <see cref="Document"/> zelf is bron,
/// geen afgeleide laag) en de afgeleide laag zou na de wipe permanent leeg
/// blijven tot een geforceerde run. <see cref="KnowledgeDoc.RelationsMinedAt"/>
/// heeft zo'n reset niet nodig: de primer-rijen zelf zijn weg, dus een
/// her-generatie (<see cref="PrimerService"/>) maakt frisse rijen met een
/// lege marker.
///
/// Uitvoering: één transactie (docs/CONVENTIONS.md: "transacties rond
/// rebuilds") — een wipe die halverwege faalt laat nooit een deel van de
/// afgeleide laag hangen zonder de rest. Bewust GEEN automatische
/// her-generatie hierna (issue-eis: expliciete admin-actie, geen verborgen
/// keten) — "Alles bijwerken" (<see cref="JobCatalog"/>'s <c>RunAllAsync</c>)
/// bevat primer/claims/clarify/relations toch al niet, dus de beheerder
/// start ze na deze wipe bewust zelf, één voor één, met eigen tempo/
/// kostenbeheersing (rb-ai-budget per nacht) — zelfde volgorde-vrijheid als
/// vandaag, alleen met een schone (Engelse) startset.</summary>
public class KnowledgeRegenerationService(RbRulesDbContext db)
{
    public const string LedgerKind = "regenerateknowledge";

    // Getrackt verwijderen (RemoveRange) i.p.v. ExecuteDeleteAsync (CONVENTIONS.
    // md's bulk-verwijder-voorkeur): dit is geen hete pad — een expliciete,
    // zeldzame admin-actie, geen nachtelijke rebuild van een grote tabel — en
    // getrackt houdt de scope-logica (o.a. het Kind == "primer"-filter) een
    // gewone LINQ-query die de InMemory-tests écht uitvoeren (ExecuteDelete/
    // ExecuteUpdate zijn op de InMemory-provider niet beschikbaar; dan zou de
    // scope-bewijs-test een aparte, niet-representatieve implementatie moeten
    // testen). In productie (Postgres) triggert het verwijderen van een Claim
    // via SaveChanges dezelfde ON DELETE CASCADE-constraint op claim_source
    // als een ExecuteDelete zou doen — geen gedragsverschil, alleen minder
    // SQL-efficiënt bij zeer grote tabellen, wat hier acceptabel is.
    public async Task<KnowledgeWipeResult> WipeAsync(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claims = await db.Claims.ToListAsync(ct);
        db.Claims.RemoveRange(claims);

        var corrections = await db.Corrections.ToListAsync(ct);
        db.Corrections.RemoveRange(corrections);

        var primerDocs = await db.KnowledgeDocs.Where(k => k.Kind == "primer").ToListAsync(ct);
        db.KnowledgeDocs.RemoveRange(primerDocs);

        var relations = await db.Relations.ToListAsync(ct);
        db.Relations.RemoveRange(relations);

        // Zelf-invaliderend patroon (#92/#93) terugdraaien: zonder deze reset
        // blijft elk brondocument voor altijd "al verwerkt" en her-mint niets.
        var docsToReset = await db.Documents
            .Where(d => d.ClaimsMinedAt != null || d.ClarifiedAt != null)
            .ToListAsync(ct);
        foreach (var d in docsToReset)
        {
            d.ClaimsMinedAt = null;
            d.ClarifiedAt = null;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var result = new KnowledgeWipeResult(
            claims.Count, corrections.Count, primerDocs.Count, relations.Count, docsToReset.Count);
        db.RunLogs.Add(new RunLog { Kind = LedgerKind, Status = "ok", Detail = result.Message });
        await db.SaveChangesAsync(ct);
        return result;
    }
}
