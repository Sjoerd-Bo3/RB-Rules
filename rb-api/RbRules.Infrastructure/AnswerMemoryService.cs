using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>De dichtstbijzijnde eerdere vraag (#384), ongeacht trust: de
/// beslissing wat ermee te doen (dienen, tellen, alleen tonen) valt in
/// <see cref="AnswerMemoryService.LookupAsync"/> op grond van
/// <see cref="AnswerMemoryPolicy"/>.</summary>
public sealed record MemoryMatch(AnswerMemory Row, double Similarity);

/// <summary>Uitkomst van een geheugen-lookup voor één vraag.</summary>
/// <param name="Serve">Niet-null ⇒ dit antwoord mag gediend worden zonder
/// LLM-call: dezelfde vraag, bevestigd, bronnen ongewijzigd.</param>
/// <param name="Same">Dezelfde vraag (boven de serve-drempel) maar níet te
/// dienen — een kandidaat die een hit erbij krijgt, of een ingetrokken rij.
/// Bij een kandidaat wordt de nieuwe vraag NIET opnieuw bewaard (dedupe).</param>
/// <param name="Similar">Een vergelijkbare eerdere vraag (0,80–0,92) — alleen
/// ter weergave naast het verse antwoord.</param>
public sealed record MemoryLookup(MemoryMatch? Serve, MemoryMatch? Same, MemoryMatch? Similar)
{
    public static readonly MemoryLookup None = new(null, null, null);
}

/// <summary>Antwoordgeheugen (#384): de I/O rond <see cref="AnswerMemory"/>.
/// Elke publieke methode is best-effort — een geheugen dat omvalt mag een
/// vraag nooit laten falen; de aanroeper (AskService) vangt exceptions als
/// "geen geheugen". Vector-zoeken zit in de overridable
/// <see cref="NearestAsync"/>, zodat tests op EF InMemory (dat geen
/// CosineDistance kent) de cosinus zelf kunnen uitrekenen.</summary>
public class AnswerMemoryService(
    RbRulesDbContext db, ILogger<AnswerMemoryService> logger,
    ManagedSettingsService? settings = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Staat het geheugen aan? Beheerde instelling (#254) op het
    /// gebruiksmoment; zonder settings-service (tests) de env-/codewaarde.</summary>
    public async Task<bool> EnabledAsync(CancellationToken ct) =>
        settings is null
            ? AskMemorySettings.FromEnvironment().Enabled
            : (await settings.AskMemoryAsync(ct)).Enabled;

    /// <summary>Zoek de dichtstbijzijnde eerdere vraag en beslis wat ermee mag.
    /// <paramref name="questionType"/> is het vraagtype van de NIEUWE vraag: een
    /// bewaard antwoord op een ander vraagtype (andere structuur, andere
    /// bron-bias) dient nooit, hoe gelijkend de tekst ook is.</summary>
    public async Task<MemoryLookup> LookupAsync(
        Vector questionVector, string questionType, CancellationToken ct)
    {
        var candidates = await NearestAsync(questionVector, 3, ct);
        MemoryMatch? serve = null, same = null, similar = null;
        foreach (var m in candidates)
        {
            if (m.Row.QuestionType != questionType) continue;
            if (AnswerMemoryPolicy.IsSameQuestion(m.Similarity))
            {
                if (AnswerMemoryPolicy.MayServe(m.Row.Trust) && serve is null
                    && await SourcesUnchangedAsync(m.Row, ct))
                    serve = m;
                else if (same is null && m.Row.Trust != AnswerMemoryPolicy.Retracted)
                    same = m;
            }
            else if (similar is null && AnswerMemoryPolicy.IsSimilarQuestion(m.Similarity)
                     && m.Row.Trust != AnswerMemoryPolicy.Retracted)
            {
                similar = m;
            }
        }
        return new MemoryLookup(serve, serve is null ? same : null, similar);
    }

    /// <summary>De k dichtstbijzijnde rijen op cosinus-gelijkenis (1 = identiek),
    /// ingetrokken rijen inbegrepen — de aanroeper filtert. Overridable voor
    /// tests (InMemory kent geen pgvector-operators).</summary>
    protected virtual async Task<IReadOnlyList<MemoryMatch>> NearestAsync(
        Vector questionVector, int k, CancellationToken ct)
    {
        var rows = await db.AnswerMemories
            .Where(m => m.Embedding != null)
            .OrderBy(m => m.Embedding!.CosineDistance(questionVector))
            .Take(k)
            .Select(m => new { Row = m, Distance = m.Embedding!.CosineDistance(questionVector) })
            .ToListAsync(ct);
        return [.. rows.Select(r => new MemoryMatch(r.Row, 1 - r.Distance))];
    }

    /// <summary>Zijn de bronnen waarop dit antwoord leunde nog dezelfde? Een
    /// gewijzigde bron trekt de rij meteen in (met reden) — de invalidatie via
    /// de wijzigingen-feed is de eerste linie, dit is het vangnet.</summary>
    private async Task<bool> SourcesUnchangedAsync(AnswerMemory row, CancellationToken ct)
    {
        var ids = AnswerMemoryPolicy.DecodeSnapshot(row.SourceSnapshot).Select(s => s.SourceId).ToList();
        if (ids.Count == 0) return true;
        var current = await db.Sources.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.LastHash })
            .ToDictionaryAsync(s => s.Id, s => s.LastHash, ct);
        if (AnswerMemoryPolicy.SourcesUnchanged(row.SourceSnapshot, current)) return true;
        row.Trust = AnswerMemoryPolicy.Retracted;
        row.RetractedReason = "bron gewijzigd (bij hergebruik geconstateerd)";
        row.RetractedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return false;
    }

    /// <summary>Een hit registreren (gediend of alleen herkend): teller, tijdstip
    /// en de promotie kandidaat → bevestigd na <see cref="AnswerMemoryPolicy.ConfirmAfterHits"/>.</summary>
    public async Task RegisterHitAsync(AnswerMemory row, CancellationToken ct)
    {
        row.HitCount++;
        row.LastHitAt = DateTimeOffset.UtcNow;
        row.Trust = AnswerMemoryPolicy.TrustAfterHit(row.Trust, row.HitCount);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Een vers antwoord bewaren als kandidaat. <paramref name="sourceIds"/>
    /// zijn de bronnen van de citaties; hun huidige <c>LastHash</c> gaat als
    /// momentopname mee zodat een latere wijziging het antwoord intrekt.</summary>
    public async Task<AnswerMemory> RememberAsync(
        string question, string questionNormalized, string questionType,
        Vector questionVector, string answer, IReadOnlyList<Citation> citations,
        IEnumerable<string> sourceIds, IEnumerable<long> retrievalSet,
        string model, string promptVersion, long? userId, CancellationToken ct)
    {
        var ids = sourceIds.Distinct().ToList();
        var hashes = ids.Count == 0
            ? []
            : await db.Sources.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, s.LastHash })
                .ToListAsync(ct);
        var row = new AnswerMemory
        {
            Question = question.Length > 500 ? question[..500] : question,
            QuestionNormalized = questionNormalized.Length > 500 ? questionNormalized[..500] : questionNormalized,
            QuestionType = questionType,
            Embedding = questionVector,
            EmbeddingModel = EmbeddingConfig.Model,
            EmbeddingContentHash = EmbeddingProvenance.ContentHash(question),
            Answer = answer,
            CitationsJson = JsonSerializer.Serialize(citations, Json),
            SourceSnapshot = AnswerMemoryPolicy.EncodeSnapshot(
                hashes.Select(h => (h.Id, h.LastHash))),
            RetrievalSet = string.Join(",", retrievalSet),
            Model = model,
            PromptVersion = promptVersion,
            UserId = userId,
        };
        db.AnswerMemories.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>De citaties van een bewaarde rij terug als records. Een
    /// onparseerbare rij (hoort niet voor te komen) geeft een lege lijst — dan
    /// dient de rij niet (de aanroeper eist citaties).</summary>
    public static IReadOnlyList<Citation> Citations(AnswerMemory row)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Citation>>(row.CitationsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Gebruikersfeedback (duim omhoog/omlaag) op een gediend of vers
    /// antwoord: de promotielus uit <see cref="AnswerMemoryPolicy.TrustAfterFeedback"/>.
    /// False = rij bestaat niet.</summary>
    public async Task<bool> FeedbackAsync(long id, bool thumbsUp, CancellationToken ct)
    {
        var row = await db.AnswerMemories.FindAsync([id], ct);
        if (row is null) return false;
        var next = AnswerMemoryPolicy.TrustAfterFeedback(row.Trust, thumbsUp);
        if (next == AnswerMemoryPolicy.Retracted && row.Trust != AnswerMemoryPolicy.Retracted)
        {
            row.RetractedReason = "door gebruiker als onjuist gemarkeerd";
            row.RetractedAt = DateTimeOffset.UtcNow;
        }
        row.Trust = next;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Beheer: verifiëren (hoogste trust) of intrekken met reden.</summary>
    public async Task<bool> ReviewAsync(long id, bool verify, string? reason, CancellationToken ct)
    {
        var row = await db.AnswerMemories.FindAsync([id], ct);
        if (row is null) return false;
        if (verify)
        {
            row.Trust = AnswerMemoryPolicy.Verified;
            row.RetractedReason = null;
            row.RetractedAt = null;
        }
        else
        {
            row.Trust = AnswerMemoryPolicy.Retracted;
            row.RetractedReason = string.IsNullOrWhiteSpace(reason) ? "door beheer ingetrokken" : reason.Trim();
            row.RetractedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Invalidatie vanuit de wijzigingen-feed (#384): elke niet-
    /// ingetrokken rij die deze bron citeert gaat naar <c>retracted</c> met de
    /// reden erbij. Statisch en op de meegegeven context, zodat IngestService
    /// het in zijn eigen SaveChanges kan meenemen — de wijziging en de
    /// intrekking landen dan atomair. Tracked update i.p.v. ExecuteUpdate: de
    /// aantallen zijn klein en EF InMemory kent ExecuteUpdate niet.</summary>
    public static async Task<int> RetractForSourceAsync(
        RbRulesDbContext db, string sourceId, string reason, CancellationToken ct)
    {
        var key = AnswerMemoryPolicy.SnapshotKey(sourceId);
        var rows = await db.AnswerMemories
            .Where(m => m.Trust != AnswerMemoryPolicy.Retracted && m.SourceSnapshot.Contains(key))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.Trust = AnswerMemoryPolicy.Retracted;
            row.RetractedReason = reason;
            row.RetractedAt = now;
        }
        return rows.Count;
    }

    /// <summary>Beheeroverzicht: de recentste rijen, zonder embedding.</summary>
    public async Task<IReadOnlyList<AnswerMemoryListItem>> ListAsync(int take, CancellationToken ct) =>
        await db.AnswerMemories.AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new AnswerMemoryListItem(
                m.Id, m.Question, m.QuestionType, m.Trust, m.HitCount, m.LastHitAt,
                m.CreatedAt, m.Model, m.RetractedReason, m.Answer))
            .ToListAsync(ct);

    public async Task<AnswerMemoryCounts> CountsAsync(CancellationToken ct) => new(
        await db.AnswerMemories.CountAsync(m => m.Trust == AnswerMemoryPolicy.Candidate, ct),
        await db.AnswerMemories.CountAsync(
            m => m.Trust == AnswerMemoryPolicy.Confirmed || m.Trust == AnswerMemoryPolicy.Verified, ct),
        await db.AnswerMemories.CountAsync(m => m.Trust == AnswerMemoryPolicy.Retracted, ct));

    /// <summary>Logt een geheugen-storing één keer per aanroep: het geheugen is
    /// best-effort, maar stil falen is de #282-les — dan is de laag "aan"
    /// terwijl niemand hem ooit ziet werken.</summary>
    public void LogFailure(Exception ex, string stage) =>
        logger.LogWarning(ex, "antwoordgeheugen (#384) overgeslagen in stap {Stage}", stage);
}

public sealed record AnswerMemoryListItem(
    long Id, string Question, string? QuestionType, string Trust, int HitCount,
    DateTimeOffset? LastHitAt, DateTimeOffset CreatedAt, string? Model,
    string? RetractedReason, string Answer);

public sealed record AnswerMemoryCounts(int Candidates, int Servable, int Retracted);
