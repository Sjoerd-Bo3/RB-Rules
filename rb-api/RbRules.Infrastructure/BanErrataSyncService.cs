using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BanErrataResult(int Bans, int Errata);

/// <summary>Structureert de banlijst en errata uit de nieuwste officiële
/// documenten via LLM-extractie (rb-ai). Vervangt de tekst-heuristiek uit de
/// PoP ("Bandle" matchte "ban"). Herbouwt de tabellen volledig per run —
/// de bron van waarheid is de officiële pagina, niet de vorige extractie.</summary>
public class BanErrataSyncService(RbRulesDbContext db, RbAiClient ai)
{
    public async Task<BanErrataResult> SyncAsync(CancellationToken ct = default)
    {
        var cards = await db.Cards
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);
        string? MatchCard(string name) => cards
            .FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.RiftboundId;

        var bans = 0;
        var hub = await LatestDocAsync("rules-hub", ct);
        if (hub is not null)
        {
            var raw = await ai.AskAsync(
                hub.Value.Content[..Math.Min(hub.Value.Content.Length, 12000)],
                BanErrataExtractor.BanSystemPrompt, ct: ct);
            if (raw is not null)
            {
                var extracted = BanErrataExtractor.ParseBans(raw);
                if (extracted.Count > 0)
                {
                    // Rebuild in één transactie: nooit een venster met lege
                    // banlijst als het middenin misgaat (review-fix).
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.BanEntries.ExecuteDeleteAsync(ct);
                    foreach (var b in extracted)
                    {
                        db.BanEntries.Add(new BanEntry
                        {
                            Name = b.Name,
                            Kind = b.Kind,
                            CardRiftboundId = MatchCard(b.Name),
                            SourceUrl = hub.Value.Url,
                        });
                        bans++;
                    }
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
            }
        }

        var errata = 0;
        var errataDoc = await LatestDocAsync("card-errata", ct);
        if (errataDoc is not null)
        {
            var raw = await ai.AskAsync(
                errataDoc.Value.Content[..Math.Min(errataDoc.Value.Content.Length, 12000)],
                BanErrataExtractor.ErrataSystemPrompt, ct: ct);
            if (raw is not null)
            {
                var extracted = BanErrataExtractor.ParseErrata(raw);
                if (extracted.Count > 0)
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.Errata.ExecuteDeleteAsync(ct);
                    foreach (var e in extracted)
                    {
                        db.Errata.Add(new Erratum
                        {
                            CardName = e.CardName,
                            NewText = e.NewText,
                            CardRiftboundId = MatchCard(e.CardName),
                            SourceUrl = errataDoc.Value.Url,
                        });
                        errata++;
                    }
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
            }
        }

        return new(bans, errata);
    }

    private async Task<(string Content, string Url)?> LatestDocAsync(string sourceId, CancellationToken ct)
    {
        var result = await db.Documents
            .Where(d => d.SourceId == sourceId)
            .OrderByDescending(d => d.RetrievedAt)
            .Join(db.Sources, d => d.SourceId, s => s.Id, (d, s) => new { d.Content, s.Url })
            .FirstOrDefaultAsync(ct);
        return result is null ? null : (result.Content, result.Url);
    }
}
