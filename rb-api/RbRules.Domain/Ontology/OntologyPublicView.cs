using RbRules.Domain.Reasoning;

namespace RbRules.Domain.Ontology;

/// <summary>Eén klasse uit de hiërarchie, leesbaar.</summary>
/// <param name="Naam">Het klasse-type (= Neo4j-label).</param>
/// <param name="Ouders">Directe superklassen; leeg voor de wortel.</param>
/// <param name="Omschrijving">De omschrijving uit het schema.</param>
public sealed record OntologyViewClass(
    string Naam, IReadOnlyList<string> Ouders, string Omschrijving);

/// <summary>Eén relatie, leesbaar: van welke soorten naar welke soorten, met
/// kardinaliteit en of zij gereïficeerd moet worden.</summary>
/// <param name="Edge">De canonieke Neo4j-edge-naam (SCREAMING_SNAKE_CASE).</param>
/// <param name="Van">Domein-klassen (subclass-gesloten).</param>
/// <param name="Naar">Range-klassen (subclass-gesloten).</param>
/// <param name="Kardinaliteit">"min..max" met <c>*</c> voor onbegrensd.</param>
/// <param name="Gereificeerd">Verboden als kale edge — gaat via een Interaction-knoop.</param>
public sealed record OntologyViewRelation(
    string Edge,
    IReadOnlyList<string> Van,
    IReadOnlyList<string> Naar,
    string Kardinaliteit,
    bool Gereificeerd);

/// <summary>Eén inferentie-regel zonder haar Cypher: wélke edge zij afleidt en
/// waaruit. De Cypher zelf is uitvoeringsdetail en hoort niet publiek.</summary>
public sealed record OntologyViewInference(
    string Edge, string Naam, string Familie, string Omschrijving);

/// <summary>Het ontologie-schema zoals de uitlegpagina het toont.</summary>
public sealed record OntologyView(
    string Versie,
    IReadOnlyList<OntologyViewClass> Klassen,
    IReadOnlyList<OntologyViewRelation> Relaties,
    IReadOnlyList<string> DisjuncteParen,
    IReadOnlyList<OntologyViewInference> Inferenties);

/// <summary>Publieke, leesbare projectie van <see cref="OntologySchema"/> en de
/// daaruit gegenereerde <see cref="InferenceRuleRegistry"/>. Bestaat zodat de
/// uitlegpagina (/hoe-het-werkt) de ontologie LIVE toont in plaats van een
/// met de hand overgetypte kopie: het schema is de ÉNE bron, dus een uitleg die
/// ernaast leeft veroudert per definitie zodra iemand een relatie toevoegt.
///
/// Puur en IO-loos; het schema is compile-time constant, dus de projectie wordt
/// één keer opgebouwd (<see cref="Current"/>) en daarna hergebruikt — de
/// inferentie-generatie loopt over alle relatie-ketens en hoeft niet per
/// request opnieuw.</summary>
public static class OntologyPublicProjection
{
    /// <summary>De projectie van het huidige schema. Eén keer opgebouwd.</summary>
    public static readonly OntologyView Current = Build();

    private static OntologyView Build()
    {
        var klassen = OntologySchema.Classes.Values
            .OrderBy(c => c.Type.ToString(), StringComparer.Ordinal)
            .Select(c => new OntologyViewClass(
                c.Type.ToString(),
                [.. c.DirectParents.Select(p => p.ToString())],
                c.Description))
            .ToList();

        var relaties = OntologySchema.Relations.Values
            .OrderBy(r => r.EdgeName, StringComparer.Ordinal)
            .Select(r => new OntologyViewRelation(
                r.EdgeName,
                [.. r.Domain.Select(d => d.ToString())],
                [.. r.Range.Select(d => d.ToString())],
                $"{r.MinCardinality}..{r.MaxCardinality?.ToString() ?? "*"}",
                r.MustReify))
            .ToList();

        var disjunct = OntologySchema.DisjointPairs
            .Select(p => $"{p.A} ⟂ {p.B}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var inferenties = InferenceRuleRegistry.All
            .Select(r => new OntologyViewInference(
                r.DerivedEdge, r.Name, r.Family.ToString(), r.Description))
            .OrderBy(i => i.Edge, StringComparer.Ordinal)
            .ThenBy(i => i.Naam, StringComparer.Ordinal)
            .ToList();

        return new OntologyView(
            OntologyBaseline.Version.ToString(), klassen, relaties, disjunct, inferenties);
    }
}
