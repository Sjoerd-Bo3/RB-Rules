using System.Globalization;

namespace RbRules.Domain.GraphRag;

/// <summary>Pad-scoring (§4): het STEVIGST onderbouwde pad, niet het letterlijk
/// kortste. Elke stap kost <c>1/(trust·confidence)</c>; het pad-gewicht is de som —
/// een pad over zwakke/onzekere edges is "langer". k-shortest kiest de laagste
/// gewichten. Puur: de GDS-Yen/Dijkstra-uitvoering leeft in de retriever, maar de
/// weging en de k-best-keuze (voor deterministische tests en als fallback) hier.</summary>
public static class PathScoring
{
    /// <summary>Ondergrens op trust·confidence per stap zodat een 0-edge het pad niet
    /// oneindig maakt (en de som eindig blijft).</summary>
    public const double Epsilon = 1e-3;

    public static double StepCost(double trustWeight, double confidence)
    {
        var denom = Math.Max(Epsilon, Math.Clamp(trustWeight, 0, 1) * Math.Clamp(confidence, 0, 1));
        return 1.0 / denom;
    }

    public static double Weight(GraphPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sum = 0.0;
        foreach (var step in path.Steps)
            sum += StepCost(step.TrustWeight, step.Confidence);
        return sum;
    }

    /// <summary>De k paden met het laagste gewicht (stevigst onderbouwd), stabiel
    /// getie-breakt op de eind-ref zodat de volgorde niet van invoervolgorde afhangt.</summary>
    public static IReadOnlyList<GraphPath> SelectKBest(IEnumerable<GraphPath> paths, int k)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return [.. paths
            .Select(p => (Path: p, W: Weight(p)))
            .OrderBy(x => x.W)
            .ThenBy(x => x.Path.End.Ref.Format(), StringComparer.Ordinal)
            .Take(Math.Max(0, k))
            .Select(x => x.Path)];
    }
}

/// <summary>Signaal wanneer er GEEN pad tussen de ankers is (§4: NoPath → geen
/// hallucinatie, wél een eerlijke "geen bekende interactie" + voeding voor de
/// KnowledgeGapsService). Inzicht #236: een leeg resultaat is óók een zichtbare
/// uitkomst, geen stil niets.</summary>
public sealed record NoPathSignal(IReadOnlyList<BrainRef> Anchors, string Message)
{
    public static NoPathSignal For(IReadOnlyList<BrainRef> anchors) =>
        new(anchors,
            anchors.Count >= 2
                ? $"Geen bekend pad tussen {anchors[0].Format()} en {anchors[^1].Format()} — " +
                  "geen bekende interactie i.p.v. een gok."
                : "Onvoldoende ankers voor een pad-onderbouwing.");
}

/// <summary>Zet paden om in geordende citaties met widget-markers (§4:
/// "citaties volgen uit de structuur, niet uit LLM-tekst"). Elke knoop/edge krijgt
/// een stabiel citationId; de referee verwijst met <c>[cit:N]</c>, de mapping is
/// post-hoc — het model kan geen bron verzinnen, alleen verwijzen. Een pad levert
/// een interactie-widget, een kaart een kaart-widget, een §-sectie een
/// permalink-widget.</summary>
public static class PathCitations
{
    /// <summary>Widget-marker per knoopsoort, conform de bestaande
    /// <c>[[rule:…]]</c>/<c>[[card:…]]</c>-conventie in AskService.</summary>
    public static string? WidgetMarker(GraphNode node) => node.Ref.Kind switch
    {
        BrainRefKind.Card => $"[[card:{node.Label}]]",
        BrainRefKind.Section => $"[[rule:{SectionCode(node.Ref)}]]",
        BrainRefKind.Interaction => $"[[interaction:{node.Ref.Key}]]",
        _ => null,
    };

    /// <summary>De §-code uit een sectie-ref ("core-rules-pdf/101.2" → "101.2").</summary>
    private static string SectionCode(BrainRef sectionRef)
    {
        var slash = sectionRef.Key.IndexOf('/');
        return slash >= 0 && slash < sectionRef.Key.Length - 1 ? sectionRef.Key[(slash + 1)..] : sectionRef.Key;
    }

    /// <summary>Menselijk leesbare pad-onderbouwing (§4-voorbeeld:
    /// <c>Unit —HAS_STATUS→ Exhausted —REQUIRES→ Showdown —GOVERNED_BY→ §7.3</c>).
    /// Dit is de uitleg-structuur die achter <c>[cit:N]</c> hangt.</summary>
    public static string Explain(GraphPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sb = new System.Text.StringBuilder(path.Start.Label);
        foreach (var step in path.Steps)
            sb.Append(" —").Append(step.Edge.EdgeType).Append("→ ").Append(step.Target.Label);
        return sb.ToString();
    }

    /// <summary>Bouw citaties uit paden. <paramref name="startId"/> is het eerste
    /// citation-nummer (de bundel kan al chunk-citaties hebben uitgedeeld). Elke
    /// unieke knoop krijgt één citation-id; herhaalde knopen over paden delen hun
    /// id (stabiele mapping). Retourneert de citaties in pad-volgorde.</summary>
    public static IReadOnlyList<PathCitation> Build(IReadOnlyList<GraphPath> paths, int startId = 1)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var byRef = new Dictionary<string, PathCitation>(StringComparer.Ordinal);
        var ordered = new List<PathCitation>();
        var next = startId;
        foreach (var path in paths)
        {
            foreach (var node in path.Nodes)
            {
                var key = node.Ref.Format();
                if (byRef.ContainsKey(key)) continue;
                var cit = new PathCitation(
                    next++, node.Ref, node.Tier, node.Label, WidgetMarker(node),
                    node.EffectiveTrust.Weight);
                byRef[key] = cit;
                ordered.Add(cit);
            }
        }
        return ordered;
    }
}

/// <summary>Eén citatie afgeleid uit een pad-knoop (§4): stabiel nummer, de knoop-
/// ref, zijn tier, de widget-marker en het WERKELIJKE trust-gewicht van de knoop
/// (<see cref="GraphNode.EffectiveTrust"/>). De referee schrijft <c>[cit:N]</c>;
/// AskService mapt N → deze rij. Het trust-gewicht laat de pad-citatie exact zoals een
/// bundle-chunk door de <see cref="TrustGate"/> (#229) — geen rauwe-authority-omweg.</summary>
public sealed record PathCitation(
    int N, BrainRef Ref, KnowledgeTier Tier, string Label, string? WidgetMarker, double TrustWeight)
{
    public string CitationTag => string.Create(CultureInfo.InvariantCulture, $"[cit:{N}]");
}
