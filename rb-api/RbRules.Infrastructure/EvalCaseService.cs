using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Lijstweergave van één eval-geval voor beheer.</summary>
public sealed record EvalCaseListItem(
    string Id, string Question, string QueryType, string Status,
    IReadOnlyList<string> ExpectedCitations, string? Origin, long? OriginRef,
    DateTimeOffset CreatedAt);

public sealed record EvalCaseCounts(int Active, int Shadow, int Retired);

/// <summary>Uitkomst van een promotie: het geval, of waarom niet.</summary>
public sealed record EvalPromotion(EvalCaseListItem? Case, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>De eval-set in Postgres (#387): promoveren vanuit een trace of
/// geheugenrij, status beheren, en de cases als Domain-<see cref="EvalCase"/>
/// aan de runner leveren. De rekenregels zitten in
/// <see cref="EvalCasePromotion"/> (Domain, puur).</summary>
public class EvalCaseService(RbRulesDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record ForbiddenDto(string Id, string Text, string? SupersededByErratum);

    /// <summary>Promoveer een vraag-trace: de vraag plus de §-codes uit
    /// <c>AskTrace.Sections</c> als verwachte citaties. Geen citaties ⇒ geen
    /// geval (een eval zonder verwachting meet niets); dezelfde vraag al in de
    /// set ⇒ geweigerd met uitleg.</summary>
    public async Task<EvalPromotion> PromoteFromTraceAsync(long traceId, CancellationToken ct)
    {
        var t = await db.AskTraces.AsNoTracking()
            .Where(x => x.Id == traceId)
            .Select(x => new { x.Question, x.QuestionType, x.Sections, x.Ok })
            .SingleOrDefaultAsync(ct);
        if (t is null) return new(null, "Trace niet gevonden.");
        if (!t.Ok) return new(null, "Een mislukt antwoord is geen verwachting.");
        var ids = EvalCasePromotion.SectionIdsFromTrace(t.Sections);
        return await PromoteAsync(t.Question, t.QuestionType, ids,
            EvalCasePromotion.OriginTrace, traceId, ct);
    }

    /// <summary>Promoveer een geheugenrij: de citaties van het bewaarde antwoord
    /// (§-codes) als verwachting. Alleen bevestigde/geverifieerde rijen — een
    /// kandidaat is nog niemands oordeel, een ingetrokken rij is verouderd.</summary>
    public async Task<EvalPromotion> PromoteFromMemoryAsync(long memoryId, CancellationToken ct)
    {
        var m = await db.AnswerMemories.AsNoTracking()
            .Where(x => x.Id == memoryId)
            .Select(x => new { x.Question, x.QuestionType, x.CitationsJson, x.Trust })
            .SingleOrDefaultAsync(ct);
        if (m is null) return new(null, "Geheugenrij niet gevonden.");
        if (!AnswerMemoryPolicy.MayServe(m.Trust))
            return new(null, "Alleen een bevestigde of geverifieerde rij kan een eval-geval worden.");
        var citations = AnswerMemoryService.Citations(new AnswerMemory
        {
            Question = m.Question, QuestionNormalized = m.Question, Answer = "",
            CitationsJson = m.CitationsJson, SourceSnapshot = ",",
        });
        var ids = citations
            .Where(c => !string.IsNullOrWhiteSpace(c.Section))
            .Select(c => EvalCasePromotion.SectionId(c.Section!))
            .ToList();
        return await PromoteAsync(m.Question, m.QuestionType, ids,
            EvalCasePromotion.OriginMemory, memoryId, ct);
    }

    private async Task<EvalPromotion> PromoteAsync(
        string question, string? questionType, IReadOnlyList<string> ids,
        string origin, long originRef, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new(null, "Geen geciteerde regelsecties — zonder verwachting valt er niets te meten.");
        var draft = EvalCasePromotion.Draft(question, questionType, ids, DateOnly.FromDateTime(DateTime.UtcNow));
        if (await db.EvalCases.AnyAsync(c => c.Id == draft.Id, ct))
            return new(null, $"Deze vraag staat al in de eval-set ({draft.Id}).");
        var row = new EvalCaseRecord
        {
            Id = draft.Id,
            Question = draft.Question,
            QueryType = draft.QueryType.ToString(),
            Status = draft.Status.ToString().ToLowerInvariant(),
            ValidFrom = draft.ValidFrom,
            GoldSupportJson = JsonSerializer.Serialize(draft.GoldSupport, Json),
            ExpectedCitationsJson = JsonSerializer.Serialize(draft.ExpectedCitations, Json),
            Origin = origin,
            OriginRef = originRef,
        };
        db.EvalCases.Add(row);
        await db.SaveChangesAsync(ct);
        return new(ToItem(row), null);
    }

    /// <summary>Status zetten (shadow ↔ active ↔ retired). Onbekende status ⇒ false.</summary>
    public async Task<bool> SetStatusAsync(string id, string status, CancellationToken ct)
    {
        if (!Enum.TryParse<EvalStatus>(status, ignoreCase: true, out var parsed)) return false;
        var row = await db.EvalCases.FindAsync([id], ct);
        if (row is null) return false;
        row.Status = parsed.ToString().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var row = await db.EvalCases.FindAsync([id], ct);
        if (row is null) return false;
        db.EvalCases.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<EvalCaseListItem>> ListAsync(CancellationToken ct) =>
        [.. (await db.EvalCases.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct))
            .Select(ToItem)];

    public async Task<EvalCaseCounts> CountsAsync(CancellationToken ct) => new(
        await db.EvalCases.CountAsync(c => c.Status == "active", ct),
        await db.EvalCases.CountAsync(c => c.Status == "shadow", ct),
        await db.EvalCases.CountAsync(c => c.Status == "retired", ct));

    /// <summary>Alle gevallen als Domain-cases voor de runner (de runner
    /// filtert zelf op <see cref="EvalCase.IsInEffect"/>).</summary>
    public async Task<IReadOnlyList<EvalCase>> DomainCasesAsync(CancellationToken ct) =>
        [.. (await db.EvalCases.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct)).Select(ToDomain)];

    public static EvalCase ToDomain(EvalCaseRecord r) => new()
    {
        Id = r.Id,
        Question = r.Question,
        QueryType = Enum.TryParse<EvalQueryType>(r.QueryType, true, out var qt) ? qt : EvalQueryType.Factoid,
        Status = Enum.TryParse<EvalStatus>(r.Status, true, out var st) ? st : EvalStatus.Shadow,
        ValidFrom = r.ValidFrom,
        ValidUntil = r.ValidUntil,
        SupersededByErratum = r.SupersededByErratum,
        GoldSupport = Ids(r.GoldSupportJson),
        ExpectedCitations = Ids(r.ExpectedCitationsJson),
        ForbiddenClaims = Forbidden(r.ForbiddenClaimsJson),
    };

    private static EvalCaseListItem ToItem(EvalCaseRecord r) => new(
        r.Id, r.Question, r.QueryType, r.Status, Ids(r.ExpectedCitationsJson),
        r.Origin, r.OriginRef, r.CreatedAt);

    private static IReadOnlyList<string> Ids(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<ForbiddenClaim> Forbidden(string json)
    {
        try
        {
            return [.. (JsonSerializer.Deserialize<List<ForbiddenDto>>(json, Json) ?? [])
                .Select(f => new ForbiddenClaim(f.Id, f.Text, f.SupersededByErratum))];
        }
        catch (JsonException) { return []; }
    }
}
