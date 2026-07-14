using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record Paged<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);

public record CardOverviewItem(
    string RiftboundId, string Name, string? SetLabel, string? Rarity, string? Type,
    string? VariantOf, bool Embedded, string[]? Mechanics, DateTimeOffset UpdatedAt);

public record RuleChunkSourceCount(string SourceId, int Count);
public record RuleChunkOverviewItem(
    long Id, string SourceId, string? SectionCode, int? Page, int ChunkIndex, string Snippet);
public record RuleChunkOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<RuleChunkSourceCount> Sources, IReadOnlyList<RuleChunkOverviewItem> Items);

public record BanOverviewItem(
    long Id, string Name, string? CardRiftboundId, string Kind, string Format,
    DateOnly? EffectiveFrom, string SourceUrl, DateTimeOffset DetectedAt);

/// <summary>SupersededByErratumId (#168): gevuld wanneer een ander errata-item
/// over dezelfde kaart een hogere temporele precedentie heeft (gelijke of
/// hogere TrustTier, nieuwere EffectiveFrom) — een kandidaat-signaal voor de
/// beheerder, puur berekend uit de bestaande data (geen eigen status-kolom,
/// geen automatische verwijdering; beide rijen blijven gewoon bestaan).</summary>
public record ErratumOverviewItem(
    long Id, string CardName, string? CardRiftboundId, string NewText,
    string SourceUrl, DateTimeOffset DetectedAt, DateOnly? EffectiveFrom,
    long? SupersededByErratumId);

public record InteractionOverviewItem(
    long Id, string Kind, string Explanation, string CardAId, string CardAName,
    string CardBId, string CardBName, DateTimeOffset DetectedAt);

public record ChangeOverviewItem(
    long Id, string SourceId, string SourceName, string ChangeType, string Severity,
    string? Summary, string? Meaning, DateTimeOffset DetectedAt);

/// <summary>UrlSafe (#184): server-side UrlGuard.Check op de bewijs-URL —
/// de UI rendert alleen een klikbare link als dit true is (sanitize vóór
/// {@html}, docs/CONVENTIONS.md); anders de kale naam/URL als platte tekst.
/// ClaimSource.Url is in de praktijk altijd Source.Url (al UrlGuard-gecheckt
/// bij bronregistratie), dus dit is een extra, goedkope verdedigingslaag.</summary>
public record ClaimSourceOverviewItem(
    string SourceId, string SourceName, string Url, bool UrlSafe, string? Quote, DateTimeOffset SeenAt);

/// <summary>Reviewqueue-item voor clarify-correcties (#184): SourceName
/// resolvet — waar mogelijk — de bron-naam via de clarify-mining-Provenance
/// ("clarify-mining:{sourceId}", de enige ontstaanswijze met een
/// resolveerbare Source-rij); SourceRefSafe is UrlGuard.Check op SourceRef
/// (zelfde patroon als ClaimSourceOverviewItem.UrlSafe) — de UI rendert
/// alleen een klikbare link als dit true is.</summary>
public record CorrectionOverviewItem(
    long Id, string Scope, string Ref, string Text, string? Question,
    string? Provenance, string? SourceRef, string? SourceName, bool SourceRefSafe,
    string Status, string? StatusReason, string? ReviewNote,
    DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt);
public record ClaimOverviewItem(
    long Id, string TopicType, string TopicRef, string Statement,
    int Corroboration, double TrustScore, string Status, string? StatusReason,
    string OfficialStatus, string? ReviewNote, DateTimeOffset? ArchivedAt,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    IReadOnlyList<ClaimSourceOverviewItem> Sources);
public record ClaimStatusCount(string Status, int Count);
public record ClaimOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<ClaimStatusCount> StatusCounts, int Archived,
    IReadOnlyList<ClaimOverviewItem> Items);

public record RelationOverviewItem(
    long Id, string FromRef, string? FromName, string ToRef, string? ToName,
    string Kind, string Explanation, string Provenance, double Trust,
    string Status, string? ReviewNote, DateTimeOffset? ArchivedAt, DateTimeOffset DetectedAt);
/// <summary>Bewijs bij een kandidaat-kind (#123): een voorbeeldvoorstel
/// dat het kind draagt, zodat reviewen geen gokken is.</summary>
public record RelationKindExample(
    string FromRef, string? FromName, string ToRef, string? ToName);
public record RelationKindOverviewItem(
    long Id, string Kind, string Status, int Occurrences,
    DateTimeOffset FirstSeen, DateTimeOffset? ReviewedAt,
    IReadOnlyList<RelationKindExample> Examples);
public record RelationStatusCount(string Status, int Count);
public record RelationOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<RelationStatusCount> StatusCounts, int Archived,
    IReadOnlyList<RelationKindOverviewItem> Kinds,
    IReadOnlyList<RelationOverviewItem> Items);

public record DeckOverviewItem(
    long Id, string PaId, string? Name, string[] Domains, int Cards, int UnknownCards,
    int Views, int Likes, string SourceUrl, DateTimeOffset? PaUpdatedAt,
    DateTimeOffset FetchedAt);

public record ProposalOverviewItem(
    long Id, string Url, string Name, string Type, string Motivation,
    string Status, DateTimeOffset FoundAt, DateTimeOffset? ReviewedAt);
public record ProposalStatusCount(string Status, int Count);
public record ProposalOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<ProposalStatusCount> StatusCounts, IReadOnlyList<ProposalOverviewItem> Items);

