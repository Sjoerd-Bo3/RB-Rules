namespace RbRules.Domain;

/// <summary>Afwijkend settotaal binnen één set (bronruis): riftcodex levert
/// bijv. OPP-promo's met totaal 298 naast 024. Het meest voorkomende totaal
/// wint als basistotaal; de rest wordt gerapporteerd, niet weggegooid.</summary>
public record SetTotalDeviation(int Total, int Count);

/// <summary>Dekking van één set (#145), afgeleid uit de riftbound-id's zelf:
/// "ogn-074-298" codeert nummer 74 van 298. Basisnummers zijn de naamloze
/// printings 1..totaal; alles daarbuiten (suffix a/star, overnumbered,
/// tokens/runes/specials) telt als boven-basis-variant.</summary>
public record SetCoverageRow(
    string SetId, int? BaseTotal, int Present, IReadOnlyList<int> MissingNumbers,
    int Variants, IReadOnlyList<SetTotalDeviation> TotalDeviations);

/// <summary>Set-dekking (#145): parse + aggregatie per set, puur op id's —
/// unit-getest op de echte vormen (ster, -star, suffix a, tokens, sp-reeks).</summary>
public static class SetCoverage
{
    public static IReadOnlyList<SetCoverageRow> Aggregate(IEnumerable<string> riftboundIds)
    {
        var parsed = new List<RiftboundIdParts>();
        foreach (var id in riftboundIds)
            if (RiftboundIds.TryParse(id, out var parts))
                parsed.Add(parts);

        return [.. parsed
            .GroupBy(p => p.SetCode)
            .Select(AggregateSet)
            .OrderBy(r => r.SetId, StringComparer.Ordinal)];
    }

    private static SetCoverageRow AggregateSet(IGrouping<string, RiftboundIdParts> set)
    {
        // Meest voorkomende totaal wint; bij gelijke stand het hoogste
        // (liever te veel als ontbrekend rapporteren dan gaten verzwijgen).
        // Specials stemmen niet mee: de sp-reeks draagt een eigen subtotaal
        // ("ven-sp3-006") dat het echte settotaal anders kan overstemmen —
        // hun subtotaal blijft wel zichtbaar als afwijking.
        var votes = set
            .Where(p => !p.IsSpecial && p.SetTotal is not null)
            .GroupBy(p => p.SetTotal!.Value)
            .Select(g => new SetTotalDeviation(g.Key, g.Count()))
            .OrderByDescending(t => t.Count).ThenByDescending(t => t.Total)
            .ToList();
        var baseTotal = votes.Count > 0 ? votes[0].Total : (int?)null;
        var deviations = set
            .Where(p => p.SetTotal is not null && p.SetTotal != baseTotal)
            .GroupBy(p => p.SetTotal!.Value)
            .Select(g => new SetTotalDeviation(g.Key, g.Count()))
            .OrderByDescending(t => t.Count).ThenByDescending(t => t.Total)
            .ToList();

        // Basisnummers tellen alleen bij het winnende totaal: een rij met
        // afwijkend totaal ("opp-005-298" naast basistotaal 024) mag een
        // écht gat op dat nummer niet maskeren — die rij telt als variant.
        var presentBase = set
            .Where(p => !p.IsSpecial && p.Suffix.Length == 0
                        && p.SetTotal == baseTotal
                        && p.Number >= 1 && p.Number <= baseTotal)
            .Select(p => p.Number)
            .ToHashSet();
        var missing = baseTotal is { } total
            ? Enumerable.Range(1, total).Where(n => !presentBase.Contains(n)).ToList()
            : [];

        // Boven-basis: gesuffixte printings, overnumbered (nummer > totaal),
        // specials (tokens/runes/sp-reeks) en afwijkend-totaal-rijen.
        var variants = set.Count(p =>
            p.IsSpecial || p.Suffix.Length > 0
            || (p.SetTotal is not null && p.SetTotal != baseTotal)
            || (baseTotal is { } t && p.Number > t));

        return new(set.Key, baseTotal, presentBase.Count, missing, variants, deviations);
    }
}
