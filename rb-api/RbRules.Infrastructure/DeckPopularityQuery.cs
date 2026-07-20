using Microsoft.EntityFrameworkCore;

namespace RbRules.Infrastructure;

/// <summary>Het deck-gebruikssignaal (#15 golf 1 spoor B) op één plek: het
/// aandeel van de recente Piltover Archive-decks dat een kaart speelt, het
/// gemiddeld aantal exemplaren en de top-co-occurrence. Oorspronkelijk
/// gebouwd voor het kaartdossier (CardDetailService); sinds #267 leest ook
/// het deck-meta-kanaal van /ask (kennislaag 3) hetzelfde signaal — vandaar
/// een statische query met expliciete context-parameter: de /ask-kanalen
/// draaien elk op een eigen DbContext uit de factory (#152).</summary>
public static class DeckPopularityQuery
{
    /// <summary>Poolgrootte voor "recent": de N meest recent bijgewerkte
    /// decks (PA's <c>updatedAt</c>), geen kalendervenster. De backfill
    /// (#15 spoor 2) loopt nog en vult de bank geleidelijk richting de
    /// ~10.000 sitemap-decks; een vast kalendervenster (bv. 90 dagen) zou de
    /// noemer onvoorspelbaar maken — soms 0 vlak na een cold start, dan
    /// weer duizenden zodra de bank vol is. Een vaste poolgrootte geeft een
    /// stabiele, altijd-vergelijkbare noemer en valt vanzelf terug op "alle
    /// decks die we hebben" zolang de bank kleiner is dan de pool.</summary>
    public const int RecentDeckWindow = 500;

    /// <summary>Onder deze noemer is een percentage misleidend eerlijk
    /// noch onbetrouwbaar — de UI toont dan absolute aantallen in plaats
    /// van een "N%"-claim (ThinData).</summary>
    public const int MinRecentDecksForSignal = 20;

    private const int CoOccurrenceCap = 5;

    /// <summary>Secties die het daadwerkelijk ingeleverde deck vertegen-
    /// woordigen: champions, hoofddeck, runes en battlefields. Bewust
    /// buiten de telling: <c>sideboard</c> (matchup-tech, geen kernidentiteit
    /// van het deck — meetellen zou "populair" laten oplopen voor kaarten
    /// die zelden echt gespeeld worden), <c>bench</c> (Piltover Archive's
    /// bouwer-kladblok voor overwogen kaarten, geen ingeleverde lijst) en
    /// <c>legend</c> (precies 1 per deck — een eigen signaal, geen "aandeel
    /// van het deck" zoals de andere secties).</summary>
    private static readonly string[] PopularitySections =
        ["champions", "maindeck", "runes", "battlefields"];

    /// <summary>De "recent"-pool zelf: de <see cref="RecentDeckWindow"/> meest
    /// recent bijgewerkte decks. Zelfde "recentst"-maat als het beheeroverzicht
    /// (AdminOverviewService.DecksAsync): PA's eigen updatedAt, met onze
    /// FetchedAt als terugval voor de zeldzame pagina zonder datum —
    /// consistent "recent" door de hele bank heen, in plaats van een tweede
    /// definitie. Apart opvraagbaar (#318-review B2) zodat een aanroeper met
    /// meerdere kaarten — het /ask-deck-meta-kanaal — de pool één keer ophaalt
    /// en hergebruikt: dit is de duurste query van het signaal (sort over de
    /// hele deck-bank).</summary>
    public static Task<List<long>> RecentDeckIdsAsync(
        RbRulesDbContext db, CancellationToken ct) =>
        db.Decks.AsNoTracking()
            .OrderByDescending(d => d.PaUpdatedAt ?? d.FetchedAt)
            .ThenBy(d => d.Id)
            .Take(RecentDeckWindow)
            .Select(d => d.Id)
            .ToListAsync(ct);

