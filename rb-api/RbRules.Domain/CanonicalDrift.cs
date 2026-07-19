namespace RbRules.Domain;

/// <summary>Eén regel in de canonieke-laag-drift-snapshot: per kind het aantal
/// levende (canonical+candidate) entiteiten, de tombstones (merged) en de
/// singletons (entiteiten die nog geen enkele alias hebben geabsorbeerd).</summary>
public sealed record CanonicalKindDrift(
    string Kind, int Live, int Candidates, int Canonical, int Tombstones, int Singletons);

/// <summary>Drift-snapshot van de canonieke laag (§Fase 1, queryable voor inzicht
/// #236): node-count per label, singleton-communities en duplicatie-schuld (open
/// merge-kandidaten). Puur: de service levert de tellingen uit Postgres, dit
/// ordent en somt alleen — geen IO, deterministisch, testbaar.</summary>
public sealed record CanonicalDriftSnapshot(
    IReadOnlyList<CanonicalKindDrift> ByKind,
    int DuplicationDebt,
    DateTimeOffset TakenAt)
{
    public int TotalLive => ByKind.Sum(k => k.Live);
    public int TotalTombstones => ByKind.Sum(k => k.Tombstones);
    public int TotalSingletons => ByKind.Sum(k => k.Singletons);

    /// <summary>Bouwt de snapshot uit ruwe per-kind-tellingen en de open-kandidaat-
    /// telling (duplicatie-schuld). De kinds komen in <see cref="CanonicalEntityKinds"/>-
    /// volgorde; onbekende kinds (mocht een toekomstige waarde binnensluipen) sluiten
    /// alfabetisch achteraan aan — een onverwachte telling blijft zo zichtbaar.</summary>
    public static CanonicalDriftSnapshot Build(
        IReadOnlyList<CanonicalKindDrift> rawByKind, int duplicationDebt, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(rawByKind);
        var order = new[]
        {
            CanonicalEntityKinds.Mechanic, CanonicalEntityKinds.Keyword, CanonicalEntityKinds.Concept,
        };
        var rank = order
            .Select((k, i) => (k, i))
            .ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
        var ordered = rawByKind
            .OrderBy(k => rank.GetValueOrDefault(k.Kind, int.MaxValue))
            .ThenBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();
        return new(ordered, duplicationDebt, takenAt);
    }
}
