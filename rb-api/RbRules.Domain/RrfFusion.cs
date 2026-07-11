namespace RbRules.Domain;

/// <summary>Reciprocal Rank Fusion (#44): meerdere ranked lijsten (vector-
/// zoek, full-text, …) samenvoegen tot één rangorde. Stond bewust dubbel in
/// AskService en RuleSearchService — nu één pure, unit-testbare helper.
/// Score per item: som over de lijsten van 1/(k + rang + 1), plus een
/// optionele bonus per voorkomen (bijv. bron-bias per vraagtype).</summary>
public static class RrfFusion
{
    /// <summary>Standaard demping (k=60, de gebruikelijke literatuurwaarde):
    /// hoge posities wegen zwaarder, maar één toplijst domineert niet.</summary>
    public const int DefaultK = 60;

    public static List<TKey> Fuse<TItem, TKey>(
        IEnumerable<IEnumerable<TItem>> rankedLists,
        Func<TItem, TKey> key,
        int take,
        Func<TItem, double>? bonus = null,
        int k = DefaultK) where TKey : notnull
    {
        var scores = new Dictionary<TKey, double>();
        foreach (var list in rankedLists)
        {
            var rank = 0;
            foreach (var item in list)
            {
                var id = key(item);
                scores[id] = scores.GetValueOrDefault(id)
                    + 1.0 / (k + rank + 1)
                    + (bonus?.Invoke(item) ?? 0);
                rank++;
            }
        }
        return [.. scores
            .OrderByDescending(kv => kv.Value)
            .Take(take)
            .Select(kv => kv.Key)];
    }

    /// <summary>Variant voor lijsten die zelf al de sleutel zijn (id-lijsten).</summary>
    public static List<T> Fuse<T>(
        IEnumerable<IEnumerable<T>> rankedLists, int take, int k = DefaultK)
        where T : notnull =>
        Fuse(rankedLists, item => item, take, bonus: null, k);
}
