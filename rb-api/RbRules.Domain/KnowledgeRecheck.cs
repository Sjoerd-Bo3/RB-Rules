namespace RbRules.Domain;

/// <summary>Kennis-levenscyclus (#119), de pure koppeling: een verwerkte
/// regelwijziging → betrokken refs → acties. De AFFECTS-mapper (#104) is de
/// enige bron van "betrokken"; de intersectie met wat kennis eraan ophangt
/// volgt exact de graph-semantiek: primer-docs leunen via hun SectionRefs op
/// secties (EXPLAINS), claims wijzen via topic_type/topic_ref naar hun
/// onderwerp (ABOUT). Geen nieuwe heuristiek — alleen bestaande mappers en
/// een doorsnede. Redenen reizen zonder migratie mee: als kanttekening
/// vooraan in de doc-tekst (het #110-patroon) en als StatusReason-prefix op
/// de claim.</summary>
public static class KnowledgeRecheck
{
    /// <summary>Primer-doc-kandidaat: id + de komma-gescheiden §-codes
    /// waarop het doc gebaseerd is (KnowledgeDoc.SectionRefs).</summary>
    public sealed record DocCandidate(long Id, string? SectionRefs);

    /// <summary>Claim-kandidaat: id + het onderwerp (topic_type/topic_ref,
    /// dezelfde velden als de ABOUT-edge in de graph).</summary>
    public sealed record ClaimCandidate(long Id, string? TopicType, string TopicRef);

    /// <summary>Actie: dit doc terug naar draft, met deze reden.</summary>
    public sealed record DocMark(long DocId, string Reason);

    /// <summary>Het hertoets-plan voor één change: welke docs terug naar
    /// draft moeten en welke claims opnieuw langs de official-check.</summary>
    public sealed record RecheckPlan(
        IReadOnlyList<DocMark> Docs, IReadOnlyList<long> ClaimIds)
    {
        public bool IsEmpty => Docs.Count == 0 && ClaimIds.Count == 0;
    }

