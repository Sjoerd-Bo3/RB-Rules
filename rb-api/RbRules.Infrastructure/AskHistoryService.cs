using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record AskHistoryItem(
    long Id, string Question, DateTimeOffset CreatedAt,
    string? QuestionType, string? Answer, bool Agentic);

/// <summary>Eigen ask-geschiedenis (#157): de laatste eigen vragen — voor een
/// ingelogde gebruiker op user_id, anders op de ip_hash van het huidige
/// request. Strikt eigen scope: geen id-parameter, dus geen enumeratie van
/// andermans historie. AsNoTracking (read-only).</summary>
public class AskHistoryService(RbRulesDbContext db)
{
    private const int Limit = 20;

    public async Task<IReadOnlyList<AskHistoryItem>> RecentAsync(
        long? userId, string? ipHash, CancellationToken ct = default)
    {
        var query = userId is { } uid
            ? db.AskTraces.Where(t => t.UserId == uid)
            : !string.IsNullOrEmpty(ipHash)
                ? db.AskTraces.Where(t => t.IpHash == ipHash)
                : null;
        // Geen user_id én geen ip_hash (#157): anoniem zonder
        // ASK_IP_HASH_SECRET of nog geen eerdere vraag — lege lijst.
        if (query is null) return [];

        return await query
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(Limit)
            .Select(t => new AskHistoryItem(
                t.Id, t.Question, t.CreatedAt, t.QuestionType, t.Answer, t.Agentic))
            .ToListAsync(ct);
    }
}
