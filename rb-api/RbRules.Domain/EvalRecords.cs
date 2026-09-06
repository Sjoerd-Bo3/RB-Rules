namespace RbRules.Domain;

/// <summary>De persistente vorm van één <see cref="BaselineCell"/> (Postgres
/// <c>eval_baseline</c>, #231). Eén rij per (ring × question_class × metric): de
/// vastgelegde verdeling waartegen de <see cref="BaselineDiffGate"/> de volgende run
/// diff't. <see cref="PromptContractHash"/> stempelt het gehashte judge-prompt-
/// contract (spec §7: judge-calls temperature 0, promptcontract gehasht) zodat een
/// baseline die onder een ander contract is opgenomen herkenbaar veroudert.</summary>
public class EvalBaselineRecord
{
    public long Id { get; set; }

    /// <summary>De ring (<see cref="EvalRing"/>) als string.</summary>
    public required string Ring { get; set; }

    /// <summary>De question_class (<see cref="EvalQueryType"/>) als string.</summary>
    public required string QueryType { get; set; }

    /// <summary>De metriek (<see cref="EvalMetricNames"/>).</summary>
    public required string Metric { get; set; }

    public double Mean { get; set; }
    public double StdDev { get; set; }
    public int SampleCount { get; set; }

    /// <summary>Git-SHA van de build die de baseline vastlegde (reproduceerbaarheid).</summary>
    public string? GitSha { get; set; }

    /// <summary>Hash van het judge-promptcontract waaronder de baseline is opgenomen.</summary>
    public string? PromptContractHash { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Naar de pure gate-vorm. Ongeldige ring/query-type in de rij is een
    /// datafout — de aanroeper vangt de <see cref="FormatException"/>.</summary>
    public BaselineCell ToCell() => new(
        Enum.Parse<EvalRing>(Ring),
        Enum.Parse<EvalQueryType>(QueryType),
        Metric, Mean, StdDev, SampleCount);

    /// <summary>Bouw een rij uit een gate-cel (voor het vastleggen van een verse
    /// baseline na een geaccordeerde run).</summary>
    public static EvalBaselineRecord FromCell(
        BaselineCell cell, string? gitSha = null, string? promptContractHash = null) => new()
    {
        Ring = cell.Ring.ToString(),
        QueryType = cell.QueryType.ToString(),
        Metric = cell.Metric,
        Mean = cell.Mean,
        StdDev = cell.StdDev,
        SampleCount = cell.SampleCount,
        GitSha = gitSha,
        PromptContractHash = promptContractHash,
    };
}

/// <summary>De persistente samenvatting van één harness-gate-run (Postgres
/// <c>eval_run</c>, #231). Bewaart de uitslag + de epoch-stempels (model/prompt/
/// git-SHA) zodat "sluipende degradatie" over runs heen zichtbaar wordt en een
/// baseline-diff aan een concrete run te koppelen is. De per-case-details leven in
/// het (nog te bedraden) eval_case-corpus; deze rij is de admin-tegel-rollup.</summary>
public class EvalRunRecord
{
    /// <summary>ULID — tijd-sorteerbaar.</summary>
    public required string Id { get; set; }

    /// <summary>De ring (<see cref="EvalRing"/>) als string.</summary>
    public required string Ring { get; set; }

    public string? GitSha { get; set; }
    public string? LlmModel { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>Haalde de run de gate (geen regressie op een meetellende cel,
    /// geen harde overtreding)?</summary>
    public bool Passed { get; set; }

    public int CaseCount { get; set; }
    public int GatingFailureCount { get; set; }
    public int ShadowCount { get; set; }

    /// <summary>Menselijk-leesbare memo (bv. welke klasse/metriek regresseerde).</summary>
    public string? Memo { get; set; }

    /// <summary>Per-case-uitkomst van deze run (#387): JSON-lijst van
    /// {caseId, status, counted, metrics, violations} — het beheerpaneel toont
    /// hem, en een latere run vergelijkt ertegen. Additief; null bij runs van
    /// vóór #387.</summary>
    public string? ResultsJson { get; set; }

    /// <summary>De <c>ClassifiedSample</c>-stroom van deze run als JSON (#387),
    /// zodat "leg deze run vast als baseline" later nog kan zonder de run te
    /// herhalen.</summary>
    public string? SamplesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>De persistente vorm van één <see cref="EvalCase"/> (Postgres
/// <c>eval_case</c>, #387). Tot #387 leefde de gouden set alleen als
/// test-fixture van vijf illustratieve gevallen; sinds #387 groeit hij uit
/// echt verkeer (promotie van een <c>AskTrace</c> of een
/// <c>answer_memory</c>-rij, <see cref="EvalCasePromotion"/>). De lijst-velden
/// staan als JSON: de harness leest ze als geheel, nooit per element.</summary>
public class EvalCaseRecord
{
    /// <summary><see cref="EvalCasePromotion.CaseId"/>: "eval-{slug}-{hash}".</summary>
    public required string Id { get; set; }
    public required string Question { get; set; }
    /// <summary><see cref="EvalQueryType"/> als string.</summary>
    public required string QueryType { get; set; }
    /// <summary><see cref="EvalStatus"/> als string (shadow | active | retired).</summary>
    public string Status { get; set; } = "shadow";
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public string? SupersededByErratum { get; set; }
    /// <summary>JSON-lijst van ids (section:… / card:…).</summary>
    public string GoldSupportJson { get; set; } = "[]";
    public string ExpectedCitationsJson { get; set; } = "[]";
    /// <summary>JSON-lijst van {id, text, supersededByErratum}.</summary>
    public string ForbiddenClaimsJson { get; set; } = "[]";
    /// <summary>Herkomst: trace | memory (<see cref="EvalCasePromotion"/>).</summary>
    public string? Origin { get; set; }
    /// <summary>Id van de trace/geheugenrij waaruit het geval kwam.</summary>
    public long? OriginRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
