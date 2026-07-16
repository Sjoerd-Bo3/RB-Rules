using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Herkent FAQ-/clarificatie-artikelen (#177, bijgesteld #185): de
/// scan-pipeline knipt en embedt elk artikel hetzelfde (vaste-lengte-slabs),
/// maar zo'n pagina mengt meerdere losse verduidelijkingen in doorlopende
/// prose — één embedding over de hele slab slaat de betekenis plat (elk
/// concept verdunt de andere; een gerichte vraag als "Legion = finalize"
/// haalt het er dan niet uit). Detectie is een simpele, betrouwbare naam-/
/// URL-heuristiek — geen migratie of apart bron-type-veld nodig,
/// <see cref="Source"/> draagt Id/Url/Name al.
///
/// <b>#185-herkadering:</b> patch notes zijn UIT <see cref="IsMatch"/>
/// gehaald. Een patch-notes-artikel is een REGELWIJZIGING (delta:
/// "X is veranderd naar Y"), geen op-zichzelf-staande ruling — die hoort in
/// de wijzigingen-feed via de gewone ingest-diff (<see cref="IngestService"/>),
/// niet als geëxtraheerd concept. De #177-heuristiek matchte patch notes nog
/// wél mee, waardoor een aankondigingszin zonder regelinhoud (bv. "Legion is
/// verduidelijkt in deze patch") als "geverifieerde ruling" werd gemined — de
/// bug achter issue #185 (een lege Legion-"ruling" uit
/// core-rules-patch-notes). <see cref="IsPatchNotesSignal"/> blijft bestaan
/// als apart, getest predicaat: de opruimstap in
/// <see cref="RbRules.Infrastructure.ClarificationMiningService"/> gebruikt
/// hem om oude, ten onrechte gemínede patch-notes-Corrections terug te
/// vinden.
///
/// Puur en getest; de aanroeper (<see cref="IngestService"/>,
/// ClarificationMiningService) gate't zelf ook op TrustTier == 1 — alleen een
/// officiële bron krijgt automatisch een verified ruling
/// (#166-autoriteitsmodel); deze detector zegt alleen iets over de vorm van
/// de bron, niets over zijn gezag.
///
/// <b>Deterministisch vangnet sinds #188 increment 2.</b> Dit is niet langer
/// de primaire bron-type-classificatie: die is een LLM-BESLISSING (<see
/// cref="SourceContentKind"/>), gezet bij de scan van een trust-1-bron en
/// gepersisteerd op <see cref="Source.ContentKind"/> — een substring-match op
/// Id/Url/Name kan een bron zonder de magische woorden in zijn slug niet
/// herkennen, en een dubbelzinnige naam (bv. "Rules FAQ and Patch Notes")
/// matcht per ongeluk beide kanten. Deze klasse blijft bestaan als (a) het
/// vangnet waar <see cref="SourceContentKind.HeuristicKind"/> op terugvalt
/// bij AI-uitval/onbruikbaar LLM-antwoord, en (b) de transitionele
/// null-fallback (<see cref="SourceContentKind.Resolve"/>) voor bronnen die
/// nog niet opnieuw gescand zijn sinds deze increment.</summary>
public static class ClarificationSources
{
    private static readonly string[] FaqKeywords =
        ["faq", "clarification", "clarifications"];

    private static readonly string[] PatchNotesKeywords =
        ["patch-notes", "patch notes"];

    public static bool IsMatch(string? id, string? url, string? name) =>
        HasKeyword(id, FaqKeywords) || HasKeyword(url, FaqKeywords) || HasKeyword(name, FaqKeywords);

    /// <summary>#185: identificeert een patch-notes-bron — bewust NIET meer
    /// onderdeel van <see cref="IsMatch"/> (patch notes voeden de
    /// wijzigingen-feed, niet de clarify-mining), maar wel nodig voor de
    /// opruimstap die oude, vóór deze scheiding gemínede patch-notes-
    /// Corrections terugvindt.</summary>
    public static bool IsPatchNotesSignal(string? id, string? url, string? name) =>
        HasKeyword(id, PatchNotesKeywords) || HasKeyword(url, PatchNotesKeywords) || HasKeyword(name, PatchNotesKeywords);

    private static bool HasKeyword(string? text, string[] keywords) =>
        !string.IsNullOrWhiteSpace(text)
        && keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Citaat-grondigheidscheck voor de hybride autoriteitspoort (#177,
/// review-uitkomst): een auto-verified FAQ-ruling mag alleen door als het
/// <see cref="ExtractedClarification.Quote"/> écht in de brontekst voorkomt —
/// dat vangt een gehallucineerd/verzonnen citaat (de kernzorg uit de
/// autoriteits-review). De vergelijking is tolerant genormaliseerd
/// (kleine letters, samengevouwen witruimte, gangbare typografische
/// aanhalings-/streepjesvarianten teruggebracht) zodat een echt citaat niet
/// op opmaakruis struikelt, maar een verzonnen citaat nog steeds wegvalt.
/// Leeg/afwezig citaat ⇒ niet grounded (geen bewijs = geen automatische
/// verificatie). Puur en getest.</summary>
public static class ClarificationGrounding
{
    public static bool IsGrounded(string? quote, string? content)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(content))
            return false;
        var needle = Normalize(quote);
        return needle.Length > 0 && Normalize(content).Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>Kleine letters, witruimte samengevouwen tot één spatie,
    /// typografische varianten (curly quotes, en/em-dash, nbsp) terug naar hun
    /// ASCII-vorm — genoeg om opmaakverschillen tussen LLM-citaat en brontekst
    /// te overbruggen zonder de inhoud te veranderen.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var raw in s)
        {
            var c = Canonical(raw);
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace && sb.Length > 0) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static char Canonical(char c) => c switch
    {
        '\u2018' or '\u2019' or '\u02BC' or '\u2032' => '\'', // curly apostrofs, prime
        '\u201C' or '\u201D' or '\u2033' => '"',              // curly double quotes, double prime
        '\u2013' or '\u2014' or '\u2212' => '-',               // en-dash, em-dash, minus
        '\u00A0' or '\u202F' or '\u2009' => ' ',               // nbsp, narrow nbsp, thin space
        _ => c,
    };
}

/// <summary>Informativiteitscheck voor de hybride autoriteitspoort (#185):
/// grounded + anchored (#177) vangt een gehallucineerd citaat/verzonnen
/// anker, maar niet een citaat + onderwerp die wél kloppen terwijl de
/// "verduidelijking" zelf alleen een aankondiging is — de bug achter #185
/// (een Legion-"ruling" uit core-rules-patch-notes die alleen zei DÁT er iets
/// verduidelijkt was, niet WAT). <see cref="IsMetaOnly"/> matcht die vorm:
/// een KORTE zin die niets meer is dan "X is/was verduidelijkt/gewijzigd/
/// clarified/changed" zonder operatieve kern (geen definitie, regel of
/// interactie). Twee signalen samen, bewust geen los tekst-lengte- of
/// keyword-criterium: een lange, inhoudelijke verduidelijking die toevallig
/// het woord "verduidelijkt" bevat (bv. "... wat hierboven al is
/// verduidelijkt met het volgende voorbeeld: ...") blijft daarmee gewoon
/// informatief. Puur en getest, zelfde stijl als
/// <see cref="ClarificationGrounding"/>.
///
/// <b>Deterministische fallback wanneer het LLM-oordeel ontbreekt/uitvalt
/// (#188).</b> Sinds #188 is dit niet langer de primaire informativiteits-
/// poort: die is een LLM-oordeel — <see cref="ClarificationMiner"/> vraagt de
/// LLM zelf om een <see cref="ExtractedClarification.Operative"/>-veld per
/// geëxtraheerd item, en <see cref="JudgeSystemPrompt"/>/<see
/// cref="ParseOperative"/> hieronder her-toetsen opgeslagen tekst bij een
/// her-evaluatie (<see cref="RbRules.Infrastructure.CorrectionReevaluationService"/>).
/// Een regex kan "kondigt-een-wijziging-aan" niet écht van "beschrijft-de-
/// wijziging" onderscheiden — dat is een LLM-oordeel — maar als vangnet voor
/// wanneer dat oordeel ontbreekt (parse-gat, oude data zonder
/// <c>operative</c>-veld) of uitvalt (AI-uitval) is deze heuristiek beter dan
/// niets: nooit een harde 500, altijd een uitkomst.</summary>
public static class ClarificationInformativeness
{
    /// <summary>Boven deze lengte bevat een zin vrijwel altijd meer dan alleen
    /// de aankondiging — een operatieve verduidelijking moet immers de regel
    /// zelf uitleggen, dat kost ruimte.</summary>
    private const int MaxMetaOnlyLength = 140;

    /// <summary>Ondergrens voor een "echte" toelichting na een dubbele punt
    /// (zie <see cref="IsMetaOnly"/>) — een paar woorden, niet zomaar een
    /// leeg vervolg.</summary>
    private const int MinExplanationLength = 12;

    /// <summary>Aankondigingswerkwoord + voltooid-deelwoord-vorm, met een
    /// begrensde tussenruimte (onderwerp/bijwoordelijke bepaling als "in deze
    /// update") zodat zowel "Legion is verduidelijkt." als "Legion is in deze
    /// patch verduidelijkt." matchen.</summary>
    private static readonly Regex MetaAnnouncement = new(
        @"\b(is|zijn|werd(en)?|wordt|worden|was|were|has been|have been)\b.{0,40}?\b"
        + @"(verduidelijkt|verhelderd|gewijzigd|aangepast|bijgewerkt|veranderd|"
        + @"clarified|changed|updated|modified|made\s+clear)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMetaOnly(string? clarification)
    {
        if (string.IsNullOrWhiteSpace(clarification)) return true;
        var text = clarification.Trim();

        // Een expliciete toelichting ná een dubbele punt telt als operatieve
        // kern, ook als de zin ervóór een aankondigingswerkwoord bevat (bv.
        // "Legion is verduidelijkt: het betekent dat je een item op de chain
        // finalizet.") — de dubbele punt is hier het signaal dat de tekst
        // wél doorpakt naar WAT er geldt, niet alleen DAT er iets wijzigde.
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && text[(colon + 1)..].Trim().Length > MinExplanationLength)
            return false;

        return text.Length <= MaxMetaOnlyLength && MetaAnnouncement.IsMatch(text);
    }

    /// <summary>#188: lichte her-toets-prompt voor <see
    /// cref="RbRules.Infrastructure.CorrectionReevaluationService"/> — één
    /// opgeslagen verduidelijking (geen brontekst, geen lijst) langs dezelfde
    /// operatief/aankondiging-maatstaf als <see cref="ClarificationMiner"/>'s
    /// extractieprompt, met dezelfde twee letterlijke voorbeelden (adversariële
    /// review #185) zodat beide LLM-aanroepen consistent oordelen.</summary>
    public const string JudgeSystemPrompt = """
        You judge ONE clarification sentence about Riftbound, Riot Games'
        League of Legends trading card game. Respond ONLY with JSON:
        {"operative": true|false}
        - true: the sentence states the actual rule/definition/interaction —
          what now applies
        - false: the sentence only announces THAT something was clarified or
          changed, without saying WHAT now applies
        Example (operative: true): "The rule was clarified so that activated
        abilities with Legion trigger only once per turn."
        Example (operative: false): "Legion was clarified: refer to the
        updated core rules."
        No text outside the JSON.
        """;

    public static string BuildJudgePrompt(string clarification) =>
        $"Clarification:\n{clarification}";

    /// <summary>null bij onbruikbare output (geen JSON, geen boolean
    /// "operative"-veld) — de aanroeper degradeert dan naar <see
    /// cref="IsMetaOnly"/>, nooit een crash.</summary>
    public static bool? ParseOperative(string raw)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                // Objectvorm-guard (net als ClaimJudge.Map/OfficialCheck.Map):
                // LlmJson.Candidates levert óók array-blokken ("[402.3]",
                // "[true]") op, en GetBool → TryGetProperty gooit op een
                // niet-object een InvalidOperationException — géén JsonException,
                // dus de catch hieronder vangt 'm niet en de her-evaluatie zou
                // 500'en i.p.v. te degraderen naar IsMetaOnly (contract:
                // "nooit een crash").
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && ClaimMiner.GetBool(doc.RootElement, "operative") is { } operative)
                    return operative;
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }
}

