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
    /// Lege lijst = consistent.</summary>
    public static IReadOnlyList<OntologyAuditFinding> Run()
    {
        var findings = new List<OntologyAuditFinding>();
        CheckAcyclicHierarchy(findings);
        CheckDisjointnessSatisfiable(findings);
        CheckRelationDomainsRegistered(findings);
        return findings;
    }

    /// <summary>De hiërarchie mag geen cyclus bevatten: geen type is zijn eigen
    /// (transitieve) voorouder.</summary>
    private static void CheckAcyclicHierarchy(List<OntologyAuditFinding> findings)
    {
        foreach (var type in OntologySchema.Classes.Keys)
            if (OntologySchema.Ancestors(type).Contains(type))
                findings.Add(new("subclass-cycle",
                    $"{type} is (transitief) zijn eigen voorouder — de hiërarchie is niet acyclisch."));
    }

    /// <summary>Elke disjointness-as moet vervulbaar zijn: geen enkel type mag een
    /// (voorouder-)subklasse zijn van BEIDE kanten van een gedeclareerd disjunct
    /// paar (dat zou de klasse onvervulbaar/leeg maken — het Card⊑Object-scenario
    /// dat de ontologie juist vermijdt).</summary>
    private static void CheckDisjointnessSatisfiable(List<OntologyAuditFinding> findings)
    {
        foreach (var (a, b) in OntologySchema.DisjointPairs)
            foreach (var type in OntologySchema.Classes.Keys)
                if (OntologySchema.IsA(type, a) && OntologySchema.IsA(type, b))
                    findings.Add(new("unsatisfiable-class",
                        $"{type} is subklasse van zowel {a} als {b}, maar die zijn disjunct — onvervulbare klasse."));
    }

    /// <summary>Elk domain-/range-type van elke relatie moet een geregistreerde
    /// klasse zijn (geen dangling type-verwijzing in het schema).</summary>
    private static void CheckRelationDomainsRegistered(List<OntologyAuditFinding> findings)
    {
        foreach (var rel in OntologySchema.Relations.Values)
        {
            foreach (var d in rel.Domain)
                if (!OntologySchema.Classes.ContainsKey(d))
                    findings.Add(new("dangling-domain",
                        $"{rel.EdgeName} verwijst naar een niet-geregistreerd domein-type {d}."));
            foreach (var r in rel.Range)
                if (!OntologySchema.Classes.ContainsKey(r))
                    findings.Add(new("dangling-range",
                        $"{rel.EdgeName} verwijst naar een niet-geregistreerd range-type {r}."));
        }
    }
}
