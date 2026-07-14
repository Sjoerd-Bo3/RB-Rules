using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

// Response-records van de rulings-databank (#127). Zelfde afspraak als bij
// BrainService: de service bouwt de responses en Infrastructure mag niet naar
// Api verwijzen, dus de records leven hier.

public record RulingsSectionRef(string SourceId, string Code);
public record RulingsSource(string Name, string? Url, string? Quote, short TrustTier);

/// <summary>Eén item in de publieke rulings-databank: een geverifieerde
/// ruling (kind=ruling) of een officieel bevestigde community-claim
/// (kind=claim), met de volledige bewijsketen: trust-label, bron/provenance,
/// citaat per bron en klikbare §-verwijzingen uit de tekst.</summary>
public record RulingsItem(
    string Ref, string Kind, string Topic, string? TopicRef, string? CardId,
    string? Question, string Text, string TrustLabel, string? Provenance,
    DateTimeOffset Date, double? Score,
    IReadOnlyList<RulingsSectionRef> Sections,
    IReadOnlyList<RulingsSource> Sources,
    // "Waar besloten" (#166) — alleen op rulings (Correction), niet op claims.
    string? SourceRef = null);

public record RulingsResponse(
    IReadOnlyList<RulingsItem> Items, int Total, int Page, int PageSize, bool Degraded);

