using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van een on-demand deck-fetch (#346). Precies één van
/// Deck/Error is gevuld; Status is de HTTP-status die het endpoint teruggeeft
/// (200, 400 ongeldige id, 404 deck weg op PA, 502 PA-storing) — de mapping
/// is een service-beslissing, het endpoint blijft dun.</summary>
public record DeckFetchResult(DeckDetail? Deck, int Status, string? Error);

/// <summary>On-demand deck ophalen van Piltover Archive (#346): de score-pad
/// laat een speler een PA-deck-link plakken, en dat deck hoeft nog niet in
/// de sitemap-backfill te zitten. Staat het deck al in de DB, dan is dit een
/// gewone detail-read; anders één gerichte fetch van de publieke
/// /decks/view/{uuid}-pagina via het bestaande ingest-pad
/// (DeckIngestService.IngestDeckAsync — zelfde parser, zelfde upsert, zelfde
/// run_log-grootboek, dus de bulk-run fetcht dit deck niet nóg eens). De
/// robots-afspraak blijft hard: alleen die publieke pagina, nooit hun /api/.</summary>
public partial class DeckFetchService(
    RbRulesDbContext db, DeckIngestService ingest, DeckBrowserService decks)
{
    /// <summary>Strikte uuid-vorm (lowercase, ná normalisatie): alles wat
    /// hier niet aan voldoet gaat als 400 terug en bereikt Piltover nooit —
    /// wij zijn te gast en sturen geen rommel-URL's naar hun server.</summary>
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    /// <summary>Trim + lowercase, dan de strikte uuid-toets; null = geen
    /// geldige deck-id. De lengte-check vóór de regex houdt een geplakte
    /// megastring uit de matcher (zelfde verweer als DeckCodeService).</summary>
    public static string? NormalizeId(string? input)
    {
        var candidate = input?.Trim().ToLowerInvariant();
        if (candidate is null || candidate.Length != 36) return null;
        return UuidPattern().IsMatch(candidate) ? candidate : null;
    }

    public async Task<DeckFetchResult> FetchAsync(
        string? id, string format = DeckBrowserService.DefaultFormat,
        CancellationToken ct = default)
    {
        var uuid = NormalizeId(id);
        if (uuid is null)
            return new(null, 400,
                "Geen geldige Piltover Archive deck-id (uuid verwacht).");

        // Al bekend? Dan geen externe request — zelfde vorm als GET
        // /api/decks/{id}, en de ingest houdt het deck vanzelf vers.
        if (await decks.DetailAsync(uuid, format, ct) is { } existing)
            return new(existing, 200, null);

        // Zelfde kaartkoppeling als de bulk-run (variantgroepering, #54).
        var linker = new DeckCardLinker(await db.Cards.AsNoTracking()
            .Select(c => new Card { RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf })
            .ToListAsync(ct));

        var one = await ingest.IngestDeckAsync(uuid, linker, ct);
        if (one.Outcome == DeckIngestOutcome.NotFound)
            return new(null, 404, "Deck bestaat niet (meer) op Piltover Archive.");
        if (one.Outcome != DeckIngestOutcome.Saved)
            return new(null, 502,
                $"Deck kon niet van Piltover Archive gehaald worden: {one.Detail}");

        // Herlezen via hetzelfde detail-pad als GET /api/decks/{id}. Null kan
        // alleen als de pagina een ánder deck-id droeg dan gevraagd — dat is
        // een PA-anomalie, geen fout van de aanvrager.
        return await decks.DetailAsync(uuid, format, ct) is { } detail
            ? new(detail, 200, null)
            : new(null, 502,
                "Piltover Archive gaf een pagina terug die niet bij deze deck-id hoort.");
    }
}
