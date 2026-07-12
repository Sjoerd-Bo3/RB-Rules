using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public enum PromoteNoteStatus { Promoted, NotFound, NoNote }

public record PromoteNoteResult(
    PromoteNoteStatus Status, long CorrectionId = 0, bool Embedded = false, bool Updated = false);

/// <summary>Beheerder-notities op reviewqueue-items doorzetten als
/// geverifieerde ruling (#124): de notitie ("zo zit het wél") wordt een
/// Correction met scope claim/relation en ref = BrainRef van het onderwerp,
/// direct verified en geembed via hetzelfde pad als corrections/verify —
/// zodat de uitleg van de beheerder voortaan antwoorden stuurt in plaats van
/// in een kladblok te sterven. Idempotent per onderwerp: nogmaals promoveren
/// werkt de bestaande ruling bij (één ruling per claim/relatie).</summary>
public class ReviewNoteService(RbRulesDbContext db, EmbeddingService embeddings)
{
    public async Task<PromoteNoteResult> PromoteClaimNoteAsync(
        long id, string? note = null, CancellationToken ct = default)
    {
        var claim = await db.Claims.FindAsync([id], ct);
        if (claim is null) return new(PromoteNoteStatus.NotFound);
        var text = Clean(note) ?? Clean(claim.ReviewNote);
        if (text is null) return new(PromoteNoteStatus.NoNote);

        claim.ReviewNote = text;
        // Question = de bewering zelf: de embedding (vraag+correctie, het
        // verify-pad) matcht dan vragen over hetzelfde onderwerp als de claim.
        return await UpsertRulingAsync(
            scope: "claim",
            subject: BrainRef.Claim(claim.Id).Format(),
            question: $"{claim.TopicRef}: {claim.Statement}",
            text, ct);
    }

    public async Task<PromoteNoteResult> PromoteRelationNoteAsync(
        long id, string? note = null, CancellationToken ct = default)
    {
        var relation = await db.Relations.FindAsync([id], ct);
        if (relation is null) return new(PromoteNoteStatus.NotFound);
        var text = Clean(note) ?? Clean(relation.ReviewNote);
        if (text is null) return new(PromoteNoteStatus.NoNote);

        relation.ReviewNote = text;
        return await UpsertRulingAsync(
            scope: "relation",
            subject: BrainRef.Relation(relation.Id).Format(),
            question: $"{relation.FromRef} {relation.Kind} {relation.ToRef}: {relation.Explanation}",
            text, ct);
    }

    private async Task<PromoteNoteResult> UpsertRulingAsync(
        string scope, string subject, string question, string text, CancellationToken ct)
    {
        var correction = await db.Corrections
            .FirstOrDefaultAsync(c => c.Scope == scope && c.Ref == subject, ct);
        var updated = correction is not null;
        if (correction is null)
        {
            correction = new Correction
            {
                Scope = scope, Ref = subject, Text = text,
                Provenance = "review-notitie",
            };
            db.Corrections.Add(correction);
        }
        correction.Text = text;
        correction.Question = question;
        correction.Status = "verified";
        correction.VerifiedAt = DateTimeOffset.UtcNow;

        var embedded = true;
        try
        {
            // Zelfde embed-input als corrections/verify, zodat /ask de ruling
            // semantisch vindt.
            correction.Embedding = await embeddings.EmbedOneAsync($"{question}\n{text}", ct);
        }
        catch
        {
            // Ollama tijdelijk weg — promotie telt (de ruling doet via het
            // recentste-vangnet al mee); een oude embedding hoort bij de oude
            // tekst, dus liever geen embedding dan een stille mismatch.
            // Nogmaals promoveren embedt opnieuw.
            correction.Embedding = null;
            embedded = false;
        }
        await db.SaveChangesAsync(ct);
        return new(PromoteNoteStatus.Promoted, correction.Id, embedded, updated);
    }

    /// <summary>Notities zijn tekst of niets: witruimte telt niet als notitie.</summary>
    private static string? Clean(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
