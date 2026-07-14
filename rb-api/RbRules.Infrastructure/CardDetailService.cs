using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record CardVersion(
    string RiftboundId, string? SetId, string? SetLabel, string? Rarity,
    int? CollectorNumber, string? ImageUrl);

/// <summary>EffectiveFrom (#168): vanaf wanneer deze tekst gold (afgeleid van
/// de bron), null als de bron geen datum draagt. De lijst staat op
/// precedentie-volgorde (hoogste TrustTier, dan nieuwste EffectiveFrom) — het
/// eerste item is de tekst die NU geldt; latere items zijn candidate-
/// achterhaald (zichtbaar, niet verwijderd).</summary>
public record CardErratumRef(
    string NewText, string SourceUrl, DateTimeOffset DetectedAt, DateOnly? EffectiveFrom);
public record CardRelevantRule(string? Section, string Snippet, string SourceName, string Url);
public record CardRuleLinks(
    IReadOnlyList<CardErratumRef> Errata, IReadOnlyList<CardRelevantRule> RelevantRules);

public record CardDetail(
    string RiftboundId, string Name, string? Type, string? Supertype,
    string? Rarity, string[] Domains, int? Energy, int? Might, int? Power,
    string? SetId, string? SetLabel, int? CollectorNumber, string? TextPlain,
    string? ImageUrl, string[] Tags, string[]? Mechanics, string[]? Triggers,
    string[]? Effects, DateTimeOffset UpdatedAt, bool Banned, string? ErrataText,
    string? VariantOf, IReadOnlyList<CardVersion> Versions,
    DateOnly? LegalFrom, string Legality);

// ── Kaart-dossier (#127) ─────────────────────────────────────────────────

public record CardDossierRuling(
    long Id, string? Question, string Text, string? Provenance,
    DateTimeOffset Date, IReadOnlyList<RulingsSectionRef> Sections,
    // "Waar besloten" (#166) — URL of vrije citatie, getoond als bewijs.
    string? SourceRef = null);

public record CardDossierClaim(
    long Id, string Statement, int Corroboration, double TrustScore,
    string OfficialStatus, string TrustLabel, IReadOnlyList<RulingsSource> Sources);

public record CardDossierRelation(
    string OtherRef, string? OtherName, string Kind, string Explanation,
    string Status, double Trust, string Richting);

public record CardBanEvent(
    string Kind, string Format, DateOnly? EffectiveFrom,
    string SourceUrl, DateTimeOffset DetectedAt);

/// <summary>Een mede-gespeelde kaart binnen de decks die de dossierkaart al
/// bevatten (co-occurrence, #15 golf 1 spoor B) — DeckCount is het aantal
/// van die decks waarin deze andere kaart óók voorkomt.</summary>
public record CardCoOccurrence(string RiftboundId, string Name, int DeckCount);

/// <summary>Deck-gebruikssignaal (#15 golf 1 spoor B): het aandeel van de
/// recente Piltover Archive-decks (zie <see cref="CardDetailService.
/// RecentDeckWindow"/>) dat deze kaart speelt. RecentDeckCount is de
/// noemer — altijd meegegeven zodat "Percentage" nooit los van zijn basis
/// leest. ThinData markeert een te kleine noemer (bv. tijdens de lopende
/// backfill, #15 spoor 2): de UI toont dan absolute aantallen in plaats
/// van het percentage.</summary>
public record CardDeckPopularity(
    int DeckCount, int RecentDeckCount, double Percentage,
    double? AverageCopiesWhenPlayed, bool ThinData,
    IReadOnlyList<CardCoOccurrence> TopCoPlayed);

/// <summary>Het dossier boven op de kaartfeiten: geverifieerde rulings over
/// de kaart, geaccepteerde community-claims (met trust en bewijs),
/// dynamische brein-relaties (#116), de ban-historie en het
/// deck-gebruikssignaal (#15). Uitbreidpunt: bekende misvattingen (#125)
/// sluiten hier aan zodra dat veld bestaat.</summary>
public record CardDossier(
    IReadOnlyList<CardDossierRuling> Rulings,
    IReadOnlyList<CardDossierClaim> Claims,
    IReadOnlyList<CardDossierRelation> Relations,
    IReadOnlyList<CardBanEvent> BanHistory,
    CardDeckPopularity DeckPopularity);