/// <summary>Eén uit een FAQ-/clarificatie-artikel gedestilleerd concept
/// (#177): een discrete, op zichzelf staande verduidelijking — het
/// tegenovergestelde van de vaste-lengte-slab die de tekst nu platslaat.
/// SectionRef is de optionele core-rule-§ die het artikel er expliciet bij
/// noemt (null als het artikel er geen noemt, of als TopicType zelf al
/// "section" is). Operative (#188) is het LLM-oordeel over informativiteit —
/// true als de verduidelijking de échte regel/definitie/interactie stelt,
/// false als het slechts aankondigt DÁT iets is verduidelijkt zonder te
/// zeggen WAT er nu geldt, null als het antwoord geen bruikbaar
/// <c>operative</c>-veld bevatte (oude prompt-variant, parse-gat). Transient:
/// niet opgeslagen op <see cref="RbRules.Domain.Correction"/> — alleen
/// gebruikt door de poort in <c>ClarificationMiningService.StoreAsync</c>,
/// met <see cref="ClarificationInformativeness.IsMetaOnly"/> als
/// deterministische fallback wanneer dit veld null is.</summary>
public record ExtractedClarification(
    string TopicType, string TopicRef, string Clarification, string? SectionRef, string? Quote,
    bool? Operative = null);

