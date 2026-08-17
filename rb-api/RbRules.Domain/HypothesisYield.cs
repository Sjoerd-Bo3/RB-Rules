namespace RbRules.Domain;

/// <summary>De MEETBARE precisie-/kostenwinst van de <see cref="HypothesisEngine"/>
/// t.o.v. blinde N²-kandidaatgeneratie (fase 5, #229, §5 — kritiek B7: géén verzonnen
/// vaste factor). Alle velden worden UIT DE DATA berekend: het blinde-baseline-
/// paaraantal, het werkelijke hypothese-kandidaataantal, en — als een gouden set
/// bekende echte interacties wordt meegegeven — de gemeten precisie en de precisie-
/// lift t.o.v. de blinde base-rate. Zo staat er in de admin/telemetrie een echt getal
/// ("op deze N kaarten: 12 kandidaten i.p.v. 457.000 — 38.000× minder LLM-calls,
/// precisie 0,33 vs base-rate 0,004"), geen folklore-multiplier.</summary>
/// <param name="EntityCount">Aantal holders (n) — de blinde baseline is n·(n−1)/2.</param>
/// <param name="BlindPairCount">De blinde N²-baseline: elk ongeordend paar één
/// LLM-call.</param>
/// <param name="HypothesisPairCount">Aantal UNIEKE ongeordende kandidaat-paren dat de
/// motor voortbrengt — precies de gerichte LLM-calls die overblijven.</param>
/// <param name="ReductionFactor">Blind ÷ hypothese: hoeveel minder LLM-calls. Bij nul
/// kandidaten geklemd op de blinde telling (deel-door-1) i.p.v. oneindig.</param>
/// <param name="TruePositives">Kandidaat-paren die in de gouden set zitten (null als
/// geen gouden set is meegegeven).</param>
/// <param name="Precision">TruePositives ÷ hypothese-paren (null zonder gouden set).</param>
/// <param name="BlindBaseRate">|gouden set| ÷ blinde paren — de precisie die blind
/// N²-gokken zou halen (null zonder gouden set).</param>
/// <param name="PrecisionLift">Precisie ÷ blinde base-rate — hoeveel scherper de motor
/// mikt (null zonder gouden set of bij base-rate 0).</param>
public sealed record HypothesisYield(
    int EntityCount,
    long BlindPairCount,
    int HypothesisPairCount,
    double ReductionFactor,
    int? TruePositives,
    double? Precision,
    double? BlindBaseRate,
    double? PrecisionLift)
{
    /// <summary>Meet de opbrengst van een hypothese-oogst. <paramref name="entityCount"/>
    /// is het aantal kandidaat-holders (kaarten) dat de blinde baseline zou paren;
    /// <paramref name="goldTruePairs"/> is optioneel de set bekende echte interacties
    /// als ongeordende paar-sleutels (<see cref="InteractionHypothesis.UnorderedPairKey"/>-
    /// vorm) — meegeven maakt precisie meetbaar, weglaten meet alleen de kostenwinst.</summary>
    public static HypothesisYield Measure(
        int entityCount,
        IReadOnlyCollection<InteractionHypothesis> hypotheses,
        IReadOnlySet<string>? goldTruePairs = null)
    {
        ArgumentNullException.ThrowIfNull(hypotheses);
        var n = Math.Max(0, entityCount);
        var blind = (long)n * (n - 1) / 2;

        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hypotheses) pairs.Add(h.UnorderedPairKey);
        var hypoPairs = pairs.Count;

        var reduction = (double)blind / Math.Max(1, hypoPairs);

        int? tp = null;
        double? precision = null, baseRate = null, lift = null;
        if (goldTruePairs is not null)
        {
            var hit = pairs.Count(goldTruePairs.Contains);
            tp = hit;
            precision = hypoPairs == 0 ? 0.0 : (double)hit / hypoPairs;
            baseRate = blind == 0 ? 0.0 : (double)goldTruePairs.Count / blind;
            lift = baseRate > 0 ? precision / baseRate : null;
        }

        return new HypothesisYield(n, blind, hypoPairs, reduction, tp, precision, baseRate, lift);
    }
}
