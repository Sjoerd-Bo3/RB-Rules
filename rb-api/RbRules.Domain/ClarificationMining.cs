using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Herkent FAQ-/clarificatie-artikelen (#177): de scan-pipeline knipt
/// en embedt elk artikel hetzelfde (vaste-lengte-slabs), maar zo'n pagina
/// mengt meerdere losse verduidelijkingen in doorlopende prose — één
/// embedding over de hele slab slaat de betekenis plat (elk concept verdunt
/// de andere; een gerichte vraag als "Legion = finalize" haalt het er dan
/// niet uit). Detectie is een simpele, betrouwbare naam-/URL-heuristiek —
/// geen migratie of apart bron-type-veld nodig, <see cref="Source"/> draagt
/// Id/Url/Name al: de kandidaten hebben een herkenbaar woord in hun slug/
/// titel (bv. "unleashed-rules-faq-and-clarifications",
/// "Unleashed Rules FAQ and Clarifications"). Puur en getest; de aanroeper
/// (<see cref="IngestService"/>, ClarificationMiningService) gate't zelf ook
/// op TrustTier == 1 — alleen een officiële bron krijgt automatisch een
/// verified ruling (#166-autoriteitsmodel); deze detector zegt alleen iets
/// over de vorm van de bron, niets over zijn gezag.</summary>
public static class ClarificationSources
{
    private static readonly string[] Keywords =
        ["faq", "clarification", "clarifications", "patch-notes", "patch notes"];

    public static bool IsMatch(string? id, string? url, string? name) =>
        HasKeyword(id) || HasKeyword(url) || HasKeyword(name);

    private static bool HasKeyword(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Eén uit een FAQ-/clarificatie-artikel gedestilleerd concept
/// (#177): een discrete, op zichzelf staande verduidelijking — het
/// tegenovergestelde van de vaste-lengte-slab die de tekst nu platslaat.
/// SectionRef is de optionele core-rule-§ die het artikel er expliciet bij
/// noemt (null als het artikel er geen noemt, of als TopicType zelf al
/// "section" is).</summary>
public record ExtractedClarification(
    string TopicType, string TopicRef, string Clarification, string? SectionRef, string? Quote);

/// <summary>Prompt + parser voor concept-extractie uit FAQ-/clarificatie-
/// artikelen (#177), zelfde patroon als <see cref="ClaimMiner"/>: de LLM-call
/// zelf loopt via rb-ai, dit deel is puur en getest. Anders dan de
/// claims-pipeline (community-interpretatie, ongeverifieerd tot corroboratie/
/// officiële toets) is de bron hier per definitie officieel (de aanroeper
/// filtert op TrustTier == 1) — elk item wordt dus direct een geverifieerde
/// ruling, geen kandidaat-claim.</summary>
public static class ClarificationMiner
{
    /// <summary>Cap per extractie-call — liever een handvol scherpe concepten
    /// dan een ongefilterde lijst.</summary>
    public const int MaxItems = 25;
    public const int MaxTopicRefLength = 120;
    /// <summary>Ruimer dan een claim-statement (400): een verduidelijking mag
    /// het "waarom" meenemen, niet alleen de bewering.</summary>
    public const int MaxClarificationLength = 600;
    public const int MaxSectionRefLength = 20;
    /// <summary>Auteursrecht (docs/KNOWLEDGE.md): korte citaten, geen
    /// overgenomen teksten.</summary>
    public const int MaxQuoteLength = 200;

    private static readonly HashSet<string> TopicTypes =
        ["card", "mechanic", "section", "concept"];

    public const string SystemPrompt = """
        Je bent de concept-extractor van een kennisbank over Riftbound, het
        League of Legends trading card game van Riot Games. Je krijgt tekst
        van een officiële FAQ-/clarificatie-pagina. Zo'n pagina mengt meerdere
        losse verduidelijkingen in doorlopende prose — één embedding over de
        hele tekst slaat de betekenis plat (elk concept verdunt de andere).
        Destilleer daarom elke DISCRETE verduidelijking als eigen, gefocust
        item. Antwoord UITSLUITEND met JSON:
        {"clarifications": [{"topicType": "...", "topicRef": "...", "clarification": "...", "sectionRef": "...", "quote": "..."}]}
        - topicType ∈ card | mechanic | section | concept
        - topicRef: het onderwerp — de mechaniek-/keywordnaam (bv. "Legion"),
          de kaartnaam, het §-nummer, of een kort concept
        - clarification: de verduidelijking zelf, GEPARAFRASEERD in het
          Nederlands (Engelse speltermen onvertaald), gefocust op ÉÉN
          concept, op zichzelf leesbaar (dus niet "zie hierboven" of "zoals
          hierboven vermeld")
        - sectionRef: alleen als het artikel expliciet naar een core-rule-§
          verwijst die dit concept ondersteunt (bv. "402.3"); leeg als het
          artikel er geen noemt
        - quote: kort letterlijk citaat uit de brontekst als bewijs (max ~25
          woorden)
        - Splits een alinea die meerdere keywords/concepten mengt in
          meerdere items — nooit één item met twee onderwerpen
        - Maximaal 25 items; liever 8 scherpe dan 25 wazige
        - Alleen concrete verduidelijkingen/regels; geen inleidende tekst,
          geen aankondigingen zonder regelinhoud
        - Niets bruikbaars? Antwoord {"clarifications": []}
        Geen tekst buiten de JSON.
        """;

    public static string BuildPrompt(string sourceName, string articleText) =>
        $"Bron: {sourceName}\n\nArtikeltekst:\n{articleText}";

    /// <summary>Tolerante JSON-extractie uit een LLM-antwoord. null bij
    /// mislukking (geen bruikbare JSON); een lege lijst betekent "geparsed,
    /// maar niets gevonden". Items zonder clarification of topicRef vallen
    /// weg; een onbekend topicType degradeert naar "concept"; duplicaten
    /// binnen het antwoord (zelfde onderwerp + genormaliseerde tekst) vallen
    /// weg — hergebruikt <see cref="ClaimMiner.GetString"/>/Truncate/
    /// NormalizeStatement, zelfde tolerantie-patroon als de claims-parser.</summary>
    public static IReadOnlyList<ExtractedClarification>? Parse(string raw)
    {
        var items = LlmJson.ExtractItems(raw, "clarifications");
        if (items is null) return null;

        var seen = new HashSet<string>();
        var result = new List<ExtractedClarification>();
        foreach (var item in items)
        {
            if (result.Count >= MaxItems) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var clarification = ClaimMiner.Truncate(
                ClaimMiner.GetString(item, "clarification"), MaxClarificationLength);
            var topicRef = ClaimMiner.Truncate(
                ClaimMiner.GetString(item, "topicRef"), MaxTopicRefLength);
            if (string.IsNullOrEmpty(clarification) || string.IsNullOrEmpty(topicRef)) continue;

            var dedupeKey = $"{topicRef.Trim().ToLowerInvariant()}|{ClaimMiner.NormalizeStatement(clarification)}";
            if (!seen.Add(dedupeKey)) continue;

            var topicType = ClaimMiner.GetString(item, "topicType")?.ToLowerInvariant();
            result.Add(new ExtractedClarification(
                topicType is not null && TopicTypes.Contains(topicType) ? topicType : "concept",
                topicRef,
                clarification,
                ClaimMiner.Truncate(ClaimMiner.GetString(item, "sectionRef"), MaxSectionRefLength),
                ClaimMiner.Truncate(ClaimMiner.GetString(item, "quote"), MaxQuoteLength)));
        }
        return result;
    }
}
