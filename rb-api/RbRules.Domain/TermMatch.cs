namespace RbRules.Domain;

/// <summary>Woordgrens-bewuste term-matching voor keyword-/kaartlabels in vrije tekst
/// (#249-review). Een kale <c>string.Contains</c> was verdedigbaar zolang alleen korte,
/// gecontroleerde kaartteksten doorzocht werden, maar sinds #249 is het HELE regelcorpus
/// bewijsbron én ankertoets voor de promotie-poort. Riftbound-keywords zijn deels gewone
/// Engelse woorden ("Tank", "Hidden", "Assault", "Equip", "Vision"), dus een substring-
/// match maakt van een regelzin als "a player may not look at hidden information" een
/// bewijszin voor mechanic:Hidden — en met een positief LLM-verdict promoveert dat
/// direct, met een officieel-ogende sectie als onderbouwing die het keyword niet eens
/// noemt.
///
/// De grens is bewust dezelfde als in <see cref="KeywordDefinition"/>: het teken vóór en
/// ná de term mag geen letter/cijfer zijn. Dat houdt precies de vormen heel die dit
/// domein nodig heeft — gebracket ("[Assault 2]"), leestekens eromheen ("Tank."), en
/// meerwoordstermen ("Reaction Window") — terwijl afleidingen en samenstellingen
/// ("Deflection", "tanking", "Equipment") niet meer meetellen.</summary>
public static class TermMatch
{
    /// <summary>Komt <paramref name="term"/> als heel woord voor in
    /// <paramref name="text"/> (hoofdletter-ongevoelig)? Leeg/whitespace-term of
    /// -tekst geeft false.</summary>
    public static bool ContainsWord(string? text, string? term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term)) return false;
        var needle = term.Trim();

        var from = 0;
        while (from <= text.Length - needle.Length)
        {
            var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            if (IsBoundary(text, at, needle.Length)) return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>Staat de treffer op <paramref name="at"/> op woordgrenzen? Het teken
    /// ervóór en het teken erná mogen geen letter/cijfer zijn (randen tellen als
    /// grens).</summary>
    public static bool IsBoundary(string text, int at, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        var end = at + length;
        var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
        return before && after;
    }
}