/// <summary>Detail-opbouw van de kaartpagina (#59, uit het endpoint):
/// ban-status per variantgroep, laatste erratum, alle printings en de
/// canonical-fallback voor mining-resultaten.</summary>
public class CardDetailService(RbRulesDbContext db, CardResolver resolver)
{
    public async Task<CardDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        // Zonder embedding-vector (#43): de detailpagina toont kaartfeiten,
        // niet de 1024 floats.
        var c = await db.Cards.AsNoTracking()
            .Where(x => x.RiftboundId == id)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct);
        if (c is null) return null;

        // Ban geldt voor de hele variantgroep (#44) — een ban op één
        // printing is op alle printings zichtbaar.
        var bannedGroups = await BanLookup.BannedCanonicalIdsAsync(db, ct);
        var banned = BanLookup.IsBanned(bannedGroups, c);

        var errataForCard = await ErrataForCardAsync(id, ct);
        var erratum = errataForCard.Count == 0 ? null : errataForCard[0].NewText;

        // Alle printings van deze kaart (alt-art/showcase/promo/herdruk).
        var canonicalId = CardText.CanonicalId(c);
        var versions = await db.Cards
            .Where(x => x.RiftboundId != c.RiftboundId &&
                        (x.RiftboundId == canonicalId || x.VariantOf == canonicalId))
            .OrderBy(x => x.RiftboundId)
            .Select(x => new CardVersion(
                x.RiftboundId, x.SetId, x.SetLabel, x.Rarity, x.CollectorNumber, x.ImageUrl))
            .ToListAsync(ct);

        // Mining draait alleen op canonieke printings — varianten tonen de
        // analyse van hun canonieke kaart (zelfde tekst, zelfde spel-gedrag).
        var canonical = await resolver.CanonicalAsync(c, ct);

        // Set-legaliteit (#22): status afgeleid van de releasedatum van de set.
        var set = c.SetId is null ? null : await db.CardSets.FindAsync([c.SetId], ct);

        return new CardDetail(
            c.RiftboundId, c.Name, c.Type, c.Supertype, c.Rarity, c.Domains,
            c.Energy, c.Might, c.Power, c.SetId, c.SetLabel, c.CollectorNumber,
            c.TextPlain, c.ImageUrl, c.Tags,
            c.Mechanics ?? canonical.Mechanics,
            c.Triggers ?? canonical.Triggers,
            c.Effects ?? canonical.Effects,
            c.UpdatedAt, banned, erratum, c.VariantOf, versions,
            set?.PublishedOn,
            SetLegality.Key(SetLegality.StatusFor(
                set?.PublishedOn, DateOnly.FromDateTime(DateTime.UtcNow))));
    }

    /// <summary>Regels & errata die bij een kaart horen (voor de kaartpagina):
    /// regelsecties semantisch dichtstbij de kaart-embedding; varianten lenen
    /// de embedding van hun canonieke printing (CardResolver). Null als de
    /// kaart niet bestaat.</summary>
    public async Task<CardRuleLinks?> RulesAsync(string id, CancellationToken ct = default)
    {
        var card = await db.Cards.FindAsync([id], ct);
        if (card is null) return null;

        var errata = await ErrataForCardAsync(id, ct);

        var embeddingSource = await resolver.EmbeddingAnchorAsync(card, ct);
        IReadOnlyList<CardRelevantRule> relevantRules = [];
        if (embeddingSource.Embedding is not null)
        {
            var anchor = embeddingSource.Embedding;
            relevantRules = await db.RuleChunks
                .Where(c => c.Embedding != null && c.SectionCode != null)
                .OrderBy(c => c.Embedding!.CosineDistance(anchor))
                .Take(3)
                .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new CardRelevantRule(
                    c.SectionCode,
                    c.Text.Substring(0, Math.Min(c.Text.Length, 260)),
                    s.Name,
                    s.Url))
                .ToListAsync(ct);
        }

        return new(errata, relevantRules);
    }

    /// <summary>Errata voor een kaart, op temporele precedentie (#168): een
    /// DETERMINISTISCHE, totale sortering — hoogste TrustTier van de bron,
    /// dan nieuwste EffectiveFrom, dan Source.Rank (bron-voorkeur), dan
    /// Erratum.Id als stabiele finale sleutel (<see cref="Precedence.
    /// CompareStable"/>). Bewust NIET op DetectedAt: die reset bij elke
    /// errata-sync (BanErrataSyncService herbouwt de rijen), dus als tie-break
    /// zou hij de "nu geldig"-keuze run-to-run laten flippen zonder echte
    /// inhoudswijziging (review-fix). Het eerste item is de tekst die NU geldt;
    /// latere items zijn candidate-achterhaald, nog altijd zichtbaar. Twee
    /// aparte, eenvoudige query's (geen LEFT JOIN-expressie) — bewezen
    /// vertaalbaar, Errata↔Source is toch al een losse URL-koppeling.</summary>
    private async Task<List<CardErratumRef>> ErrataForCardAsync(string id, CancellationToken ct)
    {
        var rows = await db.Errata.AsNoTracking()
            .Where(e => e.CardRiftboundId == id)
            .OrderBy(e => e.Id) // deterministische basisvolgorde vóór de totale sort
            .Select(e => new { e.Id, e.NewText, e.SourceUrl, e.DetectedAt, e.EffectiveFrom })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var urls = rows.Select(r => r.SourceUrl).Distinct().ToList();
        var sourceByUrl = await db.Sources.AsNoTracking()
            .Where(s => urls.Contains(s.Url))
            .Select(s => new { s.Url, s.TrustTier, s.Rank })
            .ToDictionaryAsync(s => s.Url, s => (Tier: s.TrustTier, s.Rank), ct);

        var ranked = rows
            .Select(r =>
            {
                var src = sourceByUrl.GetValueOrDefault(r.SourceUrl, (Tier: Precedence.UnknownTier, Rank: 0));
                return (
                    Ref: new CardErratumRef(r.NewText, r.SourceUrl, r.DetectedAt, r.EffectiveFrom),
                    r.Id, src.Tier, src.Rank);
            })
            .ToList();
        // Winnaar (NU geldend) eerst: CompareStable geeft >0 als a voorrang
        // heeft, Sort wil dan a vóór b (negatief) — dus negeren.
        ranked.Sort((a, b) => -Precedence.CompareStable(
            a.Tier, a.Ref.EffectiveFrom, a.Rank, a.Id,
            b.Tier, b.Ref.EffectiveFrom, b.Rank, b.Id));
        return [.. ranked.Select(x => x.Ref)];
    }

    private const int DossierRulings = 10;
    private const int DossierClaims = 10;
    private const int DossierRelations = 12;

    // ── Deck-gebruikssignaal (#15 golf 1 spoor B) ───────────────────────

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

    /// <summary>Aandeel van de recente decks dat deze kaart speelt (#15).
    /// canonicalId is altijd al de canonieke groeps-id (CardText.CanonicalId)
    /// — varianten en de basisprinting delen hetzelfde signaal, net als de
    /// rest van het dossier.</summary>
    private async Task<CardDeckPopularity> DeckPopularityAsync(string canonicalId, CancellationToken ct)
    {
        // Zelfde "recentst"-maat als het beheeroverzicht (AdminOverviewService.
        // DecksAsync): PA's eigen updatedAt, met onze FetchedAt als terugval
        // voor de zeldzame pagina zonder datum — consistent "recent" door de
        // hele bank heen, in plaats van een tweede definitie.
        var recentDeckIds = await db.Decks.AsNoTracking()
            .OrderByDescending(d => d.PaUpdatedAt ?? d.FetchedAt)
            .ThenBy(d => d.Id)
            .Take(RecentDeckWindow)
            .Select(d => d.Id)
            .ToListAsync(ct);
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

    /// <summary>Kaart-dossier (#127): rulings, claims, relaties en
    /// ban-historie voor de kaartpagina — allemaal bestaande data, alleen
    /// geprojecteerd (geen embeddings, geen LLM). Varianten tonen het dossier
    /// van hun canonieke printing (#57). Null als de kaart niet bestaat.</summary>
    public async Task<CardDossier?> DossierAsync(string id, CancellationToken ct = default)
    {
        var card = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == id)
            .WithoutEmbedding()
            .FirstOrDefaultAsync(ct);
        if (card is null) return null;
        var canonical = await resolver.CanonicalAsync(card, ct);

        // Alle printings van de groep: bans en kaart-scoped rulings kunnen aan
        // elke printing hangen, het dossier toont ze allemaal.
        var canonicalId = CardText.CanonicalId(card);
        var groupIds = await db.Cards.AsNoTracking()
            .Where(c => c.RiftboundId == canonicalId || c.VariantOf == canonicalId)
            .Select(c => c.RiftboundId)
            .ToListAsync(ct);
        if (groupIds.Count == 0) groupIds = [card.RiftboundId];
        // Vergelijkingsvormen vóór de query's bepalen: eigen methodes horen
        // niet in expression trees (CONVENTIONS: bewezen vertaalbaar LINQ).
        var name = canonical.Name;
        var lowerName = name.ToLowerInvariant();
        var nameExact = EscapeLike(name);
        var namePattern = "%" + nameExact + "%";

        // Geverifieerde rulings over deze kaart: expliciet kaart-scoped op
        // ref/naam, plus rulings die de kaart bij naam noemen in vraag of
        // tekst (de web-feedback is answer-scoped maar gaat vaak over één
        // kaart — dat is precies het dossier-materiaal).
        var rulingRows = await db.Corrections.AsNoTracking()
            .Where(c => c.Status == "verified")
            .Where(c =>
                (c.Scope == "card" && (groupIds.Contains(c.Ref) ||
                    EF.Functions.ILike(c.Ref, nameExact, "\\"))) ||
                EF.Functions.ILike(c.Text, namePattern, "\\") ||
                (c.Question != null && EF.Functions.ILike(c.Question, namePattern, "\\")))
            .OrderByDescending(c => c.VerifiedAt ?? c.CreatedAt)
            .Take(DossierRulings)
            .Select(c => new { c.Id, c.Question, c.Text, c.Provenance, c.SourceRef, c.CreatedAt, c.VerifiedAt })
            .ToListAsync(ct);

        // §-verwijzingen in de rulings klikbaar maken (zelfde extractor als
        // de rulings-databank) — alleen opbouwen als er iets te linken valt.
        var rulings = new List<CardDossierRuling>();
        if (rulingRows.Count > 0)
        {
            var pairs = await db.RuleChunks.AsNoTracking()
                .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
                .Select(c => new { c.SourceId, c.SectionCode })
                .Distinct()
                .OrderBy(p => p.SourceId)
                .ToListAsync(ct);
            var extractor = ChangeAffectsMapper.Create(
                [], pairs.Select(p => (p.SourceId, p.SectionCode!)));
            rulings = [.. rulingRows.Select(r => new CardDossierRuling(
                r.Id, r.Question, r.Text, r.Provenance, r.VerifiedAt ?? r.CreatedAt,
                [.. extractor.Resolve("core-rule", $"{r.Question}\n{r.Text}")
                    .Take(6)
                    .Select(s =>
                    {
                        var split = s.Key.IndexOf('/');
                        return new RulingsSectionRef(s.Key[..split], s.Key[(split + 1)..]);
                    })],
                r.SourceRef))];
        }

        // Geaccepteerde community-claims over de kaart: claims dragen de
        // kaartnaam als topic-ref (ClaimTopicMapper), dus we matchen op naam.
        var claimRows = await db.Claims.AsNoTracking()
            .Where(c => c.Status == "accepted" && c.TopicType == "card" &&
                c.TopicRef.ToLower() == lowerName)
            .OrderByDescending(c => c.TrustScore)
            .Take(DossierClaims)
            .Select(c => new
            {
                c.Id, c.Statement, c.Corroboration, c.TrustScore, c.Status, c.OfficialStatus,
            })
            .ToListAsync(ct);
        var claimIds = claimRows.Select(c => c.Id).ToList();
        var sourcesByClaim = (await db.ClaimSources.AsNoTracking()
                .Where(s => claimIds.Contains(s.ClaimId))
                .Join(db.Sources, cs => cs.SourceId, s => s.Id, (cs, s) => new
                {
                    cs.ClaimId, s.Name, cs.Url, cs.QuoteExcerpt, s.TrustTier,
                })
                .ToListAsync(ct))
            .GroupBy(s => s.ClaimId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(s => s.TrustTier)
                .Select(s => new RulingsSource(s.Name, s.Url, s.QuoteExcerpt, s.TrustTier))
                .ToList());
        var claims = claimRows
            .Select(c => new CardDossierClaim(
                c.Id, c.Statement, c.Corroboration, c.TrustScore, c.OfficialStatus,
                ClaimTrust.Label(c.Corroboration, c.TrustScore, c.Status, c.OfficialStatus),
                sourcesByClaim.GetValueOrDefault(c.Id, [])))
            .ToList();

        // Dynamische relaties (#116): dezelfde reviewpoort als de
        // graph-projectie — rejected nooit, unreviewed alleen met een
        // geaccepteerd kind, en de status blijft zichtbaar als label.
        var cardRef = BrainRef.Card(canonical.RiftboundId).Format();
        var relationRows = await db.Relations.AsNoTracking()
            .Where(r => (r.FromRef == cardRef || r.ToRef == cardRef) && r.Status != "rejected")
            .OrderBy(r => r.Status == "accepted" ? 0 : 1)
            .ThenByDescending(r => r.Trust)
            .Take(DossierRelations * 2) // ruim: de kind-poort hieronder filtert nog
            .Select(r => new { r.FromRef, r.ToRef, r.Kind, r.Explanation, r.Trust, r.Status })
            .ToListAsync(ct);
        var acceptedKinds = RelationProjection.AcceptedKindSet(
            await db.RelationKinds.AsNoTracking()
                .Where(k => k.Status == "accepted")
                .Select(k => k.Kind)
                .ToListAsync(ct));
        var projected = relationRows
            .Where(r => RelationProjection.ShouldProject(r.Status, r.Kind, acceptedKinds))
            .Take(DossierRelations)
            .ToList();
        var relations = new List<CardDossierRelation>();
        if (projected.Count > 0)
        {
            var otherRefs = projected
                .Select(r => r.FromRef == cardRef ? r.ToRef : r.FromRef)
                .Distinct()
                .ToList();
            var names = await RelationDisplayNamesAsync(otherRefs, ct);
            relations = [.. projected.Select(r =>
            {
                var richting = r.FromRef == cardRef ? "uit" : "in";
                var other = richting == "uit" ? r.ToRef : r.FromRef;
                return new CardDossierRelation(
                    other, names.GetValueOrDefault(other), r.Kind, r.Explanation,
                    r.Status, r.Trust, richting);
            })];
        }

        // Ban-historie: alle entries die (een printing van) deze kaart raken,
        // nieuwste eerst — de levende geschiedenis naast de actuele status.
        var banHistory = await db.BanEntries.AsNoTracking()
            .Where(b =>
                (b.CardRiftboundId != null && groupIds.Contains(b.CardRiftboundId)) ||
                b.Name.ToLower() == lowerName)
            .OrderByDescending(b => b.DetectedAt)
            .Select(b => new CardBanEvent(
                b.Kind, b.Format, b.EffectiveFrom, b.SourceUrl, b.DetectedAt))
            .ToListAsync(ct);

        var deckPopularity = await DeckPopularityAsync(canonicalId, ct);

        return new(rulings, claims, relations, banHistory, deckPopularity);
    }

    /// <summary>Weergavenamen voor de overkant van een relatie: kaarten en
    /// concepten hebben een echte naam/titel in Postgres; secties en
    /// mechanieken zijn hun key al ("§ 101.2", "Deflect").</summary>
    private async Task<Dictionary<string, string>> RelationDisplayNamesAsync(
        List<string> refs, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var cardIds = new List<string>();
        var conceptKeys = new List<string>();
        foreach (var text in refs)
        {
            if (!BrainRef.TryParse(text, out var parsed)) continue;
            switch (parsed.Kind)
            {
                case BrainRefKind.Card: cardIds.Add(parsed.Key); break;
                case BrainRefKind.Concept: conceptKeys.Add(parsed.Key); break;
                case BrainRefKind.Section:
                    var split = parsed.Key.IndexOf('/');
                    result[text] = $"§ {(split >= 0 ? parsed.Key[(split + 1)..] : parsed.Key)}";
                    break;
                default: result[text] = parsed.Key; break;
            }
        }
        if (cardIds.Count > 0)
        {
            var names = await db.Cards.AsNoTracking()
                .Where(c => cardIds.Contains(c.RiftboundId))
                .Select(c => new { c.RiftboundId, c.Name })
                .ToListAsync(ct);
            foreach (var n in names) result[BrainRef.Card(n.RiftboundId).Format()] = n.Name;
        }
        if (conceptKeys.Count > 0)
        {
            var titles = await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer" && conceptKeys.Contains(k.Topic))
                .Select(k => new { k.Topic, k.Title })
                .ToListAsync(ct);
            foreach (var t in titles) result[BrainRef.Concept(t.Topic).Format()] = t.Title;
        }
        return result;
    }

    /// <summary>LIKE/ILIKE-metatekens onschadelijk maken (escape-teken is
    /// backslash — zelfde afspraak als BrainService).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
