using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record RuleTocSection(string Code, string Preview);
public record RuleTocSource(
    string SourceId, string SourceName, IReadOnlyList<RuleTocSection> Sections);

/// <summary>SourceUpdatedAt/SourcePublishedAt (#168): "laatst bijgewerkt" (een
/// echte content-wijziging, IngestService) valt terug op "geldig sinds" (de
/// publicatiedatum van de bron) als er nog geen wijziging is gezien; beide
/// null als de bron geen van beide draagt (legacy/handmatig toegevoegd).</summary>
public record RuleSection(
    string Code, string SourceId, string SourceName, string SourceUrl,
    string Text, string? PdfUrl, int? Page,
    IReadOnlyList<ParentSection> Parents, string? Prev, string? Next,
    DateTimeOffset? SourcePublishedAt, DateTimeOffset? SourceUpdatedAt);

// ── Sectie-dossier (#127) ────────────────────────────────────────────────

public record SectionLeaningCard(
    string RiftboundId, string Name, string? Type, string? ImageUrl);

public record SectionExplainsDoc(string Topic, string Title);

public record SectionClaim(
    long Id, string Statement, string OfficialStatus, int Corroboration,
    double TrustScore, string TrustLabel);

public record SectionChangeEvent(
    long Id, string ChangeType, string Severity, string? Summary,
    DateTimeOffset DetectedAt);

/// <summary>De levende geschiedenis van één regelsectie: kaarten die er
/// semantisch op leunen, primer-uitleg die de sectie EXPLAINS, geaccepteerde
/// community-claims erover en changes die de sectie raakten (AFFECTS, via de
/// kennisgraaf). GraphDegraded meldt eerlijk dat de changes-historie
/// ontbreekt omdat Neo4j onbereikbaar was — de rest van het dossier werkt
/// dan gewoon. Uitbreidpunt: misvattingen (#125) sluiten hier aan zodra dat
/// veld bestaat.</summary>
public record SectionDossier(
    IReadOnlyList<SectionLeaningCard> Cards,
    IReadOnlyList<SectionExplainsDoc> Explains,
    IReadOnlyList<SectionClaim> Claims,
    IReadOnlyList<SectionChangeEvent> Changes,
    bool GraphDegraded);