/// <summary>Publieke rulings-databank (#127): één doorzoekbare collectie van
/// geverifieerde rulings (correction, status=verified) en officieel
/// bevestigde community-claims (official_status=confirmed, niet weerlegd).
/// Zoeken volgt het #72-patroon: één embed-call (best-effort, #100 — bij
/// Ollama-uitval degradatie naar alleen-FTS), vector + full-text per soort,
/// RRF-fusie. Bladeren zonder zoekterm is nieuwste-eerst met paging.
/// Uitbreidpunt: bekende misvattingen (#125) sluiten hier als derde soort aan
/// zodra dat veld bestaat. Geen LLM-calls — alleen DB-reads.</summary>
public class RulingsService(
    RbRulesDbContext db, EmbeddingService embeddings, ILogger<RulingsService> logger)
{
    public const int PageSize = 20;
    private const int SearchTake = 30;
    private const int MaxSectionRefs = 6;

    /// <summary>Zelfde cap als RuleSearchService/BrainService: een publieke
    /// zoekvraag mag nooit minutenlang aan een koude Ollama hangen.</summary>
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromSeconds(8);

    public async Task<RulingsResponse> QueryAsync(
        string? q, string? topic, int page, CancellationToken ct = default)
    {
        return string.IsNullOrWhiteSpace(q)
            ? await BrowseAsync(topic, Math.Max(1, page), ct)
            : await SearchAsync(q.Trim(), topic, ct);
    }

    // ── bladeren: nieuwste eerst, met paging ───────────────────────────

    private async Task<RulingsResponse> BrowseAsync(
        string? topic, int page, CancellationToken ct)
    {
        var corrections = FilterCorrections(topic);
        var claims = FilterClaims(topic);

        var total = await corrections.CountAsync(ct) + await claims.CountAsync(ct);

        // Beide soorten hebben hun eigen tijdlijn; voor een gemengde pagina
        // halen we van elk het venster tot en met deze pagina op en mengen
        // in-memory op datum. De aantallen zijn klein (honderden) — dit blijft
        // twee goedkope, geïndexeerde queries per pagina.
        var window = page * PageSize;
        var correctionIds = await corrections
            .OrderByDescending(c => c.VerifiedAt ?? c.CreatedAt)
            .Take(window)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var claimIds = await claims
            .OrderByDescending(c => c.LastSeen)
            .Take(window)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var items = await BuildItemsAsync(correctionIds, claimIds, scores: null, ct);
        var pageItems = items
            .OrderByDescending(i => i.Date)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToList();
        return new(pageItems, total, page, PageSize, Degraded: false);
    }

    // ── zoeken: hybride per soort, RRF-fusie (#72-patroon) ─────────────

    private async Task<RulingsResponse> SearchAsync(
        string query, string? topic, CancellationToken ct)
    {
        // Eén embed-call voor beide soorten — best-effort: bij uitval blijft
        // qv null en zoeken beide soorten alleen met full-text.
        Vector? qv = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EmbedTimeout);
            qv = await embeddings.EmbedOneAsync(query, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // de aanvrager zelf haakte af — niet maskeren
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Embedding voor rulings-zoek mislukt — degradatie naar alleen-FTS");
        }

        var fetch = SearchTake * 2;

        // Geverifieerde rulings: vector + FTS over vraag+tekst.
        List<long> correctionVector = [];
        if (qv is not null)
            correctionVector = await FilterCorrections(topic)
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var correctionText = await FilterCorrections(topic)
            .Where(c => EF.Functions.ToTsVector("english", (c.Question ?? "") + " " + c.Text)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", (c.Question ?? "") + " " + c.Text)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var rankedCorrections = RrfFusion.FuseScored(
            [correctionVector, correctionText], id => id, SearchTake);

        // Officieel bevestigde claims: vector + FTS over het statement.
        List<long> claimVector = [];
        if (qv is not null)
            claimVector = await FilterClaims(topic)
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qv))
                .Take(fetch)
                .Select(c => c.Id)
                .ToListAsync(ct);
        var claimText = await FilterClaims(topic)
            .Where(c => EF.Functions.ToTsVector("english", c.Statement)
                .Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(c => EF.Functions.ToTsVector("english", c.Statement)
                .Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(fetch)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var rankedClaims = RrfFusion.FuseScored([claimVector, claimText], id => id, SearchTake);

        var scores = new Dictionary<string, double>();
        foreach (var (id, score) in rankedCorrections) scores[$"ruling:{id}"] = score;
        foreach (var (id, score) in rankedClaims) scores[$"claim:{id}"] = score;

        var items = await BuildItemsAsync(
            [.. rankedCorrections.Select(r => r.Key)],
            [.. rankedClaims.Select(r => r.Key)],
            scores, ct);

        // Gemengd op score; bij gelijke score wint de ruling (gezaghebbend
        // boven community — de kennispiramide, ook in de sortering).
        var ordered = items
            .OrderByDescending(i => i.Score ?? 0)
            .ThenBy(i => i.Kind == "ruling" ? 0 : 1)
            .Take(SearchTake)
            .ToList();
        return new(ordered, ordered.Count, 1, PageSize, Degraded: qv is null);
    }

    // ── filters ────────────────────────────────────────────────────────

    /// <summary>Alleen geverifieerde rulings zijn publiek; het topic-filter
    /// vertaalt naar de opgeslagen scope (RulingsTopics is de ene bron van
    /// die vertaling). Mechanic/concept bestaan niet als correction-scope,
    /// dus die filters laten alleen claims over.</summary>
    private IQueryable<Correction> FilterCorrections(string? topic)
    {
        var query = db.Corrections.AsNoTracking().Where(c => c.Status == "verified");
        return topic switch
        {
            null => query,
            "card" => query.Where(c => c.Scope == "card"),
            "section" => query.Where(c => c.Scope == "rule_section"),
            "answer" => query.Where(c => c.Scope != "card" && c.Scope != "rule_section"),
            _ => query.Where(c => false),
        };
    }

    /// <summary>Alleen officieel bevestigde claims die niet weerlegd of
    /// vervangen zijn: de databank toont uitsluitend geldige kennis —
    /// weerlegde claims horen bij het contradictions-koppelvlak, niet hier.</summary>
    private IQueryable<Claim> FilterClaims(string? topic)
    {
        var query = db.Claims.AsNoTracking().Where(c =>
            c.OfficialStatus == "confirmed" &&
            c.Status != "rejected" && c.Status != "superseded");
        return topic switch
        {
            null => query,
            "answer" => query.Where(c => false),
            // "concept" vangt ook onbekende topic-types (RulingsTopics).
            "concept" => query.Where(c =>
                c.TopicType != "card" && c.TopicType != "mechanic" && c.TopicType != "section"),
            _ => query.Where(c => c.TopicType == topic),
        };
    }

    // ── projectie ──────────────────────────────────────────────────────

    private async Task<List<RulingsItem>> BuildItemsAsync(
        List<long> correctionIds, List<long> claimIds,
        Dictionary<string, double>? scores, CancellationToken ct)
    {
        var items = new List<RulingsItem>();
        if (correctionIds.Count == 0 && claimIds.Count == 0) return items;

        var sectionExtractor = await SectionExtractorAsync(ct);

        if (correctionIds.Count > 0)
        {
            var rows = await db.Corrections.AsNoTracking()
                .Where(c => correctionIds.Contains(c.Id))
                .Select(c => new
                {
                    c.Id, c.Scope, c.Ref, c.Text, c.Question, c.Provenance, c.SourceRef,
                    c.CreatedAt, c.VerifiedAt,
                })
                .ToListAsync(ct);

            // Kaart-scoped rulings: Ref kan een riftbound-id óf kaartnaam
            // zijn — allebei naar een klikbare kaartpagina resolven.
            var cardScoped = rows
                .Where(r => RulingsTopics.FromCorrectionScope(r.Scope) == "card")
                .Select(r => r.Ref)
                .ToList();
            var cardIds = await ResolveCardIdsAsync(cardScoped, ct);

            foreach (var r in rows)
            {
                var topic = RulingsTopics.FromCorrectionScope(r.Scope);
                var itemRef = BrainRef.Ruling(r.Id).Format();
                items.Add(new(
                    itemRef, "ruling", topic,
                    // "up"/"down" (antwoord-feedback) is administratie, geen onderwerp.
                    topic == "answer" ? null : r.Ref,
                    topic == "card" ? cardIds.GetValueOrDefault(NormKey(r.Ref)) : null,
                    r.Question, r.Text,
                    "geverifieerde ruling (gezaghebbend)",
                    r.Provenance,
                    r.VerifiedAt ?? r.CreatedAt,
                    scores?.GetValueOrDefault(itemRef),
                    ExtractSections(sectionExtractor, $"{r.Question}\n{r.Text}"),
                    Sources: [], r.SourceRef));
            }
        }

        if (claimIds.Count > 0)
        {
            var rows = await db.Claims.AsNoTracking()
                .Where(c => claimIds.Contains(c.Id))
                .Select(c => new
                {
                    c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                    c.TrustScore, c.Status, c.OfficialStatus, c.LastSeen,
                })
                .ToListAsync(ct);

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

            var cardTopics = rows
                .Where(r => RulingsTopics.FromClaimTopicType(r.TopicType) == "card")
                .Select(r => r.TopicRef)
                .ToList();
            var cardIds = await ResolveCardIdsAsync(cardTopics, ct);

            foreach (var r in rows)
            {
                var topic = RulingsTopics.FromClaimTopicType(r.TopicType);
                var itemRef = BrainRef.Claim(r.Id).Format();
                items.Add(new(
                    itemRef, "claim", topic, r.TopicRef,
                    topic == "card" ? cardIds.GetValueOrDefault(NormKey(r.TopicRef)) : null,
                    Question: null, r.Statement,
                    ClaimTrust.Label(r.Corroboration, r.TrustScore, r.Status, r.OfficialStatus),
                    Provenance: null,
                    r.LastSeen,
                    scores?.GetValueOrDefault(itemRef),
                    ExtractSections(sectionExtractor, r.Statement),
                    sourcesByClaim.GetValueOrDefault(r.Id, [])));
            }
        }

        return items;
    }

    /// <summary>§-verwijzingen in rulings/claims klikbaar maken: hergebruik
    /// van de pure ChangeAffectsMapper (alleen de sectiekant — geen kaarten),
    /// gevoed met de echte §-codes zodat "40 cards" nooit §40 wordt.</summary>
    private async Task<ChangeAffectsMapper> SectionExtractorAsync(CancellationToken ct)
    {
        var pairs = await db.RuleChunks.AsNoTracking()
            .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
            .Select(c => new { c.SourceId, c.SectionCode })
            .Distinct()
            .OrderBy(p => p.SourceId) // voorkeursvolgorde: eerste bron wint bij dubbele codes
            .ToListAsync(ct);
        return ChangeAffectsMapper.Create([], pairs.Select(p => (p.SourceId, p.SectionCode!)));
    }

    private static List<RulingsSectionRef> ExtractSections(
        ChangeAffectsMapper extractor, string text) =>
        [.. extractor.Resolve("core-rule", text)
            .Take(MaxSectionRefs)
            .Select(r =>
            {
                // Section-key is "<sourceId>/<code>" (BrainRef.Section).
                var split = r.Key.IndexOf('/');
                return new RulingsSectionRef(r.Key[..split], r.Key[(split + 1)..]);
            })];

    /// <summary>Kaartverwijzingen (id of naam, hoofdletter-ongevoelig) naar de
    /// canonieke printing resolven, in één batch. Sleutel = genormaliseerde
    /// invoerwaarde, waarde = canoniek riftbound-id.</summary>
    private async Task<Dictionary<string, string>> ResolveCardIdsAsync(
        IEnumerable<string?> refs, CancellationToken ct)
    {
        var keys = refs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => NormKey(r!))
            .Distinct()
            .ToList();
        if (keys.Count == 0) return [];

        var matches = await db.Cards.AsNoTracking()
            .Where(c => keys.Contains(c.RiftboundId.ToLower()) || keys.Contains(c.Name.ToLower()))
            .Select(c => new { c.RiftboundId, c.Name, c.VariantOf })
            .ToListAsync(ct);

        var result = new Dictionary<string, string>();
        foreach (var m in matches)
        {
            var canonical = m.VariantOf ?? m.RiftboundId;
            result.TryAdd(NormKey(m.RiftboundId), canonical);
            // Canonieke printings winnen van varianten met dezelfde naam.
            if (m.VariantOf is null) result[NormKey(m.Name)] = canonical;
            else result.TryAdd(NormKey(m.Name), canonical);
        }
        return result;
    }

    private static string NormKey(string? s) => (s ?? "").Trim().ToLowerInvariant();
}
