namespace RbRules.Domain;

/// <summary>Mining-precisie per (soort × model) over een reeks <see cref="MiningRun"/>s
/// (#231, spec §7 — ops-observability; faalmodus 1/2 drift-alarm). <see
/// cref="Precision"/> = geverifieerd ÷ (geverifieerd + verworpen) — het aandeel dat
/// de promotie-poort overleefde; <see cref="AcceptRate"/> = geverifieerd ÷ kandidaten
/// — hoe scherp de kandidaatgeneratie mikte. Onafgemaakte runs (geen CompletedAt)
/// tellen niet mee (half werk is geen schone meting).</summary>
public sealed record MiningPrecisionRow(
    string Kind,
    string Model,
    int Runs,
    int Candidates,
    int Verified,
    int Rejected,
    double Precision,
    double AcceptRate);

/// <summary>Gemeten audit-precisie per (model × promptversie) over de
/// steekproef-audits (#255). Bewust een EIGEN rij naast
/// <see cref="MiningPrecisionRow"/>: die meet de accept-ratio van onze eigen
/// promotie-poort (zelfreferentieel — een pijplijn die tautologieën promoveert
/// scoort er uitstekend op), déze meet wat een onafhankelijk, sterker model van de
/// gepromoveerde feiten vindt. <see cref="Precision"/> = correct-én-gedragen ÷
/// geauditeerd; <see cref="Incorrect"/> en <see cref="Unsupported"/> splitsen de
/// afwijzingen uit (een feit kan toevallig kloppen zonder dat het bewijs het
/// draagt — dat onderscheid is de meting).</summary>
public sealed record AuditPrecisionRow(
    string Model,
    string PromptVersion,
    int Audited,
    int Sound,
    int Incorrect,
    int Unsupported,
    double Precision);

/// <summary>Kosten/latency per retrieval-modus (#231, spec §7 — router-tuning uit
/// fase 4). Rauwe rij: één afgeronde /ask-run, de primaire modus + zijn latency en
/// tokenverbruik (de service leidt deze af uit <see cref="GraphRag.AnswerTrace"/> ×
/// AskMetric/token-metering).</summary>
public sealed record RetrievalModeCostSample(string Mode, double LatencyMs, int Tokens);

/// <summary>Geaggregeerde kosten/latency voor één retrieval-modus.</summary>
public sealed record RetrievalModeCostRow(
    string Mode,
    int Runs,
    double MeanLatencyMs,
    double MeanTokens,
    long TotalTokens);

/// <summary>Community-gezondheid (#231, spec §7 — community-modularity/stabiliteit
/// uit fase 1/5). <see cref="Modularity"/> komt uit GDS (Leiden, buiten CI — hier
/// doorgegeven); <see cref="Stability"/> is de deterministisch gemeten
/// partitie-overeenkomst t.o.v. de vorige run (<see cref="CommunityStability"/>).</summary>
public sealed record CommunityHealthRow(
    double Modularity,
    int CommunityCount,
    int SingletonCount,
    double? Stability);

/// <summary>Meet de stabiliteit van een community-indeling tussen twee runs (#231,
/// spec §7). Community-labels zijn niet stabiel over runs (Leiden hernummert), dus
/// meet dit label-onafhankelijk: elke HUIDIGE community wordt gematcht op de best
/// overlappende VORIGE community (Jaccard), en de score is het lidmaatschaps-gewogen
/// gemiddelde daarvan. 1.0 = identieke indeling; laag = de gemeenschappen zijn
/// omgeschud (instabiel → drift-alarm). Puur en getest.</summary>
public static class CommunityStability
{
    /// <summary>Partitie als node → community-label. Score in [0,1]. Lege huidige
    /// partitie → 1.0 (niets veranderd om te meten).</summary>
    public static double Score(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (current.Count == 0) return 1.0;

        var prevByLabel = previous
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var currByLabel = current
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        double weightedSum = 0;
        int totalMembers = 0;
        foreach (var (_, members) in currByLabel)
        {
            var bestJaccard = prevByLabel.Values
                .Select(prev => Jaccard(members, prev))
                .DefaultIfEmpty(0.0)
                .Max();
            weightedSum += bestJaccard * members.Count;
            totalMembers += members.Count;
        }
        return totalMembers == 0 ? 1.0 : weightedSum / totalMembers;
    }

    private static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 1.0 : (double)inter / union;
    }
}

