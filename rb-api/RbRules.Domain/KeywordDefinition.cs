namespace RbRules.Domain;

/// <summary>Deterministische definitie-vinder voor een canoniek keyword/mechanic
/// (#250). De canonieke entiteitenlaag krijgt haar <see cref="CanonicalEntity.Definition"/>
/// uit de OFFICIËLE regeltekst — geen LLM, geen gok: alleen een regelsectie die het
/// keyword daadwerkelijk DEFINIEERT telt mee. (De bron-kant van die belofte staat in
/// <c>EntityResolutionService</c>: alleen rule_chunks van trust-tier-1-bronnen worden
/// aangeboden, anders wint een community-parafrase van de officiële tekst.)
///
/// De poort is bewust smal en werd in de #250-review verscherpt. "Opent met de term"
/// bleek veel te ruim: in een genummerd regelboek strippet de sectie-parser de kop, dus
/// chunks beginnen routineus met een kale procedure-zin die toevallig met de term begint
/// ("Ready units can be exhausted to pay costs.", "Tank counters are removed at the end
/// of the showdown."). Omdat <see cref="Find"/> de KORTSTE kandidaat kiest, versloeg zo'n
/// korte procedure-zin systematisch de echte glossariumsectie — en die tekst landt zowel
/// in de hover als, gewichtiger, als eerste BEWIJSREGEL in de predicaat-mining.
///
/// Een sectie definieert een term daarom alleen wanneer haar tekst:
/// <list type="number">
/// <item>met de term OPENT, als heel woord (het volgende teken is geen letter/cijfer —
/// "Deflection …" gaat niet door voor "Deflect");</item>
/// <item>direct daarna — hooguit met een magnitude-plaatshouder ertussen ("Tank N",
/// "Assault 2") — een DEFINITIE-MARKER draagt: een dubbele punt, een gedachtestreep, of
/// het koppelwerkwoord "is"/"are".</item>
/// </list>
/// Daarmee valt ook het meerwoords-prefix-geval weg: "Reaction Window: …" definieert
/// "Reaction Window", niet "Reaction" — bij de kortere term volgt op de woordgrens
/// immers "Window", geen marker. Vindt de poort niets, dan blijft de definitie leeg —
/// de hover degradeert al netjes en een verzonnen definitie is erger dan geen.</summary>
public static class KeywordDefinition
{
    /// <summary>Maximale lengte van een bewaarde definitie: een hover-tekst, geen
    /// hele sectie. Langere teksten worden op een woordgrens afgekapt.</summary>
    public const int MaxLength = 400;

    /// <summary>De beste definitie voor <paramref name="label"/> uit
    /// <paramref name="sectionTexts"/>, of <c>null</c> als geen enkele sectie de term
    /// definieert. Bij meerdere kandidaten wint de KORTSTE (met de marker-eis zijn alle
    /// kandidaten definitie-vormig, en dan is de beknoptste de bruikbaarste hover-tekst),
    /// bij gelijke lengte de eerste in bronvolgorde — zo is de uitkomst deterministisch
    /// en dus idempotent over runs heen.</summary>
    public static string? Find(string? label, IEnumerable<string?> sectionTexts)
    {
        ArgumentNullException.ThrowIfNull(sectionTexts);
        var term = (label ?? "").Trim();
        if (term.Length == 0) return null;

        string? best = null;
        foreach (var text in sectionTexts)
        {
            if (!Defines(text, term)) continue;
            var candidate = text!.Trim();
            if (best is null || candidate.Length < best.Length) best = candidate;
        }
        return best is null ? null : Truncate(best);
    }

    /// <summary>Definieert deze sectietekst de term? De term moet aan het begin staan en
    /// als heel woord eindigen, en er moet — eventueel ná een magnitude-plaatshouder —
    /// een definitie-marker volgen (':', '—'/'–'/'-', of "is"/"are"). "Deflect: …" telt,
    /// "Tank N — …" telt, "Deflection …" niet, "Ready units can be …" niet, en
    /// "Reaction Window: …" telt niet als definitie van "Reaction".</summary>
    public static bool Defines(string? text, string? label)
    {
        var body = (text ?? "").TrimStart();
        var term = (label ?? "").Trim();
        if (body.Length == 0 || term.Length == 0 || body.Length < term.Length) return false;
        if (!body.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return false;
        if (body.Length == term.Length) return false;      // kaal label, geen definitie
        if (!TermMatch.IsBoundary(body, 0, term.Length)) return false;

        var rest = body[term.Length..].TrimStart();
        rest = SkipMagnitude(rest);
        return HasDefinitionMarker(rest);
    }

    /// <summary>Slaat een magnitude-plaatshouder over die tussen de term en de marker
    /// kan staan: een getal ("Assault 2: …") of de conventionele N/X ("Tank N — …").
    /// Alleen die twee vormen — elk ander woord betekent dat de sectie een LÁNGERE term
    /// definieert (of gewoon proza is), niet deze.</summary>
    private static string SkipMagnitude(string rest)
    {
        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        if (end == 0 && rest.Length > 0 && (rest[0] is 'N' or 'X' or 'n' or 'x')
            && (rest.Length == 1 || !char.IsLetterOrDigit(rest[1])))
            end = 1;
        return end == 0 ? rest : rest[end..].TrimStart();
    }

    /// <summary>Draagt de rest ná de term een definitie-marker? De glossarium-vormen die
    /// dit domein daadwerkelijk gebruikt: "Deflect: …", "Tank — …", "Deflect is …".</summary>
    private static bool HasDefinitionMarker(string rest)
    {
        if (rest.Length == 0) return false;
        if (rest[0] is ':' or '—' or '–' or '-') return true;
        foreach (var copula in Copulas)
            if (rest.StartsWith(copula, StringComparison.OrdinalIgnoreCase)
                && TermMatch.IsBoundary(rest, 0, copula.Length))
                return true;
        return false;
    }

    private static readonly string[] Copulas = ["is", "are"];

    private static string Truncate(string text)
    {
        if (text.Length <= MaxLength) return text;
        var cut = text.LastIndexOf(' ', MaxLength - 1);
        return (cut > MaxLength / 2 ? text[..cut] : text[..MaxLength]).TrimEnd() + "…";
    }
}
