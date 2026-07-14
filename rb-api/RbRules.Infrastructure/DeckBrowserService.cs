using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

// Response-records van de deck-browser (#15 fase 3, spoor A): zelfde afspraak
// als bij RulingsService/BrainService — de service bouwt de responses en
// Infrastructure mag niet naar Api verwijzen, dus de records leven hier.

public record DeckLegalityIssueView(string CardCode, string? CardName, string Reason);

public record DeckLegalityView(string Status, IReadOnlyList<DeckLegalityIssueView> Issues, int UnknownCount)
{
    public static DeckLegalityView From(DeckLegalityResult result) => new(
        DeckLegalityResult.Key(result.Status),
        [.. result.Issues.Select(i => new DeckLegalityIssueView(i.CardCode, i.CardName, i.Reason))],
        result.UnknownCount);
}

/// <summary>Eén deck in de lijst/facet-browser: geen kaartregels, wel de
/// legaliteitsuitkomst (die vraagt om alle regels van dit deck te evalueren,
/// vandaar vooraf uitgerekend in <see cref="DeckBrowserService.ListAsync"/>).</summary>
public record DeckSummary(
    string Id, string? Name, string[] Domains, int CardCount, int Views, int Likes,
    string SourceUrl, DateTimeOffset? PaUpdatedAt, DeckLegalityView Legality);

/// <summary>Het actieve kaart-filter (deep-link vanaf de kaartpagina,
/// #15 spoor B → spoor A): de canonieke kaart waarop gefilterd is, met de
/// naam voor een leesbare kop. Null als er geen kaart-filter is.</summary>
public record DeckCardFilter(string CanonicalId, string? Name);

public record DeckListResponse(
    IReadOnlyList<DeckSummary> Items, int Total, int Page, int PageSize,
    DeckCardFilter? CardFilter = null);

public record DeckFacets(IReadOnlyList<string> Domains);

/// <summary>Eén kaartregel op de detailpagina: CanonicalRiftboundId/CardName/
/// ImageUrl zijn null zolang PA-ingest de kaart (nog) niet kon koppelen
/// (DeckCardLinker) — de pagina toont dan de rauwe CardCode zonder link.</summary>
public record DeckCardView(
    string CardCode, int Quantity, string? CanonicalRiftboundId, string? CardName, string? ImageUrl);

public record DeckSectionView(string Section, IReadOnlyList<DeckCardView> Cards);

public record DeckDetail(
    string Id, string? Name, string[] Domains, string SourceUrl,
    DateTimeOffset? PaCreatedAt, DateTimeOffset? PaUpdatedAt, int Views, int Likes,
    IReadOnlyList<DeckSectionView> Sections, DeckLegalityView Legality);

/// <summary>Read-only projectie boven op de Piltover Archive-decks (#15 fase
/// 3, spoor A): lijst/facetten/paginering + legaliteitscheck en deep-link
/// naar de bron. Nadrukkelijk geen deck-mutatie — alleen bladeren in wat
/// DeckIngestService al heeft opgeslagen.</summary>
public class DeckBrowserService(RbRulesDbContext db)
{
    public const int PageSize = 24;
    public const string DefaultFormat = "constructed";

    /// <summary>De canonieke sectievolgorde voor weergave: legend eerst (het
    /// legend-kaart-object), dan PiltoverDeckPage.CardSections in hun eigen
    /// vaste volgorde. Lege secties worden niet getoond.</summary>
    private static readonly string[] SectionOrder =
        [PiltoverDeckPage.LegendSection, .. PiltoverDeckPage.CardSections];

    public async Task<DeckListResponse> ListAsync(
        string? domain, string? sort, int page, string format = DefaultFormat,
        string? card = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var query = db.Decks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(domain)) query = query.Where(d => d.Domains.Contains(domain));

        // Kaart-filter (deep-link vanaf de kaartpagina, spoor B → A): resolve
        // naar de canonieke groeps-id (net als het deck-gebruikssignaal in
        // CardDetailService) en beperk tot decks die die kaart bevatten.
        var cardFilter = await ResolveCardFilterAsync(card, ct);
        if (cardFilter is not null)
        {
            var deckIdsWithCard = await db.DeckCards.AsNoTracking()
                .Where(dc => dc.CanonicalRiftboundId == cardFilter.CanonicalId)
                .Select(dc => dc.DeckId)
                .Distinct()
                .ToListAsync(ct);
            query = query.Where(d => deckIdsWithCard.Contains(d.Id));
        }

        var total = await query.CountAsync(ct);
        query = sort switch
        {
            "views" => query.OrderByDescending(d => d.Views).ThenByDescending(d => d.Id),
            "likes" => query.OrderByDescending(d => d.Likes).ThenByDescending(d => d.Id),
            _ => query.OrderByDescending(d => d.PaUpdatedAt ?? d.FetchedAt).ThenByDescending(d => d.Id),
        };

        var decks = await query
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(d => new
            {
                d.Id, d.PaId, d.Name, d.Domains, d.SourceUrl, d.PaUpdatedAt, d.Views, d.Likes,
            })
            .ToListAsync(ct);

        var deckIds = decks.Select(d => d.Id).ToList();
        var rows = await db.DeckCards.AsNoTracking()
            .Where(c => deckIds.Contains(c.DeckId))
            .Select(c => new { c.DeckId, c.CardCode, c.CanonicalRiftboundId, c.Quantity })
            .ToListAsync(ct);
        var rowsByDeck = rows.GroupBy(r => r.DeckId).ToDictionary(g => g.Key, g => g.ToList());