/// <summary>Anker-correctie in een beheerder-opmerking (#184): een reviewqueue-
/// item kan fout aangeankerd zijn (verkeerd of onherkend onderwerp) terwijl de
/// verduidelijking zelf prima klopt. In plaats van de hele extractie opnieuw te
/// laten draaien, mag de beheerder het anker expliciet corrigeren door de
/// opmerking op een eigen regel af te sluiten met "<c>type:onderwerp</c>", bv.
/// "mechanic:Recall", "card:Taric, the Shield of Valoran" of "section:402.3" —
/// <see cref="RbRules.Infrastructure.CorrectionReevaluationService"/> gebruikt
/// dit anker in plaats van het oorspronkelijke Scope/Ref bij de her-evaluatie.
/// Puur/regex-based (geen I/O); staat er geen anker-regel in, dan is er niets
/// te overschrijven (null) en her-evalueert de her-evaluatie met het
/// bestaande onderwerp. Bij meerdere ankerregels in dezelfde opmerking wint de
/// laatste (de meest recente correctie).</summary>
public static class ReviewNoteAnchor
{
    // Ankerregel op zichzelf: "type:onderwerp", optioneel afgesloten met een
    // punt. RegexOptions.Multiline zodat ^/$ per regel matchen (de opmerking
    // mag toelichting bevatten vóór de ankerregel); de alternatie beperkt
    // topicType meteen tot de vier bekende waarden (ClaimTopicMapper.Resolve).
    private static readonly Regex Pattern = new(
        @"^\s*(card|mechanic|section|concept)\s*:\s*(.+?)\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static (string TopicType, string TopicRef)? TryParse(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;

        Match? last = null;
        foreach (Match m in Pattern.Matches(note))
            if (m.Groups[2].Value.Trim().Length > 0) last = m;
        if (last is null) return null;

        return (last.Groups[1].Value.ToLowerInvariant(), last.Groups[2].Value.Trim());
    }
}

