using System.Text;
using System.Text.RegularExpressions;
using RbRules.Domain.Ontology;

namespace RbRules.Domain.Reasoning;

/// <summary>De families monotone inferentie (fase 3, #227, §5). Elke afgeleide
/// edge draagt zijn familie + regel-id (inzicht #236).</summary>
public enum InferenceFamily
{
    /// <summary>Property-chain: samenstelling van ontologie-relaties die in een
    /// GOVERNED_BY naar een RuleSection uitkomt.</summary>
    PropertyChain,
    /// <summary>Symmetrische sluiting van een symmetrische relatie (INTERACTS_WITH,
    /// CONTRADICTS).</summary>
    SymmetricClosure,
    /// <summary>Subproperty-collapse: een alias-kind naar zijn canonieke
    /// super-property vouwen (synoniem-proliferatie-wapening).</summary>
    SubpropertyCollapse,
}

/// <summary>Eén monotone inferentie-regel als Neo4j-native Cypher-template (fase 3,
/// #227). VASTGELEGDE BESLISSING: één engine, Neo4j-native — géén apart C#-Datalog.
/// De <see cref="Cypher"/> is een idempotente MERGE die de afgeleide edge met
/// <see cref="DerivedEdgeProvenance"/> tagt; <see cref="Rows"/> zijn de eventuele
/// UNWIND-param-rijen (dictionaries-only, de huis-conventie voor batched writes).
/// Regels zonder rijen (path-/symmetrie-regels) dragen hun hele patroon in de
/// Cypher zelf en krijgen alleen de run-provenance-parameters mee.</summary>
/// <param name="Id">Stabiel, veilig id (<see cref="InferenceRuleRegistry.IsSafeRuleId"/>)
/// dat letterlijk in de afgeleide edge belandt als <c>derivedByRule</c>.</param>
/// <param name="Name">Menselijke naam voor de admin-/inzicht-weergave.</param>
/// <param name="Family">De inferentie-familie.</param>
/// <param name="DerivedEdge">De SCREAMING_SNAKE-edge-naam die de regel materialiseert.</param>
/// <param name="Description">Wat de regel afleidt en waaruit.</param>
/// <param name="Cypher">De idempotente MERGE-template met provenance-tagging.</param>
/// <param name="Rows">UNWIND-param-rijen (leeg voor self-contained path-/symmetrie-regels).</param>
public sealed record InferenceRule(
    string Id,
    string Name,
    InferenceFamily Family,
    string DerivedEdge,
    string Description,
    string Cypher,
    IReadOnlyList<Dictionary<string, object?>> Rows);

/// <summary>Genereert de monotone inferentie-regels DETERMINISTISCH uit de ontologie
/// (fase 3, #227, §5). De ontologie (<see cref="OntologySchema"/>) is de ÉNE
/// schema-bron: relatie-domain/range, logische traits (Transitive/Symmetric) en de
/// klassenhiërarchie bepalen wélke regels bestaan — er staat nergens een losse,
/// met de hand bijgehouden regel-lijst naast. Puur en IO-loos: de service
/// (<c>ReasoningService</c>) hangt de Cypher-executie eromheen (best-effort, want
/// Neo4j zit niet in CI/lokaal — live-executie is integratie-follow-up, net als de
/// fase-2-projectie). Afgeleide edges zijn nooit bron: ze worden bij een
/// full-rebuild opnieuw gematerialiseerd, niet als Postgres-feit gepersisteerd.</summary>
public static class InferenceRuleRegistry
{
    // Veilig id-alfabet: kleine letters, cijfers, ':' '-' '_'. Wordt letterlijk in
    // de Cypher geïnterpoleerd (als derivedByRule) — een guard tegen per ongeluk
    // een quote/space in een toekomstig id smokkelen. Nooit gebruikersinvoer.
    private static readonly Regex SafeRuleId = new("^[a-z0-9:_-]+$", RegexOptions.Compiled);

    public static bool IsSafeRuleId(string id) => id is not null && SafeRuleId.IsMatch(id);

    /// <summary>Alle regels, in een stabiele familie-volgorde (property-chain →
    /// symmetrie → subproperty). Volgorde is deterministisch zodat een reasoner-run
    /// reproduceerbaar is.</summary>
    public static IReadOnlyList<InferenceRule> All =>
    [
        .. PropertyChainRules(),
        .. SymmetricClosureRules(),
        .. SubpropertyCollapseRules(OntologySchema.RelatesToKindSubProperties),
    ];

