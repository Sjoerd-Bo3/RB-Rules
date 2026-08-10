using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>§-verwijzings-bijlading voor /ask (#364). In productie eindigde
/// een antwoord op "Onzeker" met letterlijk "de volledige tekst van §811 …
/// is niet meegeleverd": de opgehaalde fragmenten verwezen naar secties die
/// de retrieval zelf niet ophaalde. Dit is de pure kant van de fix —
/// deterministische context-expansie vóór de modelcall, géén tweede
/// LLM-pass: <see cref="ExtractCodes"/> vindt de §-verwijzingen in
/// fragmentteksten en vraag, <see cref="SelectForBackfill"/> kiest wat er
/// écht bijgeladen wordt (bestaat in de index, nog niet in de context,
/// gecapt). De DB-kant (welke chunks bij die codes horen) blijft in
/// AskService.</summary>
public static partial class SectionReferenceParser
{
    /// <summary>Cap op het aantal bijgeladen secties per vraag (#364): genoeg
    /// voor het echte gebruik (het productie-voorbeeld noemde er drie), en
    /// een harde rem tegen een opsommings-fragment dat tientallen §'s noemt —
    /// elke bijgeladen sectie is extra prompt-omvang, en omvang drijft de
    /// latency (#281). Bewust één ronde zonder recursie: bijgeladen teksten
    /// worden niet opnieuw gescand.</summary>
    public const int MaxBackfillSections = 6;

    // De vormen waarin regelsecties genoemd worden: "§811", "§ 355.6",
    // "(§811.1)", "section 421" / "sections 421", en het Nederlandse
    // "sectie 421". Bewust géén kale nummers: "[1]" (kaartkosten),
    // ":rb_energy: 3" en "[Assault 2]" mogen nooit matchen. De code-vorm
    // spiegelt RuleSectionParser.SectionHeader (1-4 cijfers, punt-subcodes,
    // optionele lettersubcode); de negative lookahead voorkomt dat "§421ab"
    // als sectie doorgaat. Lettersubcodes zijn in de bron kleine letters,
    // daarom geen IgnoreCase maar expliciete [Ss]-alternatieven.
    [GeneratedRegex(@"(?:§\s*|\b[Ss]ections?\s+|\b[Ss]ecties?\s+)(\d{1,4}(?:\.\d+)*(?:\.?[a-z])?)(?!\w)")]
    private static partial Regex Reference();

    /// <summary>Alle §-verwijzingen in <paramref name="text"/>, genormaliseerd
    /// ("466.2c" → "466.2.c", zelfde vorm als de sectiecodes in de index) en
    /// gededupliceerd in volgorde van eerste vermelding — die volgorde bepaalt
    /// wie bij de cap voorrang krijgt.</summary>
    public static IReadOnlyList<string> ExtractCodes(string text)
    {
        var codes = new List<string>();
        foreach (Match m in Reference().Matches(text))
        {
            var code = RuleSectionParser.NormalizeCode(m.Groups[1].Value);
            if (!codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    /// <summary>Welke genoemde codes er bijgeladen worden: alleen codes die in
    /// de index bestaan (<paramref name="existingInIndex"/>; "§811.1" valt
    /// terug op de diepst bestaande ouder als de subcode zelf ontbreekt), die
    /// nog niet in de context zitten (<paramref name="presentInContext"/>) en
    /// niet dubbel — tot maximaal <see cref="MaxBackfillSections"/>.</summary>
    public static IReadOnlyList<string> SelectForBackfill(
        IEnumerable<string> mentioned,
        IReadOnlySet<string> presentInContext,
        IReadOnlySet<string> existingInIndex)
    {
        var selected = new List<string>();
        foreach (var code in mentioned)
        {
            if (selected.Count >= MaxBackfillSections) break;
            var resolved = Resolve(code, existingInIndex);
            if (resolved is null) continue;               // onbestaand: overslaan
            if (presentInContext.Contains(resolved)) continue; // al in de context
            if (selected.Contains(resolved)) continue;    // niet dubbel bijladen
            selected.Add(resolved);
        }
        return selected;
    }

    private static string? Resolve(string code, IReadOnlySet<string> existing)
    {
        if (existing.Contains(code)) return code;
        // Diepste ouder eerst: "466.2.c" probeert "466.2" vóór "466".
        foreach (var parent in RuleSectionParser.ParentCodes(code).Reverse())
            if (existing.Contains(parent)) return parent;
        return null;
    }
}
