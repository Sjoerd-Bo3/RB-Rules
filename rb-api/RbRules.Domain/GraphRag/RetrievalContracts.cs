namespace RbRules.Domain.GraphRag;

/// <summary>Een knoop in een opgehaalde subgraaf/pad (§4). <see cref="Tier"/> draagt
/// de kennispiramide-laag (voor trust-weging en labels), <see cref="Text"/> de
/// eventueel gekoppelde brontekst.</summary>
public sealed record GraphNode(BrainRef Ref, KnowledgeTier Tier, string Label, string? Text = null);

/// <summary>Een getypeerde, gerichte edge (§4). <see cref="EdgeType"/> is de
/// canonieke Neo4j-edge-naam (SCREAMING_SNAKE_CASE, uit
/// <see cref="Ontology.OntologyRelation.EdgeName"/>). <see cref="Confidence"/> is
/// het edge-vertrouwen (0..1); <see cref="Qualifiers"/> de gedenormaliseerde
/// projectie-parameters (window/actor_status/cost_delta, §0).</summary>
public sealed record GraphEdge(
    BrainRef From, BrainRef To, string EdgeType, double Confidence,
    IReadOnlyDictionary<string, string>? Qualifiers = null);

/// <summary>Eén stap in een pad: de edge plus het trust-gewicht en vertrouwen die
/// de pad-scoring gebruikt (§4: gewogen op <c>1/(trust·confidence)</c>).</summary>
public sealed record PathHop(GraphNode Target, GraphEdge Edge, double TrustWeight, double Confidence);

/// <summary>Een getypeerd pad door de graaf (§4, de onderscheidende feature): de
/// startknoop + de stappen. Het pad ÍS de uitleg én de citatiestructuur —
/// <see cref="PathCitations"/> zet het om in geordende citaties met widget-markers.</summary>
public sealed record GraphPath(GraphNode Start, IReadOnlyList<PathHop> Steps)
{
    public IEnumerable<GraphNode> Nodes => new[] { Start }.Concat(Steps.Select(s => s.Target));
    public GraphNode End => Steps.Count > 0 ? Steps[^1].Target : Start;
}

/// <summary>Een opgehaalde tekst-chunk (regelsectie, kaartfeit, ruling, claim) met
/// zijn relevantie en tier (§4). De atoom-eenheid die de <see cref="ContextBundler"/>
/// budgetteert.</summary>
public sealed record RetrievedChunk(
    BrainRef Ref, KnowledgeTier Tier, string Text, double Relevance, TrustVector Trust,
    int IndependentSources = 0, int TotalSources = 0);

/// <summary>Een community-summary (§4, Global-modus): hergebruikte primer/sectie-
/// dossiers als L0/L1 (beslissing §0 — geen tweede synthese-laag). <see cref="Level"/>
/// is de Leiden-resolutie (L0 fijn … L2 grof).</summary>
public sealed record CommunitySummary(
    string CommunityId, int Level, string Title, string Text, BrainRef Ref,
    KnowledgeTier Tier, double Relevance);

/// <summary>Wat een retriever teruggeeft: de subgraaf (knopen+edges), de gekoppelde
/// chunks en eventuele paden. Leeg = niets gevonden (netjes, nooit een exception —
/// AI/graaf-uitval is een verwacht pad, §7).</summary>
public sealed record RetrievalResult(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<GraphPath> Paths,
    IReadOnlyList<CommunitySummary> Communities)
{
    public static readonly RetrievalResult Empty = new([], [], [], [], []);
    public bool IsEmpty => Nodes.Count == 0 && Chunks.Count == 0 && Paths.Count == 0 && Communities.Count == 0;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Poorten (ports) — de naden waarlangs de PURE orchestratie de daadwerkelijke
//  Neo4j/pgvector/GDS-queries aanroept. De implementaties zijn een gedocumenteerde
//  INTEGRATIE-FOLLOW-UP (docs/ARCHITECTURE §graphrag): Neo4j/GDS en live-pgvector
//  draaien niet in CI, dus de retrieval-queries leven straks in Infrastructure-
//  adapters. De orchestrator en alle beslislogica testen met in-memory fakes.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Levert de gazetteer (canonieke labels + aliassen + kaart-/sectienamen)
/// voor de entity-linker. Follow-up-implementatie leest <c>CanonicalEntity</c>,
/// <c>Card</c> en <c>RuleChunk</c> uit Postgres.</summary>
public interface IGazetteerSource
{
    Task<Gazetteer> BuildAsync(CancellationToken ct = default);
}

/// <summary>Context-embedding-similarity per kandidaat-knoop (de β·cos-as van de
/// linker). Follow-up: cosine tegen de vraag-embedding over pgvector-node-embeddings.
/// Uitval → een functie die 0 teruggeeft (embedding-degradatie, §4).</summary>
public interface INodeContextSimilarity
{
    Task<Func<BrainRef, double>> ForQuestionAsync(string question, CancellationToken ct = default);
}

/// <summary>Zijn twee kandidaat-knopen via een getypeerde edge verbonden (de
/// co-mention-coherentie/graaf-truc)? Follow-up: een begrensde Neo4j-check.</summary>
public interface INodeAdjacency
{
    Task<Func<BrainRef, BrainRef, bool>> ForCandidatesAsync(
        IReadOnlyList<BrainRef> candidates, CancellationToken ct = default);
}

/// <summary>De vier retrieval-modi als poorten. Elke methode is idempotent en
/// zij-effect-vrij; uitval geeft <see cref="RetrievalResult.Empty"/> terug.</summary>
public interface IGraphRetriever
{
    /// <summary>Local: getypeerde, gerichte k-hop BFS + gekoppelde chunks, met
    /// edge-whitelist per intent en hub-graad-cap.</summary>
    Task<RetrievalResult> LocalAsync(IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default);

    /// <summary>Global: map-reduce over community-summaries (primer/sectie-dossiers).</summary>
    Task<RetrievalResult> GlobalAsync(string question, ModeSelection mode, CancellationToken ct = default);

    /// <summary>Path: k-shortest trust-gewogen paden tussen de ankers (GDS Yen/
    /// Dijkstra op de vooraf-geprojecteerde, gepinde named-graph, beslissing #232).</summary>
    Task<RetrievalResult> PathAsync(IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default);

    /// <summary>DRIFT: vector-seed → anchor-fusie → typed-edge-expansie → trust
    /// re-rank → begrensde follow-up-hop.</summary>
    Task<RetrievalResult> DriftAsync(string question, IReadOnlyList<BrainRef> anchors, ModeSelection mode, CancellationToken ct = default);
}
