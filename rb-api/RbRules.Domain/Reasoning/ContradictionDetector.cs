using RbRules.Domain.Ontology;

namespace RbRules.Domain.Reasoning;

/// <summary>Eén bounded contradictie-patroon (fase 3, #227, §5). VASTGELEGDE
/// BESLISSING: contradictie-detectie is Neo4j-native, via bounded
/// <c>WHERE NOT EXISTS</c>-patronen — nooit een edge, altijd een RETURN die de
/// service naar een <see cref="ReasoningConflict"/>-rij vertaalt. Elk patroon RETURNt
/// <c>subjectRef</c>, optioneel <c>counterRef</c> en <c>memo</c> (de vaste kolomnamen
/// die de service leest).</summary>
/// <param name="Id">Stabiel patroon-id (ook <c>ReasoningConflict.PatternId</c>).</param>
/// <param name="Kind">De tegenspraak-soort (<see cref="ReasoningConflictKind"/>).</param>
/// <param name="Name">Menselijke naam.</param>
/// <param name="Description">Wat het patroon vindt.</param>
/// <param name="Channel">Het routerings-kanaal (uit <see cref="ConflictRouter"/>).</param>
/// <param name="Cypher">De bounded detectie-query (read-only, met LIMIT).</param>
public sealed record ContradictionPattern(
    string Id,
    string Kind,
    string Name,
    string Description,
    ConflictChannel Channel,
    string Cypher);

/// <summary>Eén rauwe treffer van een contradictie-patroon (de service leest de
/// Neo4j-record naar deze vorm; de vertaling naar een <see cref="ReasoningConflict"/>
/// is puur en getest).</summary>
/// <param name="Pattern">Het patroon dat vuurde.</param>
/// <param name="SubjectRef">BrainRef van de betrokken knoop.</param>
/// <param name="CounterRef">BrainRef van de tegensprekende knoop, of null.</param>
/// <param name="Memo">Bewijs/memo (claim-tekst, ruling-teksten, labelpaar).</param>
public sealed record ContradictionHit(
    ContradictionPattern Pattern,
    string SubjectRef,
    string? CounterRef,
    string? Memo);

/// <summary>De bounded contradictie-patronen (fase 3, #227, §5), grotendeels
/// GEGENEREERD uit de ontologie: de disjointness-schendingen komen één-op-één uit
/// <see cref="OntologySchema"/>'s effectieve disjuncte paren (dezelfde bron als de
/// schema-poort), zodat een nieuwe disjointness-as automatisch een detector krijgt.
/// De claim↔officieel- en ruling-collisie-patronen zijn vast (ze hangen aan
/// concrete knoop-/property-vormen in de projectie). Puur/IO-loos; de service draait
/// de Cypher (best-effort — Neo4j zit niet in CI, live-executie is
/// integratie-follow-up) en verwerkt de treffers via <see cref="ToConflict"/>.</summary>
public static class ContradictionDetector
{
    private const int Limit = 500;

    /// <summary>Alle patronen: de twee vaste + één per effectief disjunct
    /// klassenpaar.</summary>
    public static IReadOnlyList<ContradictionPattern> All =>
        [ClaimContradictsOfficial, RulingCollision, .. DisjointnessPatterns()];

    /// <summary>Community-claim vs. officiële regel: een Claim ABOUT een RuleSection
    /// waarvan de officiële toets 'contradicts' zegt, ZONDER dat de claim zelf
    /// officiële dekking heeft (bounded <c>NOT EXISTS</c>-guard op een trust-tier-1-bron).
    /// Concreet: claim "Deflect prevents all damage" vs §7.4 "only the first instance"
    /// ⇒ misvattingen-kanaal met beide bron-ids.</summary>
    public static readonly ContradictionPattern ClaimContradictsOfficial = new(
        "claim-contradicts-official",
        ReasoningConflictKind.ClaimContradictsOfficial,
        "Claim spreekt officiële regel tegen",
        "Community-claim tegen een RuleSection zonder eigen officiële dekking.",
        ConflictRouter.Route(ReasoningConflictKind.ClaimContradictsOfficial),
        $$"""
        MATCH (cl:Claim)-[:ABOUT]->(s:RuleSection)
        WHERE cl.officialStatus = 'contradicts'
          AND NOT EXISTS { (cl)-[:SUPPORTED_BY]->(:Source {trustTier: 1}) }
        RETURN cl.ref AS subjectRef, s.ref AS counterRef, cl.statement AS memo
        LIMIT {{Limit}}
        """);

