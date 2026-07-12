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

public record ErratumOverviewItem(
    long Id, string CardName, string? CardRiftboundId, string NewText,
    string SourceUrl, DateTimeOffset DetectedAt);

public record InteractionOverviewItem(
    long Id, string Kind, string Explanation, string CardAId, string CardAName,
    string CardBId, string CardBName, DateTimeOffset DetectedAt);

public record ChangeOverviewItem(
    long Id, string SourceId, string SourceName, string ChangeType, string Severity,
    string? Summary, string? Meaning, DateTimeOffset DetectedAt);

public record ClaimSourceOverviewItem(
    string SourceId, string SourceName, string Url, string? Quote, DateTimeOffset SeenAt);
public record ClaimOverviewItem(
    long Id, string TopicType, string TopicRef, string Statement,
    int Corroboration, double TrustScore, string Status, string? StatusReason,
    string OfficialStatus, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    IReadOnlyList<ClaimSourceOverviewItem> Sources);
public record ClaimStatusCount(string Status, int Count);
public record ClaimOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<ClaimStatusCount> StatusCounts, IReadOnlyList<ClaimOverviewItem> Items);

public record RelationOverviewItem(
    long Id, string FromRef, string? FromName, string ToRef, string? ToName,
    string Kind, string Explanation, string Provenance, double Trust,
    string Status, DateTimeOffset DetectedAt);
public record RelationKindOverviewItem(
    long Id, string Kind, string Status, int Occurrences,
    DateTimeOffset FirstSeen, DateTimeOffset? ReviewedAt);
public record RelationStatusCount(string Status, int Count);
public record RelationOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<RelationStatusCount> StatusCounts,
    IReadOnlyList<RelationKindOverviewItem> Kinds,
    IReadOnlyList<RelationOverviewItem> Items);

public record ProposalOverviewItem(
    long Id, string Url, string Name, string Type, string Motivation,
    string Status, DateTimeOffset FoundAt, DateTimeOffset? ReviewedAt);
public record ProposalStatusCount(string Status, int Count);
public record ProposalOverview(
    int Total, int Page, int PageSize,
    IReadOnlyList<ProposalStatusCount> StatusCounts, IReadOnlyList<ProposalOverviewItem> Items);

public record UserOverviewItem(
    long Id, string Email, bool Blocked, int DailyQuota, int DailyPhotoQuota,
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

    public async Task<IReadOnlyList<ErratumOverviewItem>> ErrataAsync() =>
        await db.Errata.AsNoTracking()
            .OrderByDescending(e => e.DetectedAt).ThenBy(e => e.CardName)
            .Select(e => new ErratumOverviewItem(
                e.Id, e.CardName, e.CardRiftboundId, e.NewText, e.SourceUrl, e.DetectedAt))
            .ToListAsync();

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
    /// embeddings blijven buiten de projectie).</summary>
    public async Task<ClaimOverview> ClaimsAsync(string? status, int page)
    {
        page = ClampPage(page);
        var statusCounts = (await db.Claims.AsNoTracking()
                .GroupBy(c => c.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new ClaimStatusCount(s.Key, s.Count))
            .ToList();

        var query = db.Claims.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(c => c.LastSeen).ThenBy(c => c.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .Select(c => new
            {
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
                c.FirstSeen, c.LastSeen,
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
                    new ClaimSourceOverviewItem(s.SourceId, s.Name, s.Url, s.QuoteExcerpt, s.SeenAt))]);

        var items = rows.Select(c => new ClaimOverviewItem(
                c.Id, c.TopicType, c.TopicRef, c.Statement, c.Corroboration,
                c.TrustScore, c.Status, c.StatusReason, c.OfficialStatus,
                c.FirstSeen, c.LastSeen,
                bySrc.GetValueOrDefault(c.Id, [])))
            .ToList();
        return new(total, page, PageSize, statusCounts, items);
    }

    /// <summary>Relatievoorstellen (#116): status-chips + het kind-vocabulaire
    /// (kandidaten eerst, MechanicVocabulary-sortering) + de voorstellen zelf.
    /// Kaart-refs krijgen hun naam erbij (twee stappen, Interactions-patroon);
    /// andere ref-soorten zijn zelf leesbaar (mechanic:Deflect).</summary>
    public async Task<RelationOverview> RelationsAsync(string? status, int page)
    {
        page = ClampPage(page);
        var statusCounts = (await db.Relations.AsNoTracking()
                .GroupBy(r => r.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(s => s.Key)
                .ToListAsync())
            .Select(s => new RelationStatusCount(s.Key, s.Count))
            .ToList();

        var kinds = await db.RelationKinds.AsNoTracking()
            .OrderBy(k => k.Status == "candidate" ? 0 : 1)
            .ThenByDescending(k => k.Occurrences)
            .ThenBy(k => k.Kind)
            .Select(k => new RelationKindOverviewItem(
                k.Id, k.Kind, k.Status, k.Occurrences, k.FirstSeen, k.ReviewedAt))
            .ToListAsync();

        var query = db.Relations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(r => r.DetectedAt).ThenBy(r => r.Id)
            .Skip(Math.Max(0, page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        // Alleen card:-refs hebben een opzoekbare naam; de key is het id.
        var cardIds = rows
            .SelectMany(r => new[] { r.FromRef, r.ToRef })
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

        var items = rows.Select(r => new RelationOverviewItem(
                r.Id, r.FromRef, NameFor(r.FromRef), r.ToRef, NameFor(r.ToRef),
                r.Kind, r.Explanation, r.Provenance, r.Trust, r.Status, r.DetectedAt))
            .ToList();
        return new(total, page, PageSize, statusCounts, kinds, items);
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
        // zonder model vallen op basis van HadImage onder cheap/hard.
        var pathOrder = new[] { "cheap", "hard", "agentic" };
        var paths = (await db.AskMetrics.AsNoTracking()
                .Where(m => m.CreatedAt >= since)
                .GroupBy(m => m.Model ?? (m.HadImage ? "hard" : "cheap"))
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
}