/// <summary>Bron-feed in het beheeroverzicht (#167): SourceCount is hoeveel
/// bronnen deze feed tot nu toe ontdekte (Source.FeedId) — alleen zinvol voor
/// AutoApprove-feeds; een niet-AutoApprove-feed levert altijd voorstellen,
/// dus 0 hier is geen alarmsignaal.</summary>
public record FeedOverviewItem(
    string Id, string Name, string Url, bool Enabled, bool AutoApprove,
    string? CategoryFilter, string Cadence, DateTimeOffset? LastChecked,
    int SourceCount);

public record UserOverviewItem(
    long Id, string Email, bool Blocked, int DailyQuota, int DailyPhotoQuota,
    int DailyAgenticQuota,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    int Questions, int Photos, int Cheap, int Hard, int Failed, int AvgDurationMs,
    long InputTokens, long OutputTokens);
/// <summary>Tokentotalen per antwoordpad in de gekozen periode (#121):
/// cheap/hard/agentic, over álle vragen (dus ook anonieme). Metrics zonder
/// usage (oude rb-ai) tellen als 0 tokens maar wel als vraag — de teller is
/// een ondergrens, geen verzonnen getal.</summary>
public record PathUsageItem(string Path, int Questions, long InputTokens, long OutputTokens);
public record UserOverview(
    int Total, int Page, int PageSize, string Period,
    int AnonQuestions, int AnonPhotos, IReadOnlyList<PathUsageItem> Paths,
    IReadOnlyList<UserOverviewItem> Items);

/// <summary>Vraag-trace in de beheerlijst (#40): alle route-metadata, maar
/// bewust zonder het antwoord en de gespreks-historie — die komen per trace
/// via het detail (#143), zodat de lijst-payload slank blijft.</summary>
public record AskTraceListItem(
    long Id, string Question, string? QuestionType, string? RewrittenQuery,
    string? SourceBias, bool MentionsCard, string? MechanicMatches,
    string? Sections, string? ContextCards, string? PrimerDocs,
    string? CommunityClaims, int VerifiedRulings, string? Model, bool HadImage,
    int DurationMs, string? PhaseTimings, bool Agentic, string? EscalatedBy,
    string? BrainSteps, bool Ok, DateTimeOffset CreatedAt);
public record AskTraceTurn(string Question, string Answer);
/// <summary>Trace-detail (#143): het definitieve antwoord en de eerdere
/// beurten van het gesprek — de metadata zit al in de lijst.</summary>
public record AskTraceDetail(
    long Id, string? Answer, IReadOnlyList<AskTraceTurn> History);

/// <summary>Set-dekking per set (#145): dekking uit de id's (SetCoverage) +
/// setmetadata uit card_set. MissingNumbers is de exacte lijst — de UI maakt
/// er een compacte reeksweergave van.</summary>
public record SetCoverageOverviewItem(
    string SetId, string Name, DateOnly? PublishedOn, DateTimeOffset? SyncedAt,
    int? BaseTotal, int Present, IReadOnlyList<int> MissingNumbers, int Variants,
    IReadOnlyList<SetTotalDeviation> TotalDeviations);
public record SetCoverageOverview(
    int Sets, int Incomplete, IReadOnlyList<SetCoverageOverviewItem> Items);

