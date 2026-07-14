using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public enum ReevaluateOutcome { Verified, StillPending, NotFound }

public record ReevaluateResult(ReevaluateOutcome Outcome, string? Reason);

/// <summary>Her-evaluatie van één Correction (#184), op initiatief van de
/// beheerder vanuit de reviewqueue: een opmerking wordt bewaard
/// (<see cref="Correction.ReviewNote"/>, traceerbaar) en triggert meteen een
/// deterministische her-toets van de hybride autoriteitspoort (#177/#185) voor
/// dít ene item — ROEPT de bestaande domeinlogica aan
/// (<see cref="ClarificationGrounding.IsGrounded"/>,
/// <see cref="ClaimTopicMapper.Resolve"/>,
/// <see cref="ClarificationInformativeness.IsMetaOnly"/>), wijzigt die logica
/// zelf niet (#188-terrein, met name IsMetaOnly wordt daar vervangen).
///
/// Een opmerking mag een anker-correctie bevatten
/// (<see cref="ReviewNoteAnchor"/>, bv. "mechanic:Recall") die het
/// oorspronkelijke Scope/Ref overschrijft — zo kan een fout-aangeankerd of
/// onherkend onderwerp alsnog verified worden zonder de LLM-extractie
/// opnieuw te draaien. Grounding en informativiteit toetsen altijd de
/// bestaande Text (het citaat/de verduidelijking veranderen niet mee met een
/// anker-correctie; alleen het onderwerp doet dat).
///
/// Alleen van toepassing op clarify-mining-Corrections (Provenance
/// "<see cref="ClarificationMiningService.ProvenancePrefix"/>{sourceId}"):
/// dat is de enige ontstaanswijze met een op-zichzelf-staande, gate-gehouden
/// "unverified" status én een brontekst om tegen te gronden — een
/// chat-ruling (<see cref="ChatRulingService"/>) of review-notitie-promotie
/// (<see cref="ReviewNoteService"/>) wordt altijd direct verified aangemaakt.
/// Voor een niet-clarify-mining Correction bewaart her-evaluatie alleen de
/// opmerking, zonder de poort te draaien (er is geen brontekst om tegen te
/// gronden). Een afgewezen (rejected) Correction blijft een tombstone
/// (#177-conventie): de opmerking wordt bewaard, maar her-evalueert niet
/// stiekem een menselijke afwijzing — expliciet opnieuw verifiëren kan nog
/// altijd via het bestaande /verify-pad. Een al geverifieerde Correction
/// degradeert nooit (zelfde no-demote-invariant als
/// <see cref="ClarificationMiningService"/>.StoreAsync): her-evaluatie is
/// alleen zinvol op pending items, dus daar bewaart de actie alleen de
/// opmerking.</summary>
public class CorrectionReevaluationService(RbRulesDbContext db)
{
    public async Task<ReevaluateResult> ReevaluateAsync(
        long id, string? note, CancellationToken ct = default)
    {
        var c = await db.Corrections.FindAsync([id], ct);
        if (c is null) return new(ReevaluateOutcome.NotFound, null);

        var cleanNote = Clean(note);
        if (cleanNote is not null) c.ReviewNote = cleanNote;

        if (c.Status == "rejected")
        {
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.StillPending,
                "afgewezen — opmerking bewaard, geen her-evaluatie op een afgewezen item");
        }

        if (c.Status == "verified")
        {
            // Nooit degraderen (zelfde invariant als ClarificationMiningService.
            // StoreAsync): een al geverifieerde ruling blijft verified — her-
            // evaluatie is hier alleen relevant voor pending items, de
            // opmerking wordt wel bewaard.
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.Verified, null);
        }

        if (c.Provenance is null || !c.Provenance.StartsWith(ClarificationMiningService.ProvenancePrefix))
        {
            // Geen clarify-mining-oorsprong: geen brontekst om de hybride
            // poort tegen te draaien. De opmerking is bewaard; status blijft
            // ongemoeid (handmatig/chat-ruling-correcties zijn al verified).
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.StillPending,
                "geen clarify-mining-oorsprong — alleen opmerking bewaard, poort niet van toepassing");
        }

        var anchorOverride = ReviewNoteAnchor.TryParse(c.ReviewNote);
        var anchors = await AnchorResolverFactory.BuildAsync(db, ct);

        var topicType = anchorOverride?.TopicType ?? TopicTypeFor(c.Scope);
        var topicRef = anchorOverride?.TopicRef ?? c.Ref;
        var anchored = anchors.Resolve(topicType, topicRef) is not null;

        var docContent = await LoadDocumentContentAsync(c.Provenance, ct);
        var quote = ClarificationMiningService.ExtractQuote(c.Text);
        var grounded = ClarificationGrounding.IsGrounded(quote, docContent);
        var informative = !ClarificationInformativeness.IsMetaOnly(
            ClarificationMiningService.ClarificationOf(c.Text));
        var verifies = grounded && anchored && informative;

        // Anker-correctie toepassen zodra hij ook echt resolvet — een niet-
        // herkend voorgesteld anker verandert niets (blijft het oorspronkelijke
        // onderwerp, "anchored" is dan al false en de reden zegt waarom).
        if (anchorOverride is not null && anchored)
        {
            c.Scope = ClarificationMiningService.ScopeFor(anchorOverride.Value.TopicType);
            c.Ref = anchorOverride.Value.TopicRef;
        }

        if (verifies)
        {
            c.Status = "verified";
            c.StatusReason = null;
            c.VerifiedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.Verified, null);
        }

        c.Status = "unverified";
        c.StatusReason = ClarificationMiningService.GateReason(
            grounded, anchored, informative, quote, topicType, topicRef);
        c.VerifiedAt = null;
        await db.SaveChangesAsync(ct);
        return new(ReevaluateOutcome.StillPending, c.StatusReason);
    }

    private async Task<string?> LoadDocumentContentAsync(string provenance, CancellationToken ct)
    {
        var sourceId = provenance[ClarificationMiningService.ProvenancePrefix.Length..];
        return await db.Documents.AsNoTracking()
            .Where(d => d.SourceId == sourceId)
            .OrderByDescending(d => d.RetrievedAt)
            .Select(d => d.Content)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Correction.Scope → ClaimTopicMapper-topicType, het omgekeerde
    /// van ClarificationMiningService.ScopeFor.</summary>
    private static string TopicTypeFor(string scope) => scope switch
    {
        "card" => "card",
        "mechanic" => "mechanic",
        "rule_section" => "section",
        _ => "concept",
    };

    /// <summary>Notities zijn tekst of niets: witruimte telt niet als notitie
    /// (zelfde regel als ReviewNoteService.Clean).</summary>
    private static string? Clean(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
