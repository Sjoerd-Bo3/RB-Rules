using RbRules.Domain.Ontology;

namespace RbRules.Domain.Reasoning;

/// <summary>Eén bevinding van de ontologie-consistentie-audit.</summary>
public sealed record OntologyAuditFinding(string Code, string Message);

/// <summary>Het OWL2-RL-nachtaudit-orakel (fase 3, #227, §5) — bewust een SKELETON
/// per VASTGELEGDE BESLISSING: "OWL alleen als optionele nachtaudit". Géén
/// OWL/Turtle-runtime in de hot-path (onze edges zijn gekwalificeerd; OWL zou
/// reïficatie/blank-nodes afdwingen → structuurverlies, en .NET-OWL-tooling is dun).
/// In plaats daarvan een pure, deterministische zelf-toets tegen de AFGEDWONGEN bron
/// (<see cref="OntologySchema"/>): is de klassenhiërarchie acyclisch, zijn de
/// disjointness-assen vervulbaar, en zijn alle relatie-domain/range-typen
/// geregistreerd? Vindt de audit iets, dan is de schema-bron zelf inconsistent —
/// dat is een build-/beheer-signaal, geen graaf-runtime-fout. Uitbreidbaar tot een
/// echte OWL2-RL-consistency-check zodra dat loont; de rol (nachtaudit, niet
/// hot-path) staat vast.</summary>
public static class OntologyConsistencyAudit
{
    /// <summary>Draait de volledige zelf-toets tegen <see cref="OntologySchema"/>.
    /// Lege lijst = consistent. De drie deel-checks zijn PUUR (nemen hun invoer als
    /// parameter) zodat elke detectie-tak los toetsbaar is met een verzonnen
    /// inconsistent schema — de statische <see cref="OntologySchema"/> is per
    /// constructie consistent, dus alleen daartegen zou geen tak ooit vuren
    /// (#227-review, finding #3).</summary>
    public static IReadOnlyList<OntologyAuditFinding> Run()
    {
        var findings = new List<OntologyAuditFinding>();
        findings.AddRange(CheckAcyclicHierarchy(OntologySchema.Classes.Keys, OntologySchema.Ancestors));
        findings.AddRange(CheckDisjointnessSatisfiable(
            OntologySchema.Classes.Keys, OntologySchema.DisjointPairs, OntologySchema.IsA));
        findings.AddRange(CheckRelationDomainsRegistered(
            OntologySchema.Relations.Values, OntologySchema.Classes.ContainsKey));
        return findings;
    }

    /// <summary>De hiërarchie mag geen cyclus bevatten: geen type is zijn eigen
    /// (transitieve) voorouder.</summary>
    public static IReadOnlyList<OntologyAuditFinding> CheckAcyclicHierarchy(
        IEnumerable<EntityType> types,
        Func<EntityType, IReadOnlyCollection<EntityType>> ancestors)
    {
        var findings = new List<OntologyAuditFinding>();
        foreach (var type in types)
            if (ancestors(type).Contains(type))
                findings.Add(new("subclass-cycle",
                    $"{type} is (transitief) zijn eigen voorouder — de hiërarchie is niet acyclisch."));
        return findings;
    }

    /// <summary>Elke disjointness-as moet vervulbaar zijn: geen enkel type mag een
    /// (voorouder-)subklasse zijn van BEIDE kanten van een gedeclareerd disjunct
    /// paar (dat zou de klasse onvervulbaar/leeg maken — het Card⊑Object-scenario
    /// dat de ontologie juist vermijdt).</summary>
    public static IReadOnlyList<OntologyAuditFinding> CheckDisjointnessSatisfiable(
        IEnumerable<EntityType> types,
        IEnumerable<(EntityType A, EntityType B)> disjointPairs,
        Func<EntityType, EntityType, bool> isA)
    {
        var findings = new List<OntologyAuditFinding>();
        var typeList = types as IReadOnlyList<EntityType> ?? types.ToList();
        foreach (var (a, b) in disjointPairs)
            foreach (var type in typeList)
                if (isA(type, a) && isA(type, b))
                    findings.Add(new("unsatisfiable-class",
                        $"{type} is subklasse van zowel {a} als {b}, maar die zijn disjunct — onvervulbare klasse."));
        return findings;
    }

    /// <summary>Elk domain-/range-type van elke relatie moet een geregistreerde
    /// klasse zijn (geen dangling type-verwijzing in het schema).</summary>
    public static IReadOnlyList<OntologyAuditFinding> CheckRelationDomainsRegistered(
        IEnumerable<OntologyRelation> relations,
        Func<EntityType, bool> isRegistered)
    {
        var findings = new List<OntologyAuditFinding>();
        foreach (var rel in relations)
        {
            foreach (var d in rel.Domain)
                if (!isRegistered(d))
                    findings.Add(new("dangling-domain",
                        $"{rel.EdgeName} verwijst naar een niet-geregistreerd domein-type {d}."));
            foreach (var r in rel.Range)
                if (!isRegistered(r))
                    findings.Add(new("dangling-range",
                        $"{rel.EdgeName} verwijst naar een niet-geregistreerd range-type {r}."));
        }
        return findings;
    }
}