    /// <summary>Aandeel van de recente decks dat deze kaart speelt (#15).
    /// canonicalId is altijd al de canonieke groeps-id (CardText.CanonicalId)
    /// — varianten en de basisprinting delen hetzelfde signaal, net als de
    /// rest van het kaartdossier.</summary>
    public static async Task<CardDeckPopularity> ForCanonicalAsync(
        RbRulesDbContext db, string canonicalId, CancellationToken ct) =>
        await ForCanonicalAsync(db, canonicalId, await RecentDeckIdsAsync(db, ct), ct);

    /// <summary>Variant met een reeds opgehaalde recente-decks-pool
    /// (#318-review B2): de pool is expliciete invoer — noemer én afbakening
    /// van alle tellingen — zodat meerdere kaarten binnen één vraag tegen
    /// exact dezelfde pool gemeten worden zonder de pool-query te herhalen.</summary>
    public static async Task<CardDeckPopularity> ForCanonicalAsync(
        RbRulesDbContext db, string canonicalId, List<long> recentDeckIds,
        CancellationToken ct)
    {
        var recentDeckCount = recentDeckIds.Count;
        if (recentDeckCount == 0) return new(0, 0, 0, null, true, []);

        // Eén deck telt maar één keer (GroupBy op DeckId), ook als de kaart
        // in meerdere relevante secties van hetzelfde deck voorkomt. De som
        // van Quantity per deck geeft meteen het "aantal exemplaren wanneer
        // gespeeld" zonder een tweede rondje.
        var perDeck = await db.DeckCards.AsNoTracking()
            .Where(dc => recentDeckIds.Contains(dc.DeckId)
                && dc.CanonicalRiftboundId == canonicalId
                && PopularitySections.Contains(dc.Section))
            .GroupBy(dc => dc.DeckId)
            .Select(g => new { DeckId = g.Key, Copies = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var deckCount = perDeck.Count;
        var avgCopies = deckCount == 0 ? (double?)null : Math.Round(perDeck.Average(x => x.Copies), 1);
        var percentage = Math.Round(100.0 * deckCount / recentDeckCount, 1);
        var thin = recentDeckCount < MinRecentDecksForSignal;

        IReadOnlyList<CardCoOccurrence> coPlayed = [];
        if (deckCount > 0)
        {
            // Kruisproduct bewust klein gehouden: alleen de decks die de
            // dossierkaart al bevatten (hooguit RecentDeckWindow), geen
            // volledige deck-bank. Distinct (deck, kaart)-paren eerst
            // materialiseren en dán in-memory groeperen/tellen — een
            // geneste Distinct().Count() binnen een EF-GroupBy is niet
            // betrouwbaar vertaalbaar (CONVENTIONS: bij twijfel materialiseren).
            var deckIdsWithCard = perDeck.Select(x => x.DeckId).ToList();
            var pairs = await db.DeckCards.AsNoTracking()
                .Where(dc => deckIdsWithCard.Contains(dc.DeckId)
                    && dc.CanonicalRiftboundId != null
                    && dc.CanonicalRiftboundId != canonicalId
                    && PopularitySections.Contains(dc.Section))
                .Select(dc => new { dc.DeckId, CardId = dc.CanonicalRiftboundId! })
                .Distinct()
                .ToListAsync(ct);

            var top = pairs
                .GroupBy(p => p.CardId)
                .Select(g => new { CardId = g.Key, Decks = g.Count() })
                .OrderByDescending(x => x.Decks)
                .ThenBy(x => x.CardId, StringComparer.Ordinal)
                .Take(CoOccurrenceCap)
                .ToList();

            var names = await db.Cards.AsNoTracking()
                .Where(c => top.Select(x => x.CardId).Contains(c.RiftboundId))
                .Select(c => new { c.RiftboundId, c.Name })
                .ToDictionaryAsync(c => c.RiftboundId, c => c.Name, ct);
            coPlayed = [.. top.Select(x => new CardCoOccurrence(
                x.CardId, names.GetValueOrDefault(x.CardId, x.CardId), x.Decks))];
        }

        return new(deckCount, recentDeckCount, percentage, avgCopies, thin, coPlayed);
    }
}
