namespace RbRules.Domain;

/// <summary>Temporele precedentie (#168): naast de bestaande gezags-
/// precedentie (<see cref="Source.TrustTier"/>) nu ook wanneer iets
/// gepubliceerd/bijgewerkt is. Bij gelijk gezag wint de recentste datum —
/// bij verschillend gezag blijft gezag altijd doorslaggevend (de
/// kennispiramide, docs/KNOWLEDGE.md: officieel wint altijd, ook van een
/// gedateerde officiële bron t.o.v. een verse community-bron).
///
/// Puur en generiek over het datumtype: <see cref="Source"/> draagt
/// <see cref="DateTimeOffset"/>, <see cref="Erratum"/> en <see
/// cref="BanEntry"/> een <see cref="DateOnly"/> — allebei <c>IComparable</c>
/// structs, dus één implementatie dekt beide zonder conversieboilerplate.
/// Een ontbrekende datum (null) sorteert altijd als oudste: nooit een gok,
/// wel een voorspelbare, veilige tie-break-uitkomst.</summary>
public static class Precedence
{
    /// <summary>Fallback-tier voor een ontbrekende/onbekende bron (bijvoorbeeld
    /// een erratum waarvan de bron-URL niet meer in het register staat) —
    /// verliest altijd van elke bekende TrustTier, blijft zichtbaar in plaats
    /// van te crashen op een ontbrekende join.</summary>
    public const short UnknownTier = short.MaxValue;

    /// <summary>Vergelijkt twee (TrustTier, datum)-sleutels. Positief als de
    /// eerste (tierA, dateA) voorrang heeft op de tweede, negatief andersom,
    /// 0 bij een volledig gelijke stand (aanroeper bepaalt dan een verdere
    /// tie-break, bijvoorbeeld op detectiemoment). TrustTier telt als laagste
    /// getal = meeste gezag (bestaande conventie: 1 = officieel).</summary>
    public static int Compare<TDate>(short tierA, TDate? dateA, short tierB, TDate? dateB)
        where TDate : struct, IComparable<TDate>
    {
        if (tierA != tierB) return tierB - tierA;
        if (dateA is null && dateB is null) return 0;
        if (dateA is null) return -1;
        if (dateB is null) return 1;
        return dateA.Value.CompareTo(dateB.Value);
    }

    /// <summary>Kiest de winnaar uit een niet-lege reeks kandidaten op
    /// (TrustTier, datum) — zie <see cref="Compare{TDate}"/>. Bij een
    /// volledig gelijke stand wint de eerste kandidaat (stabiel): de
    /// aanroeper bepaalt zelf een zinvolle invoervolgorde (bijvoorbeeld
    /// detectiemoment) als verdere tie-break.</summary>
    public static T Winner<T, TDate>(
        IReadOnlyList<T> candidates, Func<T, short> tier, Func<T, TDate?> date)
        where TDate : struct, IComparable<TDate>
    {
        if (candidates.Count == 0)
            throw new ArgumentException("candidates mag niet leeg zijn", nameof(candidates));
        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (Compare(tier(c), date(c), tier(best), date(best)) > 0) best = c;
        }
        return best;
    }

    /// <summary>Stabiele her-ordening bovenop een al gerangschikte lijst (#168,
    /// /ask-citaties): binnen een aaneengesloten reeks van gelijke TrustTier
    /// wint de recentste datum. De volgorde tussen verschillende tiers — en
    /// dus de onderliggende fusie-/relevantierangorde zelf — blijft ongemoeid;
    /// dit is een tie-breaker, geen nieuwe rangorde. Items zonder datum blijven
    /// binnen zo'n reeks op hun oorspronkelijke (fusie-)plek onderling.</summary>
    public static List<T> ReorderTiedByTier<T>(
        IReadOnlyList<T> ranked, Func<T, short> tier, Func<T, DateTimeOffset?> recency)
    {
        var result = new List<T>(ranked);
        var i = 0;
        while (i < result.Count)
        {
            var j = i + 1;
            while (j < result.Count && tier(result[j]) == tier(result[i])) j++;
            if (j - i > 1)
            {
                var run = result.Skip(i).Take(j - i)
                    .Select((item, idx) => (item, idx))
                    .OrderByDescending(x => recency(x.item) ?? DateTimeOffset.MinValue)
                    .ThenBy(x => x.idx)
                    .Select(x => x.item)
                    .ToList();
                for (var k = 0; k < run.Count; k++) result[i + k] = run[k];
            }
            i = j;
        }
        return result;
    }
}
