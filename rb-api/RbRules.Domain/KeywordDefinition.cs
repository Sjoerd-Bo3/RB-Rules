namespace RbRules.Domain;

/// <summary>Deterministische definitie-vinder voor een canoniek keyword/mechanic
/// (#250). De canonieke entiteitenlaag krijgt haar <see cref="CanonicalEntity.Definition"/>
/// bij voorkeur uit de OFFICIËLE regeltekst — geen LLM, geen gok: alleen een
/// regelsectie die het keyword daadwerkelijk DEFINIEERT telt mee.
///
/// De poort is bewust smal: een sectie definieert een term alleen wanneer haar
/// tekst met die term OPENT (de gebruikelijke glossarium-/keyword-vorm, "Deflect:
/// …" of "Deflect is …"), en het eerstvolgende teken geen letter is (zodat
/// "Deflection" niet als definitie van "Deflect" doorgaat). Vindt de poort niets,
/// dan blijft de definitie leeg — de hover degradeert al netjes en een verzonnen
/// definitie is erger dan geen.</summary>
public static class KeywordDefinition
{
    /// <summary>Maximale lengte van een bewaarde definitie: een hover-tekst, geen
    /// hele sectie. Langere teksten worden op een woordgrens afgekapt.</summary>
    public const int MaxLength = 400;

    /// <summary>De beste definitie voor <paramref name="label"/> uit
    /// <paramref name="sectionTexts"/>, of <c>null</c> als geen enkele sectie de term
    /// definieert. Bij meerdere kandidaten wint de KORTSTE (de meest definitie-
    /// achtige; een lange sectie die toevallig met de term opent is doorgaans
    /// procedure-tekst), bij gelijke lengte de eerste in bronvolgorde — zo is de
    /// uitkomst deterministisch en dus idempotent over runs heen.</summary>
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

    /// <summary>Opent deze sectietekst met de term als gedefinieerd begrip? De term
    /// moet aan het begin staan en als heel woord eindigen (het volgende teken is
    /// geen letter/cijfer) — "Deflect: …" telt, "Deflection …" niet.</summary>
    public static bool Defines(string? text, string? label)
    {
        var body = (text ?? "").TrimStart();
        var term = (label ?? "").Trim();
        if (body.Length == 0 || term.Length == 0 || body.Length < term.Length) return false;
        if (!body.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return false;
        if (body.Length == term.Length) return false;      // kaal label, geen definitie
        var next = body[term.Length];
        return !char.IsLetterOrDigit(next);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxLength) return text;
        var cut = text.LastIndexOf(' ', MaxLength - 1);
        return (cut > MaxLength / 2 ? text[..cut] : text[..MaxLength]).TrimEnd() + "…";
    }
}
