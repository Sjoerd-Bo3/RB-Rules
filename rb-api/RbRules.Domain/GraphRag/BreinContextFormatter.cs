using System.Text;

namespace RbRules.Domain.GraphRag;

/// <summary>Zet een <see cref="GraphRagOutcome"/> om in een prompt-BLOK dat de
/// bestaande /ask-context VERRIJKT (fase ask-retrieval, #228, §4). PUUR: geen IO —
/// de orchestrator levert de bundel, paden, gating en trace; deze formatter maakt er
/// één machine-leesbaar, trust-gelabeld blok van dat ná de bestaande
/// kennispiramide-blokken in de prompt schuift.
///
/// Het blok is ADDITIEF en experimenteel: het vervangt de bestaande citatie-/
/// kaart-blokken niet, maar geeft het model de brein-subgraaf, de pad-onderbouwing
/// (de citaties vólgen uit de structuur, §4) en de gating-beslissing expliciet mee.
/// De trust-labels (<c>[OFFICIEEL]</c> / <c>[COMMUNITY trust=… corrob=…]</c>) komen
/// uit <see cref="TrustLabels"/> zodat het model nooit een impliciete rangorde ziet.
/// Geen enkel dragend feit ⇒ een leeg blok (de bestaande flow draait dan ongewijzigd
/// door).</summary>
public static class BreinContextFormatter
{
    /// <summary>Bouw het verrijkingsblok. Leeg (<c>""</c>) als er niets te melden is —
    /// dan blijft de prompt byte-voor-byte de bestaande.</summary>
    public static string Format(GraphRagOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var hasBundle = outcome.Bundle.Items.Count > 0;
        var hasPaths = outcome.PathCitations.Count > 0;
        var hasNoPath = outcome.NoPath is not null;
        if (!hasBundle && !hasPaths && !hasNoPath) return "";

        var sb = new StringBuilder();
        sb.Append("\n\nBREIN-CONTEXT (GraphRAG uit de kennisgraaf — trust-gelabeld, ")
          .Append("aanvullend op de fragmenten hierboven):");

        // De gating-beslissing expliciet (#229/#236 — een beslissing levert nooit
        // onzichtbare state): welke laag draagt het antwoord primair.
        sb.Append("\n[gating: ").Append(outcome.Gate.Primary).Append("] ").Append(outcome.Gate.Memo);

        // Subgraaf-chunks in trust-volgorde, met hun stabiele citation-nummer en label.
        foreach (var c in outcome.Bundle.Items)
        {
            sb.Append("\n[cit:").Append(c.N).Append("] ").Append(c.TrustLabel).Append(' ')
              .Append(Collapse(c.Item.Text));
            if (c.Item.WidgetMarker is not null)
                sb.Append(' ').Append(c.Item.WidgetMarker);
        }

        // Pad-onderbouwing (§4 "het pad ÍS de uitleg"): de leesbare ketens plus de
        // pad-knoop-citaties met hun widget-markers.
        if (outcome.Retrieval.Paths.Count > 0)
            foreach (var path in outcome.Retrieval.Paths)
                sb.Append("\npad-onderbouwing: ").Append(PathCitations.Explain(path));

        foreach (var p in outcome.PathCitations)
        {
            sb.Append("\n[cit:").Append(p.N).Append("] [").Append(TrustLabels.TierTag(p.Tier))
              .Append("] ").Append(Collapse(p.Label));
            if (p.WidgetMarker is not null)
                sb.Append(' ').Append(p.WidgetMarker);
        }

        // NoPath (§4): eerlijk "geen bekende interactie" i.p.v. hallucineren.
        if (outcome.NoPath is not null)
            sb.Append("\nGEEN PAD: ").Append(outcome.NoPath.Message);

        // Terugval-transparantie (#232/#236): waarom er (evt.) gedegradeerd is.
        if (outcome.FallbackReason is not null)
            sb.Append("\n(retrieval-terugval: ").Append(outcome.FallbackReason).Append(')');

        return sb.ToString();
    }

    /// <summary>Vouw witruimte samen zodat een chunk met interne newlines het blok
    /// niet uit elkaar trekt (elke context-regel blijft één regel).</summary>
    private static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
