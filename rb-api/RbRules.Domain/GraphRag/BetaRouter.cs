namespace RbRules.Domain.GraphRag;

/// <summary>Gewichten van de β(q)-router (OMD-GraphRAG, §4). Als record zodat
/// tests ze kunnen variëren; de defaults zijn gekozen zodat een duidelijk
/// entity-dichte vraag boven β=0.5 uitkomt (graph-kanaal) en een duidelijk
/// abstracte vraag eronder (community-kanaal).</summary>
public sealed record BetaWeights(double EntityWeight, double AbstractionWeight, double Bias)
{
    public static readonly BetaWeights Default = new(EntityWeight: 6.0, AbstractionWeight: 5.0, Bias: -0.5);
}

/// <summary>De signalen waaruit β(q) wordt berekend (§4). Puur afgeleid van de
/// gelinkte vraag: hoeveel entiteiten en hoe abstract. Bewust genormaliseerd
/// (0..1) zodat de gewichten interpreteerbaar blijven.</summary>
public readonly record struct QuestionSignals(double EntityDensity, double Abstraction)
{
    /// <summary>Entity-dichtheid = gelinkte entiteiten per inhoudswoord, geklemd op
    /// 1.0 (twee entiteiten in een korte vraag is al maximaal dicht). Abstractie =
    /// aandeel abstractie-cues (zie <see cref="AbstractionLexicon"/>) plus een
    /// bonus wanneer er geen enkele entiteit gelinkt is (een vraag zonder anker is
    /// per definitie abstract/breed).</summary>
    public static QuestionSignals From(int linkedEntities, int contentWords, int abstractionCues)
    {
        var words = Math.Max(1, contentWords);
        var density = Math.Clamp(linkedEntities / (double)words * 3.0, 0, 1);
        var abstraction = Math.Clamp(
            abstractionCues / (double)words * 4.0 + (linkedEntities == 0 ? 0.5 : 0.0), 0, 1);
        return new(density, abstraction);
    }
}

/// <summary>De β(q)-router (§4): <c>S_final = β·S_graph + (1−β)·S_comm</c> met
/// <c>β(q) = sigmoid(w1·entity-dichtheid − w2·abstractie)</c>. Entity-dicht → β↑
/// → het graph-kanaal domineert; abstract → β↓ → het community-summary-kanaal.
/// Puur en getest; de kanaal-scores S_graph/S_comm komen van de retrievers.</summary>
public static class BetaRouter
{
    /// <summary>β(q) ∈ (0,1). Boven 0.5 = graph-kanaal leidt, eronder =
    /// community-kanaal leidt.</summary>
    public static double Beta(QuestionSignals q, BetaWeights? weights = null)
    {
        var w = weights ?? BetaWeights.Default;
        var z = w.EntityWeight * q.EntityDensity - w.AbstractionWeight * q.Abstraction + w.Bias;
        return Sigmoid(z);
    }

    /// <summary>Gemengde eindscore. <paramref name="graphScore"/> is het
    /// getypeerde-graaf/DRIFT-kanaal, <paramref name="communityScore"/> het
    /// community-summary-kanaal.</summary>
    public static double Blend(double beta, double graphScore, double communityScore)
    {
        var b = Math.Clamp(beta, 0, 1);
        return b * graphScore + (1 - b) * communityScore;
    }

    /// <summary>Welk kanaal leidt bij deze β — puur voor tracing/tests en voor de
    /// modus-selector (entity-dicht → graph-modi, abstract → Global).</summary>
    public static bool GraphChannelLeads(double beta) => beta >= 0.5;

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));
}