    /// <summary>change → betrokken refs → acties. Puur: de mappers en
    /// kandidaten komen van de aanroeper. Geen doelen (set-release,
    /// editorial, niet-matchende tekst) ⇒ leeg plan — de change is dan
    /// verwerkt zonder betrokken kennis, nooit een crash.</summary>
    public static RecheckPlan PlanFor(
        long changeId, string? changeType, string? summary, string? meaning, string? diff,
        ChangeAffectsMapper affects, ClaimTopicMapper topics,
        IEnumerable<DocCandidate> docs, IEnumerable<ClaimCandidate> claims)
    {
        var targets = affects
            .Resolve(changeType, ChangeAffectsMapper.AffectsText(summary, meaning, diff))
            .ToHashSet();
        if (targets.Count == 0) return new([], []);

        // Docs: §-codes lossen op via dezelfde resolutie als de
        // EXPLAINS-edges (ClaimTopicMapper.ResolveSection) — een doc is
        // betrokken zodra één van zijn secties door de change geraakt wordt.
        var docMarks = new List<DocMark>();
        foreach (var doc in docs)
        {
            var codes = (doc.SectionRefs ?? "").Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var hits = codes
                .Where(code => topics.ResolveSection(code) is { } r && targets.Contains(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hits.Count > 0) docMarks.Add(new(doc.Id, DocReason(changeId, hits)));
        }

        // Claims: het onderwerp lost op via dezelfde resolutie als de
        // ABOUT-edges. Mechanic-/concept-claims kunnen nooit matchen — de
        // AFFECTS-mapper produceert alleen kaart- en sectie-doelen.
        var claimIds = claims
            .Where(c => topics.Resolve(c.TopicType, c.TopicRef) is { } r && targets.Contains(r))
            .Select(c => c.Id)
            .Distinct()
            .ToList();

        return new(docMarks, claimIds);
    }

    /// <summary>Reden voor een doc-terugzetting, met de geraakte §'s zodat
    /// de beheerder weet wát er te controleren valt.</summary>
    public static string DocReason(long changeId, IEnumerable<string> hitCodes) =>
        $"regelwijziging #{changeId} raakt {string.Join(", ", hitCodes.Select(c => $"§{c}"))}"
        + " — controleer of de tekst nog klopt en keur opnieuw goed";

    // ── Kanttekening in de doc-tekst (#110-patroon: bewust zonder migratie,
    //    geen eigen kolom). De kanttekening leeft alleen zolang het doc op
    //    review wacht: goedkeuren of bewerken stript hem weer. ─────────────

    public const string MarkerStart = "[hertoetsing: ";

    /// <summary>Reden → kanttekening-regel. "]" in de reden wordt ")" zodat
    /// een kanttekening altijd exact één herkenbare regel blijft.</summary>
    public static string Marker(string reason) =>
        $"{MarkerStart}{reason.Replace(']', ')')}]";

    /// <summary>Kanttekening vooraan toevoegen — idempotent: dezelfde
    /// kanttekening (zelfde change, zelfde §'s) stapelt nooit; een andere
    /// change krijgt zijn eigen regel erboven.</summary>
    public static string AddMarker(string body, string marker) =>
        LeadingMarkers(body).Contains(marker, StringComparer.Ordinal)
            ? body
            : $"{marker}\n\n{body}";

    /// <summary>Verwijdert alle leidende kanttekeningen (+ lege regels
    /// ertussen); zonder kanttekening blijft de tekst byte-voor-byte gelijk
    /// — de bestaande embedding hoort immers bij die tekst.</summary>
    public static string StripMarkers(string body)
    {
        var lines = body.Split('\n');
        var idx = 0;
        var sawMarker = false;
        while (idx < lines.Length)
        {
            var line = lines[idx].TrimEnd('\r').Trim();
            if (line.Length == 0) { idx++; continue; }
            if (!IsMarkerLine(line)) break;
            sawMarker = true;
            idx++;
        }
        return sawMarker
            ? string.Join('\n', lines[idx..]).TrimStart('\r', '\n')
            : body;
    }

    /// <summary>De redenen uit de leidende kanttekeningen — voor het
    /// kennis-gaten-rapport (verouderingssignalen).</summary>
    public static IReadOnlyList<string> MarkerReasons(string body) =>
        [.. LeadingMarkers(body)
            .Select(m => m[MarkerStart.Length..^1])];

    private static List<string> LeadingMarkers(string body)
    {
        var markers = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue; // lege regel tussen kanttekeningen
            if (!IsMarkerLine(line)) break;
            markers.Add(line);
        }
        return markers;
    }

    private static bool IsMarkerLine(string line) =>
        line.StartsWith(MarkerStart, StringComparison.Ordinal) && line.EndsWith(']');

    // ── Claim-kant: reden als StatusReason-prefix (bestaand veld) ─────────

    /// <summary>Prefix waaraan het gaten-rapport hertoets-veroudering
    /// herkent (const: vertaalt als literal in EF-queries).</summary>
    public const string ClaimReasonPrefix = "hertoetst na regelwijziging ";

    public static string ClaimReason(long changeId, string? verdictReason) =>
        $"{ClaimReasonPrefix}#{changeId}: "
        + (verdictReason ?? "de officiële regels spreken deze claim tegen");

    /// <summary>Uitslag "contradicted" toepassen — zelfde semantiek als de
    /// bestaande hertoets in de claims-pipeline: een geaccepteerde claim
    /// wordt superseded (terug de reviewqueue in), een onbeoordeelde
    /// rejected; de reden verwijst naar de change én de officiële §.</summary>
    public static (string Status, string StatusReason) ApplyContradicted(
        long changeId, string currentStatus, string? verdictReason) =>
        (currentStatus == "accepted" ? "superseded" : "rejected",
         ClaimReason(changeId, verdictReason));
}