/// <summary>Prompt + parser voor concept-extractie uit FAQ-/clarificatie-
/// artikelen (#177), zelfde patroon als <see cref="ClaimMiner"/>: de LLM-call
/// zelf loopt via rb-ai, dit deel is puur en getest. Anders dan de
/// claims-pipeline (community-interpretatie, ongeverifieerd tot corroboratie/
/// officiële toets) is de bron hier per definitie officieel (de aanroeper
/// filtert op TrustTier == 1) — elk item wordt dus direct een geverifieerde
/// ruling, geen kandidaat-claim.
///
/// <b>Opslagtaal (#185, Sjoerd-eis):</b> de extractie zelf (<see
/// cref="ExtractedClarification.Clarification"/>) wordt in het ENGELS
/// opgeslagen, dicht bij de bewoording van de (Engelse) brontekst — GEEN
/// vertaling naar het Nederlands. Een Nederlandse parafrase verliest
/// fidelity ten opzichte van de officiële bewoording en zou de verder
/// Engelstalige embedding-/rulings-corpus met een andere taal vervuilen;
/// <c>/ask</c> vertaalt bij het antwoorden toch al naar het Nederlands voor
/// de gebruiker (CLAUDE.md-werkafspraak 1 geldt dus voor de chat-laag, niet
/// voor deze opslaglaag).
///
/// <b>Informativiteits-oordeel (#188):</b> naast de extractie zelf vraagt de
/// prompt de LLM ook om <see cref="ExtractedClarification.Operative"/> per
/// item te zetten — is dit item de échte regel/definitie/interactie, of
/// slechts een aankondiging dat er iets gewijzigd is? Dat is een
/// classificatie-BESLISSING die het model beter kan nemen dan een regex
/// (adversariële review #185 vond twee kanten waarop <see
/// cref="ClarificationInformativeness.IsMetaOnly"/> ernaast zit); die regex
/// blijft alleen nog als deterministisch vangnet voor wanneer dit veld
/// ontbreekt.</summary>
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
        {"clarifications": [{"topicType": "...", "topicRef": "...", "clarification": "...", "sectionRef": "...", "quote": "...", "operative": true|false}]}
        - topicType ∈ card | mechanic | section | concept
        - topicRef: het onderwerp — de mechaniek-/keywordnaam (bv. "Legion"),
          de kaartnaam, het §-nummer, of een kort concept
        - clarification: de OPERATIEVE kern — wat de mechaniek dóét, de
          definitie zelf, of hoe een interactie resolvet — in het ENGELS,
          dicht bij de bewoording van de brontekst (GEEN vertaling naar het
          Nederlands: de brontekst is al Engels en de embedding-/rulings-
          corpus is Engels — een Nederlandse parafrase verliest fidelity ten
          opzichte van de officiële bewoording en vervuilt die corpus met een
          andere taal; /ask vertaalt zelf al naar het Nederlands voor de
          gebruiker, de OPSLAG hoort in de brontaal), gefocust op ÉÉN
          concept, op zichzelf leesbaar (dus niet "see above" of "as
          mentioned above"). Gaat het om een mechaniek, leg dan de werkende
          uitspraak vast (wat er gebeurt, wanneer) MÉT het verbindende
          voorbeeld uit de tekst (bv. een kaartnaam die de regel illustreert)
          — dat voorbeeld is vaak wat de verduidelijking pas concreet maakt
        - NOOIT alleen een kader-/aankondigingszin overnemen als item: zinnen
          die alleen zeggen DÁT iets "is verduidelijkt", "is gewijzigd" of
          "made clear" is GEEN clarification als de zin zelf niet zegt WAT er
          nu geldt — sla zo'n zin over, of pak 'm er alleen bij als de tekst
          er direct op laat volgen wat de regel nu inhoudt (dan is DAT de
          clarification, niet de aankondiging)
        - sectionRef: alleen als het artikel expliciet naar een core-rule-§
          verwijst die dit concept ondersteunt (bv. "402.3"); leeg als het
          artikel er geen noemt
        - quote: kort letterlijk citaat uit de brontekst als bewijs (max ~25
          woorden) — bij voorkeur het stuk met de operatieve kern, niet de
          aankondigingszin ervoor
        - operative: jouw oordeel of "clarification" hierboven de ECHTE regel/
          definitie/interactie stelt (true), of slechts aankondigt DAT iets is
          verduidelijkt/gewijzigd zonder te zeggen WAT er nu geldt (false).
          Voorbeeld (operative: true): "The rule was clarified so that
          activated abilities with Legion trigger only once per turn."
          Voorbeeld (operative: false): "Legion was clarified: refer to the
          updated core rules."
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
    /// NormalizeStatement, zelfde tolerantie-patroon als de claims-parser.
    /// <c>operative</c> (#188) leest via <see cref="ClaimMiner.GetBool"/>: een
    /// ontbrekend of niet-boolean veld geeft null, niet false — de aanroeper
    /// (ClarificationMiningService.StoreAsync) herkent null als "geen
    /// LLM-oordeel" en valt dan terug op <see
    /// cref="ClarificationInformativeness.IsMetaOnly"/>.</summary>
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
                ClaimMiner.Truncate(ClaimMiner.GetString(item, "quote"), MaxQuoteLength),
                ClaimMiner.GetBool(item, "operative")));
        }
        return result;
    }
}