    // ── isa-closure: bewust GÉÉN materialisatie-regel ────────────────────────
    //
    // #227-review (finding #1/#2): een eerdere isa-closure-regel probeerde
    // GOVERNED_BY van een superklasse naar elke subklasse-instantie te erven, maar
    // deed dat met twee ONGEJOINDE MATCH-clausules (een 'super'-knoop én een losse
    // 'n'-knoop, zonder relatie ertussen) → een cartesisch product: élke
    // co-getypeerde instantie kreeg een afgeleide GOVERNED_BY naar élke sectie die
    // een wíllekeurige superklasse-instantie bestuurde. Concreet op de live-graaf:
    // Interaction-knopen dragen :Concept (GraphSyncService) én de enige basis-
    // GOVERNED_BY-edges; het paar (Mechanic, Concept) liet dáárdoor élke Mechanic
    // 'geregeerd' worden door de sectie van een toevallig co-gelabelde Interaction.
    // Een gedeeld label is geen subklasse-relatie TÚSSEN instanties.
    //
    // Er is geen sluitende instance-level materialisatie voor deze overerving: in
    // het multi-label model (§2.1, :Object:Card:Unit) draagt een subklasse-
    // instantie de superklasse-labels al, dus subclass-polymorfie is een
    // QUERY-TIME zaak (MATCH (:Object) matcht Units) — materialiseren voegt niets
    // toe, en een correcte same-node-join zou een no-op zijn die bovendien de
    // provenance van een bestaande basis-edge zou herschrijven. Governance-
    // overerving die WÉL nieuwe, verbonden kennis oplevert loopt uitsluitend via
    // de property-chain-regels (Card → … → GOVERNED_BY) hieronder.

    // ── property-chain → GOVERNED_BY ─────────────────────────────────────────

    /// <summary>Property-chain-regels: elke samenstelling van kale ontologie-relaties
    /// die (subclass-compatibel) van een Card via één of meer hops in een
    /// GOVERNED_BY naar een RuleSection uitkomt. Zo bereikt een Deflect-kaartvraag
    /// §7.4 in één hop zónder her-minen: <c>GOVERNED_BY(card,s) :- HAS_KEYWORD(card,kw),
    /// INVOKES(kw,m), GOVERNED_BY(m,s)</c>. De ketens worden bounded uit de ontologie
    /// afgeleid (<see cref="GovernedByChains"/>), niet met de hand opgesomd.</summary>
    public static IReadOnlyList<InferenceRule> PropertyChainRules()
    {
        var rules = new List<InferenceRule>();
        foreach (var chain in GovernedByChains())
        {
            var id = "pc:" + string.Join("-", chain.Select(r => r.EdgeName.ToLowerInvariant()));
            var cypher = RenderChainCypher(chain, id);
            rules.Add(new(id,
                "Property-chain " + string.Join(" ∘ ", chain.Select(r => r.EdgeName)),
                InferenceFamily.PropertyChain,
                OntologySchema.Relations[RelationType.GovernedBy].EdgeName,
                "Leidt GOVERNED_BY(Card,RuleSection) af via de keten " +
                string.Join(" → ", chain.Select(r => r.EdgeName)) + ".",
                cypher, []));
        }
        return rules;
    }

    /// <summary>Relaties die GEEN kennis dragen en dus nooit een inferentie-hop zijn:
    /// de gedenormaliseerde retrieval-projectie RELATES_TO ("nooit bron van waarheid")
    /// en de pre-ontologische INTERACTS_WITH-hint ("geen kennis, wel provenance"). Uit
    /// een niet-kennis-cache leid je geen kennis af — anders explodeert de zoektocht
    /// bovendien op hun generieke Concept/Card-domein/range.</summary>
    private static bool IsChainableKnowledge(OntologyRelation rel) =>
        rel.Type is not (RelationType.SubclassOf   // TBox-meta, geen ABox-hop
            or RelationType.RelatesTo               // denorm-cache, nooit bron
            or RelationType.InteractsWith)          // pre-ontologische hint, geen kennis
        && !rel.MustReify                           // geen kale edge om te ketenen
        && rel.Domain.Count > 0;

    /// <summary>Bounded diepte-eerst-zoektocht over de kale kennis-relaties: alle
    /// simpele ketens (geen relatietype herhaald) die bij een Card starten en op een
    /// GOVERNED_BY→RuleSection eindigen, lengte ≥ 2 (een directe GOVERNED_BY is geen
    /// afleiding). Subclass-compatibel: de range van hop i moet binnen het domein van
    /// hop i+1 vallen (een Mechanic ⊑ Concept voldoet aan het GOVERNED_BY-domein).
    /// Ontdupliceerd op de edge-namen-keten (een range met meerdere typen kan
    /// dezelfde keten twee keer opleveren).</summary>
    public static IReadOnlyList<IReadOnlyList<OntologyRelation>> GovernedByChains(int maxDepth = 4)
    {
        var results = new List<IReadOnlyList<OntologyRelation>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(EntityType current, List<OntologyRelation> acc)
        {
            if (acc.Count >= maxDepth) return;
            foreach (var rel in OntologySchema.Relations.Values)
            {
                if (!IsChainableKnowledge(rel)) continue;
                if (!rel.Domain.Any(d => OntologySchema.IsA(current, d))) continue;
                if (acc.Any(a => a.Type == rel.Type)) continue;      // simpel pad, geen cyclus
                var next = new List<OntologyRelation>(acc) { rel };
                if (rel.Type == RelationType.GovernedBy && next.Count >= 2
                    && seen.Add(string.Join("/", next.Select(r => r.EdgeName))))
                    results.Add(next);
                foreach (var range in rel.Range)
                    Walk(range, next);
            }
        }
        Walk(EntityType.Card, []);
        // Stabiele volgorde op de edge-namen-keten.
        return results
            .OrderBy(c => string.Join("/", c.Select(r => r.EdgeName)), StringComparer.Ordinal)
            .ToList();
    }

