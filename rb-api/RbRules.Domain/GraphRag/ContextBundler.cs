namespace RbRules.Domain.GraphRag;

/// <summary>Eén te bundelen contextfragment (§4): de atoom-eenheid waaruit de
/// prompt-context wordt opgebouwd. Draagt zijn tier, trust-vector, relevantie en —
/// cruciaal — de corroboratie-tellingen voor het machine-leesbare trust-label.</summary>
public sealed record BundleItem(
    BrainRef Ref, KnowledgeTier Tier, string Text, double Relevance, TrustVector Trust,
    int IndependentSources = 0, int TotalSources = 0, string? WidgetMarker = null)
{
    /// <summary>Trust-gewogen relevantie — de rangschik-sleutel binnen een tier
    /// (§4: <c>decay(hop)·cos·trust_weight·edge_confidence</c>, hier op fragment-
    /// niveau samengevat tot relevantie·trust).</summary>
    public double Score => Math.Clamp(Relevance, 0, 1) * Trust.Weight;

    public static BundleItem From(RetrievedChunk c, string? widget = null) =>
        new(c.Ref, c.Tier, c.Text, c.Relevance, c.Trust, c.IndependentSources, c.TotalSources, widget);

    public static BundleItem From(CommunitySummary s) =>
        new(s.Ref, s.Tier, s.Text, s.Relevance, TrustVector.For(s.Tier, Verification.LexicallySupported),
            0, 0, null);
}

/// <summary>Eén item in de uiteindelijke bundel: het fragment, zijn stabiele
/// citation-nummer en zijn trust-label.</summary>
public sealed record BundledCitation(int N, BundleItem Item, string TrustLabel);

/// <summary>De uitkomst van de bundeling (§4): de geordende, gebudgetteerde items,
/// wat er is afgekapt, en het totale token-verbruik. Transparant zodat de trace
/// kan verantwoorden wat wél en niet meegewogen is (inzicht #236).</summary>
public sealed record ContextBundle(
    IReadOnlyList<BundledCitation> Items,
    IReadOnlyList<BundleItem> Dropped,
    int EstimatedTokens)
{
    public static readonly ContextBundle Empty = new([], [], 0);
}

/// <summary>De ContextBundler (§4): één trust-geordende, gebudgetteerde bundel met
/// machine-leesbare labels, MMR binnen elke laag, en harde afkap van ONDERAF
/// (community/meta valt eerst weg). Citaties volgen uit de structuur: elk item
/// krijgt een stabiel nummer in de eindvolgorde. Puur en volledig getest.</summary>
public static class ContextBundler
{
    /// <summary>Grove token-schatting: ~4 tekens per token (bge-m3/Claude-orde).
    /// Deterministisch; de exacte tokenizer is een integratie-detail.</summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>MMR-λ: hoeveel relevantie weegt t.o.v. diversiteit binnen een laag.
    /// 0.7 = relevantie leidt, maar bijna-duplicaten worden onderdrukt.</summary>
    public const double MmrLambda = 0.7;

    /// <summary>De authority-orde waarin de tiers de bundel in gaan (officieel
    /// eerst). De afkap werkt van onderaf, dus meta/community sneuvelt het eerst.</summary>
    private static readonly KnowledgeTier[] TierOrder =
        [KnowledgeTier.Official, KnowledgeTier.VerifiedRuling, KnowledgeTier.Primer,
         KnowledgeTier.Community, KnowledgeTier.Meta];

    public static ContextBundle Bundle(
        IEnumerable<BundleItem> items, int tokenBudget,
        Func<BundleItem, BundleItem, double>? similarity = null,
        int startCitationId = 1,
        double mmrLambda = MmrLambda)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sim = similarity ?? DefaultSimilarity;
        var all = items as IReadOnlyList<BundleItem> ?? [.. items];
        if (all.Count == 0) return ContextBundle.Empty;

        // 1) Per tier: MMR-ordening (relevantie vs. diversiteit).
        var orderedByTier = new List<BundleItem>();
        foreach (var tier in TierOrder)
        {
            var layer = all.Where(i => i.Tier == tier).ToList();
            if (layer.Count > 0) orderedByTier.AddRange(Mmr(layer, sim, mmrLambda));
        }
        // Onbekende/toekomstige tiers (defensief) achteraan, na meta.
        orderedByTier.AddRange(all.Where(i => !TierOrder.Contains(i.Tier)));

        // 2) Harde afkap van ONDERAF: vul van boven tot het budget vol is.
        var kept = new List<BundleItem>();
        var dropped = new List<BundleItem>();
        var used = 0;
        foreach (var item in orderedByTier)
        {
            var cost = EstimateTokens(item.Text);
            if (used + cost <= tokenBudget || kept.Count == 0 && cost > tokenBudget)
            {
                // Neem het item; sta één over-budget item toe als er nog niets in zit
                // (anders zou een enkele lange officiële sectie een lege bundel geven).
                kept.Add(item);
                used += cost;
            }
            else
            {
                dropped.Add(item);
            }
        }

        // 3) Stabiele citation-nummering in de eindvolgorde.
        var n = startCitationId;
        var citations = kept.Select(item =>
            new BundledCitation(n++, item,
                TrustLabels.For(item.Tier, item.Trust, item.IndependentSources, item.TotalSources)))
            .ToList();

        return new(citations, dropped, used);
    }

    /// <summary>Maximal Marginal Relevance binnen één laag: kies iteratief het item
    /// met de hoogste <c>λ·score − (1−λ)·max-similarity-tot-al-gekozen</c>. Stabiel
    /// getie-breakt op de ref zodat de volgorde deterministisch is.</summary>
    private static List<BundleItem> Mmr(
        List<BundleItem> layer, Func<BundleItem, BundleItem, double> sim, double lambda)
    {
        var remaining = new List<BundleItem>(layer);
        var selected = new List<BundleItem>(layer.Count);
        while (remaining.Count > 0)
        {
            BundleItem? best = null;
            var bestVal = double.NegativeInfinity;
            foreach (var cand in remaining)
            {
                var maxSim = selected.Count == 0 ? 0.0 : selected.Max(s => sim(cand, s));
                var mmr = lambda * cand.Score - (1 - lambda) * maxSim;
                if (mmr > bestVal ||
                    (mmr == bestVal && best is not null &&
                     string.CompareOrdinal(cand.Ref.Format(), best.Ref.Format()) < 0))
                {
                    bestVal = mmr;
                    best = cand;
                }
            }
            selected.Add(best!);
            remaining.Remove(best!);
        }
        return selected;
    }

    private static double DefaultSimilarity(BundleItem a, BundleItem b) =>
        Trigrams.Similarity(
            AliasNormalizer.Normalize(a.Text.Length > 120 ? a.Text[..120] : a.Text),
            AliasNormalizer.Normalize(b.Text.Length > 120 ? b.Text[..120] : b.Text));

    /// <summary>De trust-candidaten uit een bundel voor de <see cref="TrustGate"/>:
    /// (tier, trust-scalar) per item.</summary>
    public static IReadOnlyList<TrustCandidate> ToTrustCandidates(IEnumerable<BundleItem> items) =>
        [.. items.Select(i => new TrustCandidate(i.Tier, i.Trust.Weight))];
}