/// <summary>De ops-observability-rollups (#231, spec §7 — "aggregaties als queryable
/// rapporten voor de admin-tegels", inzicht #236). Puur: de service levert de rauwe
/// rijen uit Postgres/AnswerTrace/GDS, dit rollt ze samen tot admin-tegel-vorm. Elke
/// deelmeting hergebruikt bestaande fase-bouwstenen (<see cref="GraphDrift"/>,
/// <see cref="CanonicalDriftSnapshot"/>, <see cref="HypothesisYield"/>) i.p.v. ze te
/// dupliceren; alleen de nieuwe aggregaties (mining-precisie, retrieval-kosten,
/// community-gezondheid) leven hier.</summary>
public static class ObservabilityRollups
{
    /// <summary>Mining-precisie per (soort × model). Alleen afgeronde runs; runs
    /// zonder model krijgen het label <c>"(deterministisch)"</c> — een pipeline
    /// zonder LLM heeft ook een precisie. Gesorteerd op meeste kandidaten (impact).</summary>
    public static IReadOnlyList<MiningPrecisionRow> MiningPrecision(IEnumerable<MiningRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return [.. runs
            .Where(r => r.CompletedAt is not null)
            .GroupBy(r => (r.Kind, Model: r.LlmModel ?? "(deterministisch)"))
            .Select(g =>
            {
                var candidates = g.Sum(r => r.Candidates);
                var verified = g.Sum(r => r.Verified);
                var rejected = g.Sum(r => r.Rejected);
                var judged = verified + rejected;
                return new MiningPrecisionRow(
                    g.Key.Kind, g.Key.Model, g.Count(), candidates, verified, rejected,
                    Precision: judged == 0 ? 0.0 : (double)verified / judged,
                    AcceptRate: candidates == 0 ? 0.0 : (double)verified / candidates);
            })
            .OrderByDescending(r => r.Candidates)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Model, StringComparer.Ordinal)];
    }

    /// <summary>Gemeten audit-precisie per (model × promptversie) uit de
    /// steekproef-audit-rijen (#255). Los van de poort-accept-ratio hierboven —
    /// precies dat contrast is het bestaansrecht van de audit.</summary>
    public static IReadOnlyList<AuditPrecisionRow> AuditPrecision(
        IEnumerable<InteractionAudit> audits)
    {
        ArgumentNullException.ThrowIfNull(audits);
        return [.. audits
            .GroupBy(a => (a.Model, a.PromptVersion))
            .Select(g =>
            {
                var audited = g.Count();
                var sound = g.Count(a => a.Sound);
                return new AuditPrecisionRow(
                    g.Key.Model, g.Key.PromptVersion, audited,
                    Sound: sound,
                    Incorrect: g.Count(a => !a.Correct),
                    Unsupported: g.Count(a => a.Correct && !a.SupportedByEvidence),
                    Precision: audited == 0 ? 0.0 : (double)sound / audited);
            })
            .OrderByDescending(r => r.Audited)
            .ThenBy(r => r.Model, StringComparer.Ordinal)
            .ThenBy(r => r.PromptVersion, StringComparer.Ordinal)];
    }

    /// <summary>Kosten/latency per retrieval-modus. Gesorteerd op meeste runs (de
    /// modi die het zwaarst wegen op het budget bovenaan).</summary>
    public static IReadOnlyList<RetrievalModeCostRow> RetrievalCost(
        IEnumerable<RetrievalModeCostSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return [.. samples
            .GroupBy(s => s.Mode, StringComparer.Ordinal)
            .Select(g => new RetrievalModeCostRow(
                g.Key,
                g.Count(),
                g.Average(s => s.LatencyMs),
                g.Average(s => (double)s.Tokens),
                g.Sum(s => (long)s.Tokens)))
            .OrderByDescending(r => r.Runs)
            .ThenBy(r => r.Mode, StringComparer.Ordinal)];
    }
}

/// <summary>Het samengestelde ops-observability-rapport voor de admin-tegels (#231,
/// inzicht #236). Bundelt de drift-/precisie-/kosten-rollups tot één queryable
/// snapshot. Elke sectie is optioneel (null als de bron ontbreekt) zodat een tegel
/// die maar één signaal toont niet de hele bundel hoeft te vullen.</summary>
public sealed record ObservabilityReport(
    DateTimeOffset TakenAt,
    IReadOnlyList<GraphDriftEntry> GraphDrift,
    CanonicalDriftSnapshot? CanonicalDrift,
    IReadOnlyList<MiningPrecisionRow> MiningPrecision,
    IReadOnlyList<RetrievalModeCostRow> RetrievalCost,
    HypothesisYield? HypothesisYield,
    CommunityHealthRow? CommunityHealth,
    IReadOnlyList<AuditPrecisionRow> AuditPrecision)
{
    /// <summary>Bouw het rapport uit de rauwe bronnen. De aanroeper levert de
    /// tellingen/rijen; dit rolt ze samen. GraphDrift en CanonicalDrift zijn al
    /// snapshots (fase 1) en worden onaangeraakt doorgegeven.</summary>
    public static ObservabilityReport Build(
        DateTimeOffset takenAt,
        IReadOnlyList<GraphDriftEntry>? graphDrift = null,
        CanonicalDriftSnapshot? canonicalDrift = null,
        IEnumerable<MiningRun>? miningRuns = null,
        IEnumerable<RetrievalModeCostSample>? retrievalCost = null,
        HypothesisYield? hypothesisYield = null,
        CommunityHealthRow? communityHealth = null,
        IEnumerable<InteractionAudit>? interactionAudits = null) =>
        new(
            takenAt,
            graphDrift ?? [],
            canonicalDrift,
            miningRuns is null ? [] : ObservabilityRollups.MiningPrecision(miningRuns),
            retrievalCost is null ? [] : ObservabilityRollups.RetrievalCost(retrievalCost),
            hypothesisYield,
            communityHealth,
            interactionAudits is null ? [] : ObservabilityRollups.AuditPrecision(interactionAudits));
}
