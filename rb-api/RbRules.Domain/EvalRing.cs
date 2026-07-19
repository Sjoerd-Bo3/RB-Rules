namespace RbRules.Domain;

/// <summary>De drie CI-ringen van de eval-harness (#231, spec §7). Elke ring
/// verschilt in wanneer hij draait en of hij LLM-kosten maakt — het scaffold
/// dráágt alle ringen als pure logica; wélke ring een run gate is de
/// verantwoordelijkheid van de (nog te bedraden) CI-runner.</summary>
public enum EvalRing
{
    /// <summary>Ring A — deterministisch, €0, ELKE PR, blokkerend. Subgraph-/
    /// path-recall, citation-validity, provenance-completeness, embedding-dim-
    /// guard. De goedkope hallucinatie-verzekering (<see cref="EvalGateEvaluator"/>).</summary>
    A,
    /// <summary>Ring B — LLM-judge op de kern-set (~60 cases), alleen bij PR's die
    /// retrieval/prompt/ontologie raken. Faithfulness, citation-support,
    /// contradiction-recall — begrensd en gecached (<see cref="RetrievalQualityScoring"/>).</summary>
    B,
    /// <summary>Ring C — volledig + meta, nachtelijk/pre-release. Volledige set,
    /// alle modi, drift-rapport, answer-consistency onder parafrase.</summary>
    C,
}

/// <summary>De canonieke metriek-namen waarop de baseline-diff-per-klasse-gate
/// (<see cref="BaselineDiffGate"/>) opereert. Bewust string-getypeerd zodat de
/// baseline-tabel (Postgres <c>eval_baseline</c>) per (ring × question_class ×
/// metric) één rij draagt zonder een enum-migratie bij elke nieuwe meetlaag.
/// Hoger = beter voor állemaal (een regressie is altijd een DALING) — dat maakt
/// de "&gt; kσ onder baseline"-poort uniform.</summary>
public static class EvalMetricNames
{
    /// <summary>Ring A/B — subgraph-recall (<see cref="EvalScoringService.Recall"/>).</summary>
    public const string Recall = "recall";
    /// <summary>Ring A/B — retrieval-precisie (<see cref="EvalScoringService.Relevancy"/>).</summary>
    public const string Relevancy = "relevancy";
    /// <summary>Ring A — path-recall op gekwalificeerde interacties (structuurverlies).</summary>
    public const string PathRecall = "path_recall";
    /// <summary>Ring A — citation-validity (geciteerd ∈ verwacht).</summary>
    public const string CitationPrecision = "citation_precision";
    /// <summary>Ring A/B — citation-support/groundedness (geciteerd ∈ opgehaalde subgraaf).</summary>
    public const string CitationSupport = "citation_support";
    /// <summary>Ring B — answer-faithfulness (aandeel SUPPORTED claims, met vangnet).</summary>
    public const string Faithfulness = "faithfulness";
    /// <summary>Ring B/C — contradiction-recall (vermeden forbidden claims).</summary>
    public const string ContradictionRecall = "contradiction_recall";
    /// <summary>Ring C — answer-consistency onder parafrase.</summary>
    public const string Consistency = "consistency";

    /// <summary>Alle namen — voor validatie en rapportage-volgorde.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Recall, Relevancy, PathRecall, CitationPrecision,
        CitationSupport, Faithfulness, ContradictionRecall, Consistency,
    ];
}