/// <summary>Regels-browser (#59, uit de endpoints): de hoofdstuk-hiërarchie
/// (toc) en één sectie met ouderketen, PDF-deeplink en buursecties; sinds
/// #127 ook het sectie-dossier (hergebruik van de brein-graph in-process).</summary>
public class RuleBrowserService(
    RbRulesDbContext db, BrainGraphService graph, ILogger<RuleBrowserService> logger)
{
    public async Task<IReadOnlyList<RuleTocSource>> TocAsync(CancellationToken ct = default)
    {
        var rows = await db.RuleChunks
            .Where(c => c.SectionCode != null && c.SectionCode != "" && c.SectionCode != "intro")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new
            {
                c.SourceId, c.SectionCode, c.ChunkIndex,
                Preview = c.Text.Substring(0, Math.Min(c.Text.Length, 140)),
            })
            .ToListAsync(ct);
        var sources = await db.Sources.ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return [.. rows
            .GroupBy(r => r.SourceId)
            .Select(g => new RuleTocSource(
                g.Key,
                sources.GetValueOrDefault(g.Key, g.Key),
                [.. g.GroupBy(r => r.SectionCode!)
                    .Select(sg => new
                    {
                        Code = sg.Key,
                        Preview = sg.OrderBy(x => x.ChunkIndex).First().Preview,
                        Index = sg.Min(x => x.ChunkIndex),
                    })
                    .OrderBy(s => s.Index)
                    .Select(s => new RuleTocSection(s.Code, s.Preview))]))
            .OrderBy(g => g.SourceId)];
    }

    public async Task<RuleSection?> SectionAsync(
        string code, string? source, CancellationToken ct = default)
    {
        var query = db.RuleChunks.Where(c => c.SectionCode == code);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);
        var chunks = await query
            .OrderBy(c => c.ChunkIndex)
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new
            {
                c.SourceId, SourceName = s.Name, SourceUrl = s.Url,
                c.ChunkIndex, c.Text, c.Page, c.DocumentId,
                s.PublishedAt, s.UpdatedAt,
            })
            .ToListAsync(ct);
        if (chunks.Count == 0) return null;

        // PDF-deeplink: werkelijke bestands-URL + beginpagina van de sectie.
        var fileUrl = await db.Documents
            .Where(d => d.Id == chunks[0].DocumentId)
            .Select(d => d.FileUrl)
            .FirstOrDefaultAsync(ct);

        // Bij codes die in meerdere bronnen voorkomen: houd één bron aan.
        var srcId = chunks[0].SourceId;
        chunks = [.. chunks.Where(c => c.SourceId == srcId)];

        // Buursecties in leesvolgorde van dezelfde bron.
        var codes = await db.RuleChunks
            .Where(c => c.SourceId == srcId && c.SectionCode != null &&
                        c.SectionCode != "" && c.SectionCode != "intro")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.SectionCode!)
            .ToListAsync(ct);
        var distinct = codes.Distinct().ToList();
        var idx = distinct.IndexOf(code);

        // Ouderketen (#39): subregels tonen hun bovenliggende regels mee.
        var parents = await RuleParentLookup.FetchAsync(db, [(srcId, code)], ct);

        return new RuleSection(
            code, srcId, chunks[0].SourceName, chunks[0].SourceUrl,
            string.Join("\n\n", chunks.Select(c => c.Text)),
            fileUrl, chunks[0].Page,
            parents.GetValueOrDefault((srcId, code)) ?? [],
            idx > 0 ? distinct[idx - 1] : null,
            idx >= 0 && idx < distinct.Count - 1 ? distinct[idx + 1] : null,
            chunks[0].PublishedAt, chunks[0].UpdatedAt);
    }

    private const int DossierCards = 8;
    private const int DossierClaims = 10;
    private const int DossierChanges = 12;

    /// <summary>Semantische nabijheids-plafond voor "kaarten die op deze
    /// regel leunen" — zelfde afstands-cap als de claims-retrieval (0.55):
    /// liever een leeg hoofdstuk dan willekeurige kaarten bij een generieke
    /// sectie.</summary>
    private const double CardDistanceCap = 0.55;

    /// <summary>Sectie-dossier (#127). Null als de sectie niet bestaat;
    /// bron-resolutie identiek aan <see cref="SectionAsync"/> zodat pagina en
    /// dossier altijd over dezelfde sectie praten.</summary>
    public async Task<SectionDossier?> DossierAsync(
        string code, string? source, CancellationToken ct = default)
    {
        var query = db.RuleChunks.AsNoTracking().Where(c => c.SectionCode == code);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.SourceId == source);
        var anchor = await query
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.SourceId, c.Embedding })
            .FirstOrDefaultAsync(ct);
        if (anchor is null) return null;
        var srcId = anchor.SourceId;

        // 1. Kaarten die op de sectie leunen: de omkering van de bestaande
        // kaart→regels-koppeling — canonieke kaarten semantisch dichtstbij de
        // sectie-embedding, met afstands-cap (in-memory, bewezen patroon).
        var cards = new List<SectionLeaningCard>();
        if (anchor.Embedding is { } sectionVector)
        {
            var nearest = await db.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(sectionVector))
                .Take(DossierCards * 2)
                .Select(c => new
                {
                    c.RiftboundId, c.Name, c.Type, c.ImageUrl,
                    Distance = c.Embedding!.CosineDistance(sectionVector),
                })
                .ToListAsync(ct);
            cards = [.. nearest
                .Where(c => c.Distance <= CardDistanceCap)
                .Take(DossierCards)
                .Select(c => new SectionLeaningCard(c.RiftboundId, c.Name, c.Type, c.ImageUrl))];
        }

        // 2. Primer-uitleg die deze sectie EXPLAINS: dezelfde §-resolutie als
        // de graph-projectie (ClaimTopicMapper.ResolveSection), maar dan
        // in-process — twaalf docs, dus in-memory filteren is de goedkoopste
        // correcte vorm.
        var resolver = ClaimTopicMapper.Create([], [], [(srcId, code)], []);
        var explains = (await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer" && k.Status == "approved" &&
                    k.SectionRefs != null && k.SectionRefs != "")
                .Select(k => new { k.Topic, k.Title, k.SectionRefs })
                .ToListAsync(ct))
            .Where(k => k.SectionRefs!
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(r => resolver.ResolveSection(r) is not null))
            .Select(k => new SectionExplainsDoc(k.Topic, k.Title))
            .ToList();

        // 3. Geaccepteerde claims over de sectie: claims dragen de §-code in
        // vrije vorm ("§ 101.2", "101.2"); de ILike-voorselectie houdt de
        // kandidatenlijst klein, de resolver doet de exacte match.
        var codePattern = "%" + EscapeLike(code) + "%";
        var claims = (await db.Claims.AsNoTracking()
                .Where(c => c.Status == "accepted" && c.TopicType == "section" &&
                    EF.Functions.ILike(c.TopicRef, codePattern, "\\"))
                .OrderByDescending(c => c.TrustScore)
                .Take(DossierClaims * 2)
                .Select(c => new
                {
                    c.Id, c.TopicRef, c.Statement, c.Corroboration,
                    c.TrustScore, c.Status, c.OfficialStatus,
                })
                .ToListAsync(ct))
            .Where(c => resolver.ResolveSection(c.TopicRef) is not null)
            .Take(DossierClaims)
            .Select(c => new SectionClaim(
                c.Id, c.Statement, c.OfficialStatus, c.Corroboration, c.TrustScore,
                ClaimTrust.Label(c.Corroboration, c.TrustScore, c.Status, c.OfficialStatus)))
            .ToList();

        // 4. Changes die de sectie raakten: de AFFECTS-edges uit de
        // kennisgraaf (in-process hergebruik van BrainGraphService), met de
        // detailprojectie uit Postgres — de graph wijst aan, Postgres vertelt.
        // Neo4j-uitval is een verwacht pad: dossier zonder changes-historie,
        // eerlijk gelabeld via GraphDegraded.
        var changes = new List<SectionChangeEvent>();
        var graphDegraded = false;
        try
        {
            var sectionRef = BrainRef.Section(srcId, code).Format();
            var neighbors = await graph.NeighborsAsync(
                "RuleSection", sectionRef, ["AFFECTS"], kind: "",
                BrainDirection.In, DossierChanges * 2, ct);
            var changeIds = (neighbors ?? [])
                .Select(n => BrainRef.TryParse(n.Ref, out var r) &&
                    r.Kind == BrainRefKind.Change && long.TryParse(r.Key, out var id)
                        ? id : (long?)null)
                .OfType<long>()
                .Distinct()
                .ToList();
            if (changeIds.Count > 0)
                changes = await db.Changes.AsNoTracking()
                    .Where(c => changeIds.Contains(c.Id))
                    .OrderByDescending(c => c.DetectedAt)
                    .Take(DossierChanges)
                    .Select(c => new SectionChangeEvent(
                        c.Id, c.ChangeType, c.Severity, c.Summary, c.DetectedAt))
                    .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Sectie-dossier: changes-historie niet beschikbaar (Neo4j onbereikbaar?)");
            graphDegraded = true;
        }

        return new(cards, explains, claims, changes, graphDegraded);
    }

    /// <summary>LIKE/ILIKE-metatekens onschadelijk maken (escape-teken is
    /// backslash — zelfde afspraak als BrainService).</summary>
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
