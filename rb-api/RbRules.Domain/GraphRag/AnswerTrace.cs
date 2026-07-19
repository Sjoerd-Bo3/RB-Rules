namespace RbRules.Domain.GraphRag;

/// <summary>Immutable auditspoor van één /ask-antwoord (fase 4, #228 — §6, inzicht
/// #236, "bitemporeel-licht"). Legt vast WELKE subgraaf/paden/edges/trust-gewichten
/// het antwoord droegen, plus de epoch-stempels (graphEpoch, llmModel, promptVersion,
/// embeddingRev). Twee vragen worden er later op beantwoord (§6): "verantwoord dit
/// antwoord" (de dragende <see cref="Supports"/> mét tier + trust) en "zouden we dit
/// vandaag nog zo zeggen?" (diff van deze trace tegen de huidige graaf) — die tweede
/// query is een gedocumenteerde follow-up (vereist de live-graaf).
///
/// Postgres = SoT (herbouwbaar); de Neo4j-projectie (USED_ASSERTION-edges) is een
/// integratie-follow-up. Trust is tijd-variant, dus de WAARDE-TOEN wordt bewaard
/// (<see cref="AnswerTraceSupport.TrustWeightAtQuery"/>), niet opnieuw berekend.</summary>
public class AnswerTrace
{
    /// <summary>ULID — tijd-sorteerbaar, zie <see cref="Ulid"/>.</summary>
    public required string Id { get; set; }

    public required string Question { get; set; }

    /// <summary>Het <see cref="QuestionType"/> (router-tak) als string.</summary>
    public required string QuestionType { get; set; }

    /// <summary>De primaire <see cref="RetrievalMode"/> als string.</summary>
    public required string RetrievalMode { get; set; }

    /// <summary>Terugval-reden als er gedegradeerd is (<see cref="RetrievalFallback"/>);
    /// null bij een schone run.</summary>
    public string? FallbackReason { get; set; }

    /// <summary>β(q) van de router — welk kanaal (graph vs. community) leidde.</summary>
    public double Beta { get; set; }

    /// <summary>Het primaire trust-kanaal (<see cref="PrimaryChannel"/>) uit de
    /// gating: officieel | community-badged | none.</summary>
    public required string PrimaryChannel { get; set; }

    /// <summary>De gating-memo (waarom dit kanaal) — de beslissing is zichtbaar.</summary>
    public string? GateMemo { get; set; }

    // Epoch-stempels (§6): trust is tijd-variant → bewaar de context van toen.
    /// <summary>De graaf-epoch (bv. de reasoner-run-ULID of een monotone teller)
    /// waartegen deze retrieval draaide.</summary>
    public string? GraphEpoch { get; set; }
    public string? LlmModel { get; set; }
    public string? PromptVersion { get; set; }
    public string? EmbeddingRev { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AnswerTraceSupport> Supports { get; set; } = [];

    public BrainRef Ref => BrainRef.Assertion(Id);
}

/// <summary>Eén dragend feit onder een antwoord (§6: USED_ASSERTION {wAtQueryTime}).
/// De trust-scalar wordt bevroren op query-tijd — een latere her-weging verandert de
/// verantwoording van het historische antwoord niet.</summary>
public class AnswerTraceSupport
{
    public long Id { get; set; }
    public required string AnswerTraceId { get; set; }
    public AnswerTrace? AnswerTrace { get; set; }

    /// <summary>Het citation-nummer waarmee de referee ernaar verwees.</summary>
    public int CitationN { get; set; }

    /// <summary>BrainRef van het dragende feit (section/card/claim/interaction/
    /// assertion) — de koppeling antwoord→feit zonder de feit-tabellen te vervuilen.</summary>
    public required string SubjectRef { get; set; }

    /// <summary>De kennispiramide-tier (<see cref="KnowledgeTier"/>) als string.</summary>
    public required string Tier { get; set; }

    /// <summary>De trust-scalar TOEN (<see cref="TrustVector.Weight"/> op query-tijd).</summary>
    public double TrustWeightAtQuery { get; set; }

    /// <summary>De widget-marker (<c>[[card:…]]</c>/<c>[[rule:…]]</c>/…) als die er is.</summary>
    public string? WidgetMarker { get; set; }
}

/// <summary>Epoch-context die op een <see cref="AnswerTrace"/> wordt gestempeld (§6).
/// Bewust een los record zodat de orchestrator hem uit de omgeving vult en de builder
/// puur blijft.</summary>
public sealed record TraceEpoch(
    string? GraphEpoch = null, string? LlmModel = null,
    string? PromptVersion = null, string? EmbeddingRev = null);

/// <summary>Bouwt de <see cref="AnswerTrace"/> uit de retrieval-uitkomst (§6/#236).
/// PUUR: geen IO — de orchestrator levert de bundel, de gating-beslissing en de
/// epoch; het wegschrijven naar Postgres is een dun infrastructuur-stapje. Elke
/// dragende citatie wordt één <see cref="AnswerTraceSupport"/> met de trust-waarde-
/// toen; pad-citaties tellen mee zodat ook de pad-onderbouwing verantwoordbaar is.</summary>
public static class AnswerTraceBuilder
{
    public static AnswerTrace Build(
        string question,
        QuestionType questionType,
        ModeSelection mode,
        double beta,
        TrustGateDecision gate,
        ContextBundle bundle,
        IReadOnlyList<PathCitation>? pathCitations = null,
        TraceEpoch? epoch = null,
        string? fallbackReason = null,
        string? id = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(bundle);
        var ep = epoch ?? new TraceEpoch();
        var traceId = id ?? Ulid.NewUlid();

        var trace = new AnswerTrace
        {
            Id = traceId,
            Question = question ?? "",
            QuestionType = questionType.ToString(),
            RetrievalMode = mode.Primary.ToString(),
            FallbackReason = fallbackReason,
            Beta = beta,
            PrimaryChannel = gate.Primary.ToString(),
            GateMemo = gate.Memo,
            GraphEpoch = ep.GraphEpoch,
            LlmModel = ep.LlmModel,
            PromptVersion = ep.PromptVersion,
            EmbeddingRev = ep.EmbeddingRev,
        };

        foreach (var c in bundle.Items)
            trace.Supports.Add(new AnswerTraceSupport
            {
                AnswerTraceId = traceId,
                CitationN = c.N,
                SubjectRef = c.Item.Ref.Format(),
                Tier = c.Item.Tier.ToString(),
                TrustWeightAtQuery = c.Item.Trust.Weight,
                WidgetMarker = c.Item.WidgetMarker,
            });

        if (pathCitations is not null)
            foreach (var p in pathCitations)
            {
                // Pad-citaties die al als chunk in de bundel zaten niet dubbel tellen.
                if (trace.Supports.Any(s => s.SubjectRef == p.Ref.Format())) continue;
                trace.Supports.Add(new AnswerTraceSupport
                {
                    AnswerTraceId = traceId,
                    CitationN = p.N,
                    SubjectRef = p.Ref.Format(),
                    Tier = p.Tier.ToString(),
                    TrustWeightAtQuery = p.TrustWeight,
                    WidgetMarker = p.WidgetMarker,
                });
            }

        return trace;
    }
}