    /// <summary>Botsende rulings: twee geverifieerde Rulings over hetzelfde anker met
    /// verschillende tekst — menselijke escalatie (twee normatieve bronnen, geen
    /// deterministische winnaar). Bounded: één gedeelde ABOUT-hop; <c>elementId</c>-
    /// ordening voorkomt spiegel-duplicaten.</summary>
    public static readonly ContradictionPattern RulingCollision = new(
        "ruling-collision",
        ReasoningConflictKind.RulingCollision,
        "Botsende rulings",
        "Twee geverifieerde rulings over hetzelfde anker met verschillende tekst.",
        ConflictRouter.Route(ReasoningConflictKind.RulingCollision),
        $$"""
        MATCH (r1:Ruling)-[:ABOUT]->(t)<-[:ABOUT]-(r2:Ruling)
        WHERE elementId(r1) < elementId(r2) AND r1.text <> r2.text
        RETURN r1.ref AS subjectRef, r2.ref AS counterRef,
               t.ref + ' :: ' + r1.text + ' <> ' + r2.text AS memo
        LIMIT {{Limit}}
        """);

    /// <summary>Eén detectie-patroon per EFFECTIEF disjunct klassenpaar uit de
    /// ontologie (<see cref="OntologySchema.AreDisjoint"/>, overerving meegerekend):
    /// een knoop die beide labels draagt is een disjointness-schending
    /// (bv. <c>:Unit:Spell</c> — kaart-sync-schade à la #150). De multi-label-match
    /// is de bounded vorm; ongeordend gededupliceerd (A/B == B/A).</summary>
    public static IReadOnlyList<ContradictionPattern> DisjointnessPatterns()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var patterns = new List<ContradictionPattern>();
        var types = Enum.GetValues<EntityType>()
            .Where(OntologySchema.Classes.ContainsKey)
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var a in types)
            foreach (var b in types)
            {
                if (a == b || !OntologySchema.AreDisjoint(a, b)) continue;
                var an = a.ToString();
                var bn = b.ToString();
                // Ongeordend: canoniek paar op alfabet zodat A/B en B/A samenvallen.
                var (first, second) = string.CompareOrdinal(an, bn) < 0 ? (an, bn) : (bn, an);
                if (!seen.Add($"{first}|{second}")) continue;

                patterns.Add(new(
                    $"disjointness:{first.ToLowerInvariant()}-{second.ToLowerInvariant()}",
                    ReasoningConflictKind.DisjointnessViolation,
                    $"Disjointness-schending {first}/{second}",
                    $"Een knoop draagt tegelijk de disjuncte labels {first} en {second}.",
                    ConflictRouter.Route(ReasoningConflictKind.DisjointnessViolation),
                    $$"""
                    MATCH (n:{{first}}:{{second}})
                    RETURN coalesce(n.ref, elementId(n)) AS subjectRef,
                           null AS counterRef,
                           '{{first}} + {{second}}' AS memo
                    LIMIT {{Limit}}
                    """));
            }
        return patterns;
    }

    /// <summary>Puur: vertaalt een rauwe treffer naar een <see cref="ReasoningConflict"/>-rij
    /// (fase 3, #227). Het kanaal komt van het patroon (dat het uit
    /// <see cref="ConflictRouter"/> haalt), de dedupe-sleutel uit
    /// <see cref="ReasoningConflictDedupe"/>, de provenance van de meegegeven
    /// <paramref name="runId"/>. Dit is de "Conflict-rij → misvattingen/reviewqueue-
    /// koppeling" die de service ongewijzigd persisteert.</summary>
    public static ReasoningConflict ToConflict(ContradictionHit hit, string runId)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return new ReasoningConflict
        {
            PatternId = hit.Pattern.Id,
            Kind = hit.Pattern.Kind,
            Channel = ConflictRouter.ChannelString(hit.Pattern.Channel),
            SubjectRef = hit.SubjectRef,
            CounterRef = hit.CounterRef,
            Memo = hit.Memo,
            DedupeKey = ReasoningConflictDedupe.Key(hit.Pattern.Id, hit.SubjectRef, hit.CounterRef),
            RunId = runId,
            Status = ReasoningConflictStatus.Open,
        };
    }
}