        var context = await LoadLegalityContextAsync(format, ct);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        var items = decks.Select(d =>
        {
            var deckRows = rowsByDeck.GetValueOrDefault(d.Id, []);
            var legality = DeckLegality.Evaluate(
                [.. deckRows.Select(r => context.ToLegalityCard(r.CardCode, null, r.CanonicalRiftboundId))],
                today);
            return new DeckSummary(
                d.PaId, d.Name, d.Domains, deckRows.Sum(r => r.Quantity), d.Views, d.Likes,
                d.SourceUrl, d.PaUpdatedAt, DeckLegalityView.From(legality));
        }).ToList();

        return new(items, total, page, PageSize, cardFilter);
    }

    /// <summary>Zet een kaart-id (canoniek of variant) om naar het canonieke
    /// groeps-id met de naam erbij, of null als de kaart onbekend is (dan geen
    /// filter — het is een deep-link, geen harde eis). Match op
    /// DeckCard.CanonicalRiftboundId, dat altijd al canoniek is.</summary>
    private async Task<DeckCardFilter?> ResolveCardFilterAsync(string? card, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(card)) return null;
        var hit = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == card)
            .Select(c => new { CanonicalId = c.VariantOf ?? c.RiftboundId, c.Name })
            .FirstOrDefaultAsync(ct);
        return hit is null ? new(card, null) : new(hit.CanonicalId, hit.Name);
    }

    public async Task<DeckFacets> FacetsAsync(CancellationToken ct = default)
    {
        // In-memory flatten (zelfde patroon als /api/cards/facets):
        // array-kolommen zijn geen bewezen vertaalbare SelectMany-bron
        // (docs/CONVENTIONS.md), dus eerst materialiseren, dan platslaan.
        var rows = await db.Decks.AsNoTracking().Select(d => d.Domains).ToListAsync(ct);
        var domains = rows.SelectMany(d => d).Distinct().Order().ToList();
        return new(domains);
    }

    public async Task<DeckDetail?> DetailAsync(
        string paId, string format = DefaultFormat, CancellationToken ct = default)
    {
        var deck = await db.Decks.AsNoTracking()
            .FirstOrDefaultAsync(d => d.PaId == paId, ct);
        if (deck is null) return null;

        var rows = await db.DeckCards.AsNoTracking()
            .Where(c => c.DeckId == deck.Id)
            .Select(c => new { c.Section, c.CardCode, c.CanonicalRiftboundId, c.Quantity })
            .ToListAsync(ct);

        var canonicalIds = rows.Select(r => r.CanonicalRiftboundId).OfType<string>().Distinct().ToList();
        var cardFacts = await db.Cards.AsNoTracking()
            .Where(c => canonicalIds.Contains(c.RiftboundId))
            .Select(c => new { c.RiftboundId, c.Name, c.ImageUrl, c.SetId })
            .ToDictionaryAsync(c => c.RiftboundId, ct);

        var context = await LoadLegalityContextAsync(format, ct);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        var legality = DeckLegality.Evaluate(
            [.. rows.Select(r => context.ToLegalityCard(
                r.CardCode,
                r.CanonicalRiftboundId is { } id ? cardFacts.GetValueOrDefault(id)?.Name : null,
                r.CanonicalRiftboundId))],
            today);

        var sections = SectionOrder
            .Select(section => new DeckSectionView(
                section,
                [.. rows.Where(r => r.Section == section)
                    .Select(r =>
                    {
                        var fact = r.CanonicalRiftboundId is { } id ? cardFacts.GetValueOrDefault(id) : null;
                        return new DeckCardView(
                            r.CardCode, r.Quantity, r.CanonicalRiftboundId, fact?.Name, fact?.ImageUrl);
                    })
                    .OrderBy(c => c.CardName ?? c.CardCode, StringComparer.Ordinal)]))
            .Where(s => s.Cards.Count > 0)
            .ToList();

        return new(
            deck.PaId, deck.Name, deck.Domains, deck.SourceUrl,
            deck.PaCreatedAt, deck.PaUpdatedAt, deck.Views, deck.Likes,
            sections, DeckLegalityView.From(legality));
    }

    /// <summary>Eén keer per aanroep geladen: set-releasedatums (handvol
    /// rijen) en de gebande canonieke kaarten voor dit format — daarna is elke
    /// kaartregel een dictionary-lookup in plaats van een eigen query.</summary>
    private async Task<LegalityContext> LoadLegalityContextAsync(string format, CancellationToken ct)
    {
        var setDates = await db.CardSets.AsNoTracking()
            .ToDictionaryAsync(s => s.SetId, s => s.PublishedOn, ct);
        var cardSets = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new { c.RiftboundId, c.SetId })
            .ToDictionaryAsync(c => c.RiftboundId, c => c.SetId, ct);
        var banned = await BanLookup.BannedCanonicalIdsAsync(db, format, ct);
        return new(setDates, cardSets, banned);
    }

    private sealed record LegalityContext(
        Dictionary<string, DateOnly?> SetPublishedOn,
        Dictionary<string, string?> CanonicalCardSetId,
        HashSet<string> BannedCanonicalIds)
    {
        public DeckLegalityCard ToLegalityCard(string cardCode, string? cardName, string? canonicalId)
        {
            var setId = canonicalId is not null ? CanonicalCardSetId.GetValueOrDefault(canonicalId) : null;
            var publishedOn = setId is not null ? SetPublishedOn.GetValueOrDefault(setId) : null;
            var banned = canonicalId is not null && BannedCanonicalIds.Contains(canonicalId);
            return new(cardCode, cardName, canonicalId, publishedOn, banned);
        }
    }
}