    private static string RenderChainCypher(IReadOnlyList<OntologyRelation> chain, string id)
    {
        // (c:Card)-[:HAS_KEYWORD]->(:Keyword)-[:INVOKES]->(:Mechanic)-[:GOVERNED_BY]->(s:RuleSection)
        var sb = new StringBuilder("MATCH (c:Card)");
        for (var i = 0; i < chain.Count; i++)
        {
            var rel = chain[i];
            var last = i == chain.Count - 1;
            var targetLabel = rel.Range.Count > 0 ? rel.Range[0].ToString() : "Thing";
            sb.Append($"-[:{rel.EdgeName}]->(");
            sb.Append(last ? $"s:{targetLabel}" : $":{targetLabel}");
            sb.Append(')');
        }
        var govEdge = OntologySchema.Relations[RelationType.GovernedBy].EdgeName;
        return sb.AppendLine()
            .AppendLine($"MERGE (c)-[g:{govEdge}]->(s)")
            .Append(DerivedEdgeProvenance.SetClause("g", id))
            .ToString();
    }

    // ── symmetrische sluiting ────────────────────────────────────────────────

    /// <summary>Voor elke SYMMETRISCHE kale relatie (INTERACTS_WITH, CONTRADICTS —
    /// uit <see cref="RelationTraits.Symmetric"/>) de bounded terug-edge:
    /// <c>R(b,a) :- R(a,b) ∧ ¬R(b,a)</c>. De <c>NOT EXISTS</c>-guard maakt de regel
    /// idempotent én monotoon (geen dubbel werk, geen oneindige lus). Gereïficeerde
    /// relaties (geen kale edge) doen niet mee.</summary>
    public static IReadOnlyList<InferenceRule> SymmetricClosureRules()
    {
        var rules = new List<InferenceRule>();
        foreach (var rel in OntologySchema.Relations.Values
                     .Where(r => r.Traits.HasFlag(RelationTraits.Symmetric) && !r.MustReify)
                     .OrderBy(r => r.EdgeName, StringComparer.Ordinal))
        {
            var id = "symmetric:" + rel.EdgeName.ToLowerInvariant();
            var cypher = $$"""
                MATCH (a)-[:{{rel.EdgeName}}]->(b)
                WHERE a <> b AND NOT EXISTS { (b)-[:{{rel.EdgeName}}]->(a) }
                MERGE (b)-[d:{{rel.EdgeName}}]->(a)
                {{DerivedEdgeProvenance.SetClause("d", id)}}
                """;
            rules.Add(new(id, $"Symmetrische sluiting {rel.EdgeName}",
                InferenceFamily.SymmetricClosure, rel.EdgeName,
                $"Materialiseert de ontbrekende terug-edge voor de symmetrische relatie {rel.EdgeName}.",
                cypher, []));
        }
        return rules;
    }

    // ── subproperty-collapse ─────────────────────────────────────────────────

    /// <summary>Subproperty-collapse: voor elke gedeclareerde <c>(alias → canoniek)</c>
    /// RELATES_TO-kind-mapping één regel die de alias-edge naar zijn canonieke
    /// super-property vouwt (wapening tegen synoniem-proliferatie, faalmodus #2). De
    /// mapping is de ÉNE schema-bron (<see cref="OntologySchema.RelatesToKindSubProperties"/>);
    /// v0 is leeg ⇒ geen regels. Puur: neemt de map als parameter zodat de generatie
    /// zonder ontologie-mutatie getest kan worden.</summary>
    public static IReadOnlyList<InferenceRule> SubpropertyCollapseRules(
        IReadOnlyDictionary<string, string> subProperties)
    {
        ArgumentNullException.ThrowIfNull(subProperties);
        if (subProperties.Count == 0) return [];

        const string id = "subproperty-collapse";
        var relatesTo = OntologySchema.Relations[RelationType.RelatesTo].EdgeName;
        var rows = subProperties
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (Dictionary<string, object?>)new()
            {
                ["sub"] = kv.Key,
                ["super"] = kv.Value,
            })
            .ToList();
        var cypher = $$"""
            UNWIND $rows AS m
            MATCH (a)-[:{{relatesTo}} {kind: m.sub}]->(b)
            MERGE (a)-[d:{{relatesTo}} {kind: m.super}]->(b)
            {{DerivedEdgeProvenance.SetClause("d", id)}}
            """;
        return
        [
            new(id, "Subproperty-collapse (RELATES_TO-kinds)",
                InferenceFamily.SubpropertyCollapse, relatesTo,
                "Vouwt elke alias-kind-edge naar zijn canonieke super-property-kind.",
                cypher, rows),
        ];
    }
}
