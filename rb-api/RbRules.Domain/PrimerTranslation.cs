using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>De Nederlandse weergavelaag over de (canonieke) Engelse primer
/// (#266). Sinds #187/#197 staat afgeleide kennis in het Engels — dicht bij de
/// officiële bewoording — maar de site is Nederlands (werkafspraak 1). De
/// vertaling gebeurt bij GENERATIE, niet bij weergave: zo is de Nederlandse
/// tekst onderdeel van de draft die de beheerder goedkeurt en komt er nooit
/// ongereviewde tekst bij de bezoeker.
///
/// Prompt én waarborg staan hier samen, en bewust in Domain: het glossarium
/// dat de prompt uitschrijft is exact de lijst waarop <see cref="Leaks"/>
/// achteraf controleert. Eén lijst, dus prompt en controle kunnen niet uit
/// elkaar lopen, en de controle is puur — zonder rb-ai te testen.</summary>
public static class PrimerTranslation
{
    /// <summary>Riftbound-speltermen die onvertaald door de vertaling moeten
    /// komen (werkafspraak 1). Basisvorm in de officiële schrijfwijze; de
    /// controle matcht op woordbegin, dus "Rune" dekt ook "Runes"/"Rune Deck"
    /// en "Battlefield" ook "Battlefields".
    ///
    /// Hoofdlettergevoelig aan de Engelse kant — anders zou het gewone
    /// hulpwerkwoord "might" ("you might draw") de spelterm "Might" opeisen en
    /// zou elke vertaling zonder dat woord ten onrechte als lek gelden.
    /// Hoofdletter-ONgevoelig aan de Nederlandse kant: "je unit wordt ready"
    /// is een prima vertaling en telt gewoon mee.</summary>
    public static IReadOnlyList<string> Glossary =>
    [
        "Rune", "Battlefield", "showdown", "Might", "Bonus Damage", "Equip",
        "Assault", "Reaction", "Recycle", "Channel", "Legend", "Champion",
        "Unit", "Spell", "Gear", "Deathknell", "Accelerate", "Tank", "Deflect",
        "Hidden", "Shield", "Legion", "Temporary", "Exhaust", "Ready", "Trash",
        "Banish", "Domain", "Conquer",
    ];

    /// <summary>Systeem-prompt van de vertaalstap. Het glossarium wordt uit
    /// <see cref="Glossary"/> ingevuld, zodat een nieuwe spelterm in één keer
    /// én in de prompt én in de controle landt.</summary>
    public static string SystemPrompt =>
        $"""
        You translate a Riftbound TCG game-primer from English into Dutch for a
        Dutch-language rules companion. Requirements:
        - Natural, precise Dutch (je-vorm), same register as the English text
        - NEVER translate Riftbound game terms — keep them exactly as they are,
          in English, everywhere they occur: {string.Join(", ", Glossary)}, and
          any other term that is clearly Riftbound vocabulary or a card name
        - Keep every (§123.4) section reference exactly where it stands; never
          add, drop, merge or renumber one
        - Keep the paragraph structure and length; no markdown headers, no
          introduction, no closing remarks, no notes about the translation
        - Output the Dutch text only
        """;

    /// <summary>De waarborg (#266): welke speltermen en §-verwijzingen wél in
    /// de Engelse brontekst staan maar NIET meer in de vertaling — dat zijn
    /// precies de dingen die een vertaalstap stilletjes kapotmaakt. Leeg =
    /// bruikbare vertaling; niet leeg ⇒ de aanroeper gooit de vertaling weg en
    /// degradeert naar het Engels (liever de canonieke tekst dan een
    /// vernederlandste spelterm).</summary>
    public static IReadOnlyList<string> Leaks(string english, string dutch)
    {
        if (string.IsNullOrWhiteSpace(dutch)) return [.. Glossary];
        var leaked = new List<string>();
        foreach (var term in Glossary)
        {
            // Engelse kant hoofdlettergevoelig (spelterm), Nederlandse kant
            // niet — zie de opmerking bij Glossary.
            if (!Occurs(english, term, ignoreCase: false)) continue;
            if (!Occurs(dutch, term, ignoreCase: true)) leaked.Add(term);
        }
        leaked.AddRange(MissingSections(english, dutch));
        return leaked;
    }

    /// <summary>§-verwijzingen uit de brontekst die de vertaling niet meer
    /// heeft. De primer belooft per alinea een §-onderbouwing; een vertaling
    /// die er één laat vallen breekt die belofte.</summary>
    public static IReadOnlyList<string> MissingSections(string english, string dutch)
    {
        var inDutch = Sections(dutch).ToHashSet(StringComparer.Ordinal);
        return [.. Sections(english).Where(s => !inDutch.Contains(s)).Distinct(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Sections(string text) =>
        SectionRefPattern.Matches(text).Select(m => m.Groups[1].Value);

    /// <summary>Woordbegin-match: "Rune" telt ook in "Runes" en "Rune Deck",
    /// maar niet in "Prune".</summary>
    private static bool Occurs(string text, string term, bool ignoreCase) =>
        Regex.IsMatch(
            text, $@"\b{Regex.Escape(term)}",
            ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None,
            TimeSpan.FromSeconds(1));

    private static readonly Regex SectionRefPattern =
        new(@"§\s*(\d+(?:\.\d+)*)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