/// <summary>Benchmark-overzicht (#158): run-historie (voor run-over-run-
/// vergelijking) + het geselecteerde run-detail met per vraag het antwoord
/// naast gekozen vs. juiste optie. Score/tellingen komen ongewijzigd van
/// BenchmarkRun — geen herberekening in de UI-laag.</summary>
public record BenchmarkRunOverviewItem(
    long Id, string? Label, int QuestionCount, int KeyedCount, int CorrectCount,
    double? ScorePercent, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
public record BenchmarkResultOverviewItem(
    long Id, long QuestionId, string ExternalKey, string Category, string Question,
    IReadOnlyList<string> Options, int? CorrectIndex, string? Explanation,
    string Answer, int? ChosenIndex, bool? Correct,
    int DurationMs, long? InputTokens, long? OutputTokens);
public record BenchmarkRunDetail(
    BenchmarkRunOverviewItem Run, IReadOnlyList<BenchmarkResultOverviewItem> Results);

/// <summary>Model-sweep (#174): één (model, run_index)-rij binnen een sweep,
/// met de tijdsmaten die BenchmarkRun zelf niet vastlegt (totaal/gemiddeld
/// per vraag) — afgeleid uit BenchmarkResult zodat BenchmarkRun geen
/// gedupliceerde kolommen nodig heeft.</summary>
public record BenchmarkSweepRunItem(
    long RunId, int RunIndex, double? ScorePercent, int KeyedCount, int CorrectCount,
    int QuestionCount, long TotalDurationMs, double AvgDurationMs,
    long TotalInputTokens, long TotalOutputTokens);

/// <summary>Eén model binnen een sweep: beide runs naast elkaar + de
/// consistentie-vraag uit issue #174 ("scoren de 2 runs gelijk, of was de
/// eerste een toevalstreffer?") — true/false alleen als beide runs een
/// score hebben (allebei gekeyde vragen), anders null ("nog niet te zeggen").</summary>
public record BenchmarkSweepModelItem(
    string Model, IReadOnlyList<BenchmarkSweepRunItem> Runs, bool? Consistent);

/// <summary>Rij in de sweep-historie (verloop over tijd, #174) — SweepId is
/// de UTC-starttijd in ms (BenchmarkService.RunSweepAsync), dus meteen
/// sorteerbaar en om te zetten naar StartedAt zonder extra kolom.</summary>
public record BenchmarkSweepSummary(
    long SweepId, DateTimeOffset StartedAt, int ModelCount, int QuestionCount);

public record BenchmarkSweepDetail(
    long SweepId, DateTimeOffset StartedAt, IReadOnlyList<BenchmarkSweepModelItem> Models);

public record BenchmarkOverview(
    IReadOnlyList<BenchmarkRunOverviewItem> Runs, BenchmarkRunDetail? Selected,
    IReadOnlyList<BenchmarkSweepSummary> Sweeps, BenchmarkSweepDetail? SelectedSweep);

/// <summary>Tegel-overzichten voor beheer (#61): elke dashboard-tegel klikt door
/// naar de onderliggende lijst. Alleen reads — projecties zonder embeddings,
/// server-side gepagineerd waar lijsten groot zijn.</summary>
public class AdminOverviewService(RbRulesDbContext db)
{
    private const int PageSize = 60;

    /// <summary>Bovengrens tegen int-overflow in de Skip-berekening
    /// (page * 60 moet ruim binnen int.MaxValue blijven).</summary>
    private static int ClampPage(int page) => Math.Clamp(page, 1, 100_000);

    /// <summary>LIKE-metatekens in gebruikersinvoer escapen zodat "%"/"_" in
    /// een zoekterm letterlijk matchen (Npgsql-escape is backslash).</summary>
    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Kaartenlijst achter de tegels Kaarten/Geëmbed/Geanalyseerd.
    /// Mechanics onderscheidt de drie mining-toestanden: null = nog niet
    /// gemined, leeg = gemined zonder vondst, gevuld = vondsten.</summary>
    public async Task<Paged<CardOverviewItem>> CardsAsync(
        string? filter, string? q, int page)
    {
        page = ClampPage(page);
        var query = db.Cards.AsNoTracking();
        query = filter switch
        {
            "embedded" => query.Where(c => c.Embedding != null),
            "unembedded" => query.Where(c => c.Embedding == null),
            "mined" => query.Where(c => c.Mechanics != null),
            "unmined" => query.Where(c => c.Mechanics == null),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{EscapeLike(q)}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(c => c.Name).ThenBy(c => c.RiftboundId)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(c => new CardOverviewItem(
                c.RiftboundId, c.Name, c.SetLabel, c.Rarity, c.Type, c.VariantOf,
                c.Embedding != null, c.Mechanics, c.UpdatedAt))
            .ToListAsync();
        return new(total, page, PageSize, items);
    }

    /// <summary>Regelsecties per bron, met tellingen als bronfilter-chips.</summary>
    public async Task<RuleChunkOverview> RuleChunksAsync(string? sourceId, int page)
    {
        page = ClampPage(page);
        // Record-constructors vertalen niet binnen GroupBy — eerst anoniem, dan mappen.
        var sources = (await db.RuleChunks.AsNoTracking()
                .GroupBy(rc => rc.SourceId)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new RuleChunkSourceCount(s.Key, s.Count))
            .ToList();

        var query = db.RuleChunks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(sourceId))
            query = query.Where(rc => rc.SourceId == sourceId);

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(rc => rc.SourceId).ThenBy(rc => rc.ChunkIndex)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(rc => new RuleChunkOverviewItem(
                rc.Id, rc.SourceId, rc.SectionCode, rc.Page, rc.ChunkIndex,
                rc.Text.Length > 180 ? rc.Text.Substring(0, 180) + "…" : rc.Text))
            .ToListAsync();
        return new(total, page, PageSize, sources, items);
    }

    public async Task<IReadOnlyList<BanOverviewItem>> BansAsync() =>
        await db.BanEntries.AsNoTracking()
            .OrderByDescending(b => b.DetectedAt).ThenBy(b => b.Name)
            .Select(b => new BanOverviewItem(
                b.Id, b.Name, b.CardRiftboundId, b.Kind, b.Format,
                b.EffectiveFrom, b.SourceUrl, b.DetectedAt))
            .ToListAsync();

    /// <summary>Errata-overzicht (#61) mét supersede-signaal (#168): per kaart
    /// (CardRiftboundId, of anders de naam) met meer dan één errata-bron
    /// wint de hoogste TrustTier / nieuwste EffectiveFrom — de rest krijgt
    /// SupersededByErratumId als kandidaat-signaal, puur berekend, niets
    /// wordt verwijderd of overschreven.</summary>
    public async Task<IReadOnlyList<ErratumOverviewItem>> ErrataAsync()
    {
        var rows = await db.Errata.AsNoTracking()
            .OrderByDescending(e => e.DetectedAt).ThenBy(e => e.CardName).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.CardName, e.CardRiftboundId, e.NewText, e.SourceUrl,
                e.DetectedAt, e.EffectiveFrom,
            })
            .ToListAsync();
        if (rows.Count == 0) return [];

        var urls = rows.Select(r => r.SourceUrl).Distinct().ToList();
        var sourceByUrl = await db.Sources.AsNoTracking()
            .Where(s => urls.Contains(s.Url))
            .Select(s => new { s.Url, s.TrustTier, s.Rank })
            .ToDictionaryAsync(s => s.Url, s => (Tier: s.TrustTier, s.Rank));
        (short Tier, int Rank) SrcOf(string url) =>
            sourceByUrl.GetValueOrDefault(url, (Tier: Precedence.UnknownTier, Rank: 0));

        var supersededBy = new Dictionary<long, long>();
        foreach (var group in rows.GroupBy(r => r.CardRiftboundId ?? r.CardName.Trim().ToLowerInvariant()))
        {
            var distinctUrls = group.Select(r => r.SourceUrl).Distinct().Count();
            if (distinctUrls < 2) continue; // dezelfde bron levert nooit een supersede-kandidaat op

            // Zelfde deterministische totale orde als CardDetailService
            // (#168, review-fix): de winnaar hier moet exact de "nu geldig"-
            // errata op de kaartpagina zijn, anders wijst het beheer de
            // verkeerde rij als achterhaald aan.
            var winner = group.Aggregate((best, cur) =>
            {
                var b = SrcOf(best.SourceUrl);
                var c = SrcOf(cur.SourceUrl);
                return Precedence.CompareStable(
                    c.Tier, cur.EffectiveFrom, c.Rank, cur.Id,
                    b.Tier, best.EffectiveFrom, b.Rank, best.Id) > 0 ? cur : best;
            });
            foreach (var r in group.Where(r => r.Id != winner.Id)) supersededBy[r.Id] = winner.Id;
        }

        return [.. rows.Select(e => new ErratumOverviewItem(
            e.Id, e.CardName, e.CardRiftboundId, e.NewText, e.SourceUrl, e.DetectedAt,
            e.EffectiveFrom,
            supersededBy.TryGetValue(e.Id, out var winnerId) ? winnerId : null))];
    }

    /// <summary>Geverifieerde interacties met kaartnamen erbij (twee stappen:
    /// pagina ophalen, dan namen voor precies die kaarten — geen joins over alles).</summary>
    public async Task<Paged<InteractionOverviewItem>> InteractionsAsync(int page)
    {
        page = ClampPage(page);
        var total = await db.CardInteractions.CountAsync();
        var rows = await db.CardInteractions.AsNoTracking()
            .OrderByDescending(i => i.DetectedAt).ThenBy(i => i.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        var ids = rows.SelectMany(i => new[] { i.CardAId, i.CardBId }).Distinct().ToList();
        var names = await db.Cards.AsNoTracking()
            .Where(c => ids.Contains(c.RiftboundId))
            .Select(c => new { c.RiftboundId, c.Name })
            .ToDictionaryAsync(c => c.RiftboundId, c => c.Name);

        var items = rows.Select(i => new InteractionOverviewItem(
                i.Id, i.Kind, i.Explanation,
                i.CardAId, names.GetValueOrDefault(i.CardAId, i.CardAId),
                i.CardBId, names.GetValueOrDefault(i.CardBId, i.CardBId),
                i.DetectedAt))
            .ToList();
        return new(total, page, PageSize, items);
    }

    /// <summary>Claims-overzicht (#50): status-chips + per claim de bronnen
    /// (twee stappen, zelfde patroon als Interactions — geen joins over alles;
    /// embeddings blijven buiten de projectie). Default-weergave (#124): geen
    /// status = alleen te-reviewen, niet-gearchiveerd — het overzicht toont
    /// standaard wat aandacht vraagt; afgehandeld en archief zitten achter
    /// chips ("archived"/"all"), met unreviewed bovenaan in de alles-weergave.</summary>
    public async Task<ClaimOverview> ClaimsAsync(string? status, int page)
    {
        page = ClampPage(page);
        // Chip-tellingen: statussen over het niet-gearchiveerde deel, het
        // archief als eigen teller (gearchiveerd is een zicht-, geen status-laag).
        var statusCounts = (await db.Claims.AsNoTracking()
                .Where(c => c.ArchivedAt == null)
                .GroupBy(c => c.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new ClaimStatusCount(s.Key, s.Count))
            .ToList();
        var archived = await db.Claims.CountAsync(c => c.ArchivedAt != null);

        var query = db.Claims.AsNoTracking();
        query = string.IsNullOrWhiteSpace(status) switch
        {
            true => query.Where(c => c.Status == "unreviewed" && c.ArchivedAt == null),
            false when status == "all" => query,
            false when status == "archived" => query.Where(c => c.ArchivedAt != null),
            _ => query.Where(c => c.Status == status && c.ArchivedAt == null),
        };

        var total = await query.CountAsync();
        var rows = await query
            .OrderBy(c => c.Status == "unreviewed" && c.ArchivedAt == null ? 0 : 1)
            .ThenByDescending(c => c.LastSeen).ThenBy(c => c.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
                c.ReviewNote, c.ArchivedAt, c.FirstSeen, c.LastSeen,
            })
            .ToListAsync();

        var ids = rows.Select(c => c.Id).ToList();
        var sources = await db.ClaimSources.AsNoTracking()
            .Where(cs => ids.Contains(cs.ClaimId))
            .Join(db.Sources, cs => cs.SourceId, s => s.Id, (cs, s) => new
            {
                cs.ClaimId, cs.SourceId, s.Name, cs.Url, cs.QuoteExcerpt, cs.SeenAt,
            })
            .ToListAsync();
        var bySrc = sources
            .GroupBy(s => s.ClaimId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ClaimSourceOverviewItem>)
                [.. g.OrderBy(s => s.SeenAt).Select(s =>
                    new ClaimSourceOverviewItem(
                        s.SourceId, s.Name, s.Url, UrlGuard.Check(s.Url).Allowed, s.QuoteExcerpt, s.SeenAt))]);

        var items = rows.Select(c => new ClaimOverviewItem(
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
                c.ReviewNote, c.ArchivedAt, c.FirstSeen, c.LastSeen,
                bySrc.GetValueOrDefault(c.Id, [])))
            .ToList();
        return new(total, page, PageSize, statusCounts, archived, items);
    }

    /// <summary>Correcties-reviewqueue (#177, bron+link+opmerking #184): de
    /// laatste 200 (zelfde cap als de oorspronkelijke inline-query in
    /// AdminEndpoints). SourceName resolvet alleen voor clarify-mining-rijen
    /// (Provenance "clarify-mining:{sourceId}") — de enige ontstaanswijze met
    /// een Source-rij erachter; andere ontstaanswegen (chat-ruling,
    /// review-notitie-promotie) tonen alleen de kale SourceRef.</summary>
    public async Task<IReadOnlyList<CorrectionOverviewItem>> CorrectionsAsync()
    {
        var rows = await db.Corrections.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .Select(c => new
            {
                c.Id, c.Scope, c.Ref, c.Text, c.Question, c.Provenance,
                c.SourceRef, c.Status, c.StatusReason, c.ReviewNote,
                c.CreatedAt, c.VerifiedAt,
            })
            .ToListAsync();

        var prefix = ClarificationMiningService.ProvenancePrefix;
        var sourceIds = rows
            .Where(c => c.Provenance != null && c.Provenance.StartsWith(prefix))
            .Select(c => c.Provenance![prefix.Length..])
            .Distinct().ToList();
        Dictionary<string, string> namesById = sourceIds.Count == 0
            ? []
            : await db.Sources.AsNoTracking()
                .Where(s => sourceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        return [.. rows.Select(c =>
        {
            string? sourceName = null;
            if (c.Provenance is not null && c.Provenance.StartsWith(prefix))
                namesById.TryGetValue(c.Provenance[prefix.Length..], out sourceName);
            var safe = c.SourceRef is not null && UrlGuard.Check(c.SourceRef).Allowed;
            return new CorrectionOverviewItem(
                c.Id, c.Scope, c.Ref, c.Text, c.Question, c.Provenance,
                c.SourceRef, sourceName, safe, c.Status, c.StatusReason, c.ReviewNote,
                c.CreatedAt, c.VerifiedAt);
        })];
    }

    /// <summary>Relatievoorstellen (#116): status-chips + het kind-vocabulaire
    /// (kandidaten eerst, MechanicVocabulary-sortering) + de voorstellen zelf.
    /// Kaart-refs krijgen hun naam erbij (twee stappen, Interactions-patroon);
    /// andere ref-soorten zijn zelf leesbaar (mechanic:Deflect).
    /// Default-weergave (#124, claims-patroon): geen status = alleen
    /// te-reviewen en niet-gearchiveerd; archief/alles achter chips.</summary>
    public async Task<RelationOverview> RelationsAsync(string? status, int page)
    {
        page = ClampPage(page);
        var statusCounts = (await db.Relations.AsNoTracking()
                .Where(r => r.ArchivedAt == null)
                .GroupBy(r => r.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new RelationStatusCount(s.Key, s.Count))
            .ToList();
        var archived = await db.Relations.CountAsync(r => r.ArchivedAt != null);

        var kindRows = await db.RelationKinds.AsNoTracking()
            .OrderBy(k => k.Status == "candidate" ? 0 : 1)
            .ThenByDescending(k => k.Occurrences)
            .ThenBy(k => k.Kind)
            .ToListAsync();

        // Bewijs bij kandidaat-kinds (#123): tot 3 voorbeeldvoorstellen per
        // kind. Eén kleine query per kandidaat — de open queue is klein en
        // top-N-per-groep vertaalt niet in één LINQ-query.
        var kindExamples = new Dictionary<string, List<(string FromRef, string ToRef)>>();
        foreach (var kind in kindRows.Where(k => k.Status == "candidate").Select(k => k.Kind))
        {
            kindExamples[kind] = (await db.Relations.AsNoTracking()
                    .Where(r => r.Kind == kind && r.Status != "rejected")
                    .OrderByDescending(r => r.DetectedAt).ThenBy(r => r.Id)
                    .Take(3)
                    .Select(r => new { r.FromRef, r.ToRef })
                    .ToListAsync())
                .Select(r => (r.FromRef, r.ToRef))
                .ToList();
        }

        var query = db.Relations.AsNoTracking();
        query = string.IsNullOrWhiteSpace(status) switch
        {
            true => query.Where(r => r.Status == "unreviewed" && r.ArchivedAt == null),
            false when status == "all" => query,
            false when status == "archived" => query.Where(r => r.ArchivedAt != null),
            _ => query.Where(r => r.Status == status && r.ArchivedAt == null),
        };

        var total = await query.CountAsync();
        var rows = await query
            .OrderBy(r => r.Status == "unreviewed" && r.ArchivedAt == null ? 0 : 1)
            .ThenByDescending(r => r.DetectedAt).ThenBy(r => r.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        // Alleen card:-refs hebben een opzoekbare naam; de key is het id.
        var cardIds = rows
            .SelectMany(r => new[] { r.FromRef, r.ToRef })
            .Concat(kindExamples.Values.SelectMany(e => e.SelectMany(x => new[] { x.FromRef, x.ToRef })))
            .Where(r => r.StartsWith("card:", StringComparison.Ordinal))
            .Select(r => r["card:".Length..])
            .Distinct()
            .ToList();
        var names = await db.Cards.AsNoTracking()
            .Where(c => cardIds.Contains(c.RiftboundId))
            .Select(c => new { c.RiftboundId, c.Name })
            .ToDictionaryAsync(c => c.RiftboundId, c => c.Name);
        string? NameFor(string brainRef) =>
            brainRef.StartsWith("card:", StringComparison.Ordinal)
                ? names.GetValueOrDefault(brainRef["card:".Length..])
                : null;

        var kinds = kindRows.Select(k => new RelationKindOverviewItem(
                k.Id, k.Kind, k.Status, k.Occurrences, k.FirstSeen, k.ReviewedAt,
                [.. kindExamples.GetValueOrDefault(k.Kind, [])
                    .Select(e => new RelationKindExample(
                        e.FromRef, NameFor(e.FromRef), e.ToRef, NameFor(e.ToRef)))]))
            .ToList();

        var items = rows.Select(r => new RelationOverviewItem(
                r.Id, r.FromRef, NameFor(r.FromRef), r.ToRef, NameFor(r.ToRef),
                r.Kind, r.Explanation, r.Provenance, r.Trust, r.Status,
                r.ReviewNote, r.ArchivedAt, r.DetectedAt))
            .ToList();
        return new(total, page, PageSize, statusCounts, archived, kinds, items);
    }

    /// <summary>Piltover Archive-decks (#15): recentst op PA bijgewerkt eerst,
    /// met kaartaantallen per deck in een tweede stap (Interactions-patroon —
    /// geen join over alle deck_card-rijen). UnknownCards telt regels zonder
    /// kaartkoppeling: het beheersignaal dat de gallery achterloopt op PA.</summary>
    public async Task<Paged<DeckOverviewItem>> DecksAsync(int page)
    {
        page = ClampPage(page);
        var total = await db.Decks.CountAsync();
        var rows = await db.Decks.AsNoTracking()
            .OrderByDescending(d => d.PaUpdatedAt ?? d.FetchedAt).ThenBy(d => d.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        var ids = rows.Select(d => d.Id).ToList();
        var counts = (await db.DeckCards.AsNoTracking()
                .Where(c => ids.Contains(c.DeckId))
                .GroupBy(c => c.DeckId)
                .Select(g => new
                {
                    g.Key,
                    Cards = g.Sum(c => c.Quantity),
                    Unknown = g.Count(c => c.CanonicalRiftboundId == null),
                })
                .ToListAsync())
            .ToDictionary(c => c.Key);

        var items = rows.Select(d => new DeckOverviewItem(
                d.Id, d.PaId, d.Name, d.Domains,
                counts.TryGetValue(d.Id, out var c) ? c.Cards : 0,
                counts.TryGetValue(d.Id, out var u) ? u.Unknown : 0,
                d.Views, d.Likes, d.SourceUrl, d.PaUpdatedAt, d.FetchedAt))
            .ToList();
        return new(total, page, PageSize, items);
    }

    /// <summary>Bronvoorstellen uit de scout (#63): status-chips + de
    /// reviewqueue zelf, nieuwste vondsten eerst (zelfde patroon als Claims).</summary>
    public async Task<ProposalOverview> ProposalsAsync(string? status, int page)
    {
        page = ClampPage(page);
        var statusCounts = (await db.SourceProposals.AsNoTracking()
                .GroupBy(p => p.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new ProposalStatusCount(s.Key, s.Count))
            .ToList();

        var query = db.SourceProposals.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(p => p.FoundAt).ThenBy(p => p.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(p => new ProposalOverviewItem(
                p.Id, p.Url, p.Name, p.Type, p.Motivation,
                p.Status, p.FoundAt, p.ReviewedAt))
            .ToListAsync();
        return new(total, page, PageSize, statusCounts, items);
    }

    /// <summary>Bron-feeds (#167): geen paginering (net als de bronnentabel
    /// zelf) — het aantal feeds blijft klein en overzichtelijk.</summary>
    public async Task<IReadOnlyList<FeedOverviewItem>> FeedsAsync()
    {
        var feeds = await db.SourceFeeds.AsNoTracking().OrderBy(f => f.Id).ToListAsync();
        var counts = await db.Sources.AsNoTracking()
            .Where(s => s.FeedId != null)
            .GroupBy(s => s.FeedId!)
            .Select(g => new { FeedId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FeedId, x => x.Count);
        return feeds.Select(f => new FeedOverviewItem(
                f.Id, f.Name, f.Url, f.Enabled, f.AutoApprove, f.CategoryFilter,
                f.Cadence, f.LastChecked, counts.GetValueOrDefault(f.Id, 0)))
            .ToList();
    }

    /// <summary>Gebruikers met hun LLM-gebruik in de gekozen periode (#42):
    /// aantallen, foto's en de cheap/hard-verdeling — het kosteninzicht.
    /// Bewust uit ask_metric en niet uit ask_trace: traces bewaren maar 200
    /// rijen, dus alleen de metric-tabel telt eerlijk over een periode.</summary>
    public async Task<UserOverview> UsersAsync(string? period, int page)
    {
        page = ClampPage(page);
        var now = DateTimeOffset.UtcNow;
        var normalizedPeriod = period is "vandaag" or "30d" ? period : "7d";
        var since = normalizedPeriod switch
        {
            "vandaag" => new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero),
            "30d" => now.AddDays(-30),
            _ => now.AddDays(-7),
        };

        // Eén aggregatie over de periode; kleine gebruikersaantallen, dus de
        // volledige groepering ophalen en per paginarij opzoeken is prima.
        var stats = await db.AskMetrics.AsNoTracking()
            .Where(m => m.CreatedAt >= since)
            .GroupBy(m => m.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Questions = g.Count(),
                Photos = g.Count(m => m.HadImage),
                // Oude rijen (vóór #42) hebben geen model; daar was hard == foto.
                Hard = g.Count(m => m.Model == "hard" || (m.Model == null && m.HadImage)),
                Failed = g.Count(m => !m.Ok),
                AvgMs = (int)g.Average(m => m.DurationMs),
                // Tokentotalen (#121): rijen zonder usage tellen als 0 —
                // de som is een ondergrens, geen verzonnen getal.
                InputTokens = g.Sum(m => m.InputTokens ?? 0),
                OutputTokens = g.Sum(m => m.OutputTokens ?? 0),
            })
            .ToListAsync();
        var byUser = stats.Where(s => s.UserId != null).ToDictionary(s => s.UserId!.Value);
        var anon = stats.FirstOrDefault(s => s.UserId == null);

        // Tokentotalen per antwoordpad (#121), over álle vragen in de periode
        // (incl. anoniem). Zelfde model-afleiding als hierboven: oude rijen
        // zonder model vallen op basis van HadImage onder cheap/hard. Het
        // agentic-pad splitst op wie escaleerde (#153): rijen van vóór #153
        // (EscalatedBy null) waren per definitie gate-escalaties.
        var pathOrder = new[] { "cheap", "hard", "agentic (gate)", "agentic (gebruiker)" };
        var paths = (await db.AskMetrics.AsNoTracking()
                .Where(m => m.CreatedAt >= since)
                .GroupBy(m => m.Model == "agentic"
                    ? (m.EscalatedBy == "user" ? "agentic (gebruiker)" : "agentic (gate)")
                    : m.Model ?? (m.HadImage ? "hard" : "cheap"))
                .Select(g => new
                {
                    Path = g.Key,
                    Questions = g.Count(),
                    InputTokens = g.Sum(m => m.InputTokens ?? 0),
                    OutputTokens = g.Sum(m => m.OutputTokens ?? 0),
                })
                .ToListAsync())
            .OrderBy(p => { var i = Array.IndexOf(pathOrder, p.Path); return i < 0 ? int.MaxValue : i; })
            .ThenBy(p => p.Path)
            .Select(p => new PathUsageItem(p.Path, p.Questions, p.InputTokens, p.OutputTokens))
            .ToList();

        var total = await db.Users.CountAsync();
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        var items = users.Select(u =>
        {
            var s = byUser.GetValueOrDefault(u.Id);
            return new UserOverviewItem(
                u.Id, u.Email, u.Blocked, u.DailyQuota, u.DailyPhotoQuota,
                u.DailyAgenticQuota,
                u.CreatedAt, u.LastLoginAt,
                s?.Questions ?? 0, s?.Photos ?? 0,
                (s?.Questions ?? 0) - (s?.Hard ?? 0), s?.Hard ?? 0,
                s?.Failed ?? 0, s?.AvgMs ?? 0,
                s?.InputTokens ?? 0, s?.OutputTokens ?? 0);
        }).ToList();
        return new(total, page, PageSize, normalizedPeriod,
            anon?.Questions ?? 0, anon?.Photos ?? 0, paths, items);
    }

    public async Task<Paged<ChangeOverviewItem>> ChangesAsync(int page)
    {
        page = ClampPage(page);
        var total = await db.Changes.CountAsync();
        var items = await db.Changes.AsNoTracking()
            .OrderByDescending(c => c.DetectedAt).ThenBy(c => c.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(c => new ChangeOverviewItem(
                c.Id, c.SourceId, c.Source!.Name, c.ChangeType, c.Severity,
                c.Summary, c.Meaning, c.DetectedAt))
            .ToListAsync();
        return new(total, page, PageSize, items);
    }

    /// <summary>Vraag-traces (#40): de route-metadata van de laatste vragen.
    /// Bewust zónder antwoord en gespreks-historie — die kunnen groot zijn en
    /// komen per trace via <see cref="AskTraceAsync"/> (#143).</summary>
    public async Task<IReadOnlyList<AskTraceListItem>> AskTracesAsync() =>
        await db.AskTraces.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(30)
            .Select(t => new AskTraceListItem(
                t.Id, t.Question, t.QuestionType, t.RewrittenQuery, t.SourceBias,
                t.MentionsCard, t.MechanicMatches, t.Sections, t.ContextCards,
                t.PrimerDocs, t.CommunityClaims, t.VerifiedRulings, t.Model,
                t.HadImage, t.DurationMs, t.PhaseTimings, t.Agentic, t.EscalatedBy,
                t.BrainSteps, t.Ok, t.CreatedAt))
            .ToListAsync();

    /// <summary>Het gesprek achter één trace (#143): het definitieve antwoord
    /// plus de eerdere beurten — de metadata zit al in de lijst.</summary>
    public async Task<AskTraceDetail?> AskTraceAsync(long id)
    {
        var t = await db.AskTraces.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Answer, x.History })
            .SingleOrDefaultAsync();
        return t is null ? null : new(t.Id, t.Answer, ParseHistory(t.History));
    }

    private static readonly JsonSerializerOptions HistoryJson =
        new(JsonSerializerDefaults.Web);

    /// <summary>Het history-snapshot terug naar beurten; een onparseerbare
    /// rij (hoort niet voor te komen) degradeert naar een leeg gesprek —
    /// het antwoord zelf blijft dan gewoon zichtbaar.</summary>
    private static IReadOnlyList<AskTraceTurn> ParseHistory(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AskTraceTurn>>(json, HistoryJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Set-dekking (#145): per set welke basisnummers we hebben en
    /// wélke ontbreken — exact afgeleid uit de riftbound-id's zelf (pure
    /// aggregatie in SetCoverage). Setnaam/releasedatum/laatste sync komen
    /// uit card_set; sets die alleen als id-prefix bestaan (Riot-fallback
    /// zonder setregistratie) vallen terug op de set-code.</summary>
    public async Task<SetCoverageOverview> SetCoverageAsync()
    {
        var ids = await db.Cards.AsNoTracking()
            .Select(c => c.RiftboundId)
            .ToListAsync();
        var sets = await db.CardSets.AsNoTracking()
            .ToDictionaryAsync(s => s.SetId);

        var items = SetCoverage.Aggregate(ids)
            .Select(r =>
            {
                var set = sets.GetValueOrDefault(r.SetId);
                return new SetCoverageOverviewItem(
                    r.SetId, set?.Name ?? r.SetId, set?.PublishedOn, set?.SyncedAt,
                    r.BaseTotal, r.Present, r.MissingNumbers, r.Variants,
                    r.TotalDeviations);
            })
            .ToList();
        return new(items.Count, items.Count(i => i.MissingNumbers.Count > 0), items);
    }

    /// <summary>Benchmark-tegel (#158, uitgebreid met de model-sweep #174):
    /// laatste 50 single-model runs + het detail van de gevraagde (of, zonder
    /// parameter, meest recente) run, plús de sweep-historie en het detail
    /// van de gevraagde (of meest recente) sweep. Sweep-runs (SweepId != null)
    /// blijven buiten de single-run-lijst — anders verdrinkt een sweep van
    /// N modellen × 2 die lijst in rijen die niet los bedoeld zijn. Zonder
    /// runs/sweeps zijn Selected/SelectedSweep null — de UI toont dan de
    /// nette lege staat ("start de job").</summary>
    public async Task<BenchmarkOverview> BenchmarkAsync(long? runId, long? sweepId = null)
    {
        var runs = await db.BenchmarkRuns.AsNoTracking()
            .Where(r => r.SweepId == null)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new BenchmarkRunOverviewItem(
                r.Id, r.Label, r.QuestionCount, r.KeyedCount, r.CorrectCount,
                r.ScorePercent, r.StartedAt, r.CompletedAt))
            .ToListAsync();

        var selectedId = runId ?? runs.FirstOrDefault()?.Id;
        BenchmarkRunDetail? selected = null;
        if (selectedId is { } id)
        {
            var run = runs.FirstOrDefault(r => r.Id == id);
            if (run is not null)
            {
                var results = await db.BenchmarkResults.AsNoTracking()
                    .Where(r => r.RunId == id)
                    .Join(db.BenchmarkQuestions.AsNoTracking(), r => r.QuestionId, q => q.Id,
                        (r, q) => new { r, q })
                    .OrderBy(x => x.q.Id)
                    .Select(x => new BenchmarkResultOverviewItem(
                        x.r.Id, x.q.Id, x.q.ExternalKey, x.q.Category, x.q.Question,
                        x.q.Options, x.q.CorrectIndex, x.q.Explanation,
                        x.r.Answer, x.r.ChosenIndex, x.r.Correct,
                        x.r.DurationMs, x.r.InputTokens, x.r.OutputTokens))
                    .ToListAsync();
                selected = new(run, results);
            }
        }

        // Sweeps (#174): SweepId is de UTC-starttijd in ms (zie
        // BenchmarkService.RunSweepAsync) — DESC sorteren = nieuwste eerst,
        // en dubbelt meteen als StartedAt zonder extra kolom.
        var sweepRuns = await db.BenchmarkRuns.AsNoTracking()
            .Where(r => r.SweepId != null)
            .OrderByDescending(r => r.SweepId)
            .ToListAsync();
        var sweeps = sweepRuns
            .GroupBy(r => r.SweepId!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new BenchmarkSweepSummary(
                g.Key, DateTimeOffset.FromUnixTimeMilliseconds(g.Key),
                g.Select(r => r.Model).Distinct().Count(), g.Max(r => r.QuestionCount)))
            .ToList();

        var selectedSweepId = sweepId ?? sweeps.FirstOrDefault()?.SweepId;
        BenchmarkSweepDetail? selectedSweep = null;
        if (selectedSweepId is { } sid)
        {
            var runsForSweep = sweepRuns.Where(r => r.SweepId == sid).ToList();
            if (runsForSweep.Count > 0)
            {
                // Tijdsmaten (#174: "totale/gemiddelde tijd — de eerlijke
                // tijdsvergelijking") komen uit benchmark_result, niet uit
                // BenchmarkRun zelf — één aggregatiequery voor alle runs van
                // deze sweep in plaats van er één per run.
                var runIds = runsForSweep.Select(r => r.Id).ToList();
                var aggregates = await db.BenchmarkResults.AsNoTracking()
                    .Where(r => runIds.Contains(r.RunId))
                    .GroupBy(r => r.RunId)
                    .Select(g => new
                    {
                        RunId = g.Key,
                        TotalDurationMs = g.Sum(x => (long)x.DurationMs),
                        AvgDurationMs = g.Average(x => x.DurationMs),
                        TotalInputTokens = g.Sum(x => x.InputTokens ?? 0),
                        TotalOutputTokens = g.Sum(x => x.OutputTokens ?? 0),
                    })
                    .ToDictionaryAsync(x => x.RunId);

                var models = runsForSweep
                    .GroupBy(r => r.Model ?? "onbekend")
                    .Select(g =>
                    {
                        var runItems = g.OrderBy(r => r.RunIndex)
                            .Select(r =>
                            {
                                var agg = aggregates.GetValueOrDefault(r.Id);
                                return new BenchmarkSweepRunItem(
                                    r.Id, r.RunIndex ?? 0, r.ScorePercent, r.KeyedCount,
                                    r.CorrectCount, r.QuestionCount,
                                    agg?.TotalDurationMs ?? 0,
                                    agg is null ? 0 : Math.Round(agg.AvgDurationMs, 0),
                                    agg?.TotalInputTokens ?? 0, agg?.TotalOutputTokens ?? 0);
                            })
                            .ToList();
                        // Consistentie (issue-eis): alleen te bepalen met
                        // precies 2 gescoorde runs — anders "nog niet te zeggen".
                        bool? consistent = runItems.Count == 2
                            && runItems[0].ScorePercent is { } s0 && runItems[1].ScorePercent is { } s1
                            ? Math.Abs(s0 - s1) < 0.05
                            : null;
                        return new BenchmarkSweepModelItem(g.Key, runItems, consistent);
                    })
                    .ToList();
                selectedSweep = new(sid, DateTimeOffset.FromUnixTimeMilliseconds(sid), models);
            }
        }

        return new(runs, selected, sweeps, selectedSweep);
    }
}
