using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Bron-type-classificatie (#188 increment 2): is een trust-1-bron
/// een FAQ-/clarificatie-artikel, een patch-notes-artikel (regelwijziging),
/// of iets anders? Vóór deze increment besliste <see cref="ClarificationSources"/>
/// dat met een substring-keyword-heuristiek over Id/Url/Name, op drie plekken
/// (clarify-mining-bronselectie, patch-notes-retractie, de templated Change in
/// <see cref="RbRules.Infrastructure.IngestService"/>) — een bron zonder de
/// magische woorden in zijn slug (of een dubbelzinnige naam als "Rules FAQ and
/// Patch Notes") kon zo verkeerd of helemaal niet herkend worden. Dit is nu een
/// classificatie-BESLISSING die een LLM beter kan nemen dan een regex (zelfde
/// motivatie als <see cref="ClarificationInformativeness"/> in increment 1):
/// <see cref="RbRules.Infrastructure.IngestService"/> vraagt rb-ai bij de scan
/// van een trust-1-bron één keer om een oordeel op naam + URL + een kort
/// content-fragment, en persisteert het resultaat op <see cref="Source.
/// ContentKind"/> (+ <see cref="Source.ContentKindSource"/> voor de herkomst:
/// "llm", "heuristic" of "admin").
///
/// <b>Degradatie:</b> AI-uitval of een onbruikbaar antwoord (<see
/// cref="Parse"/> geeft null) valt terug op <see cref="HeuristicKind"/> — de
/// oude <see cref="ClarificationSources"/>-substring-heuristiek, nu alleen nog
/// het deterministische vangnet. Nooit een harde 500: er komt altijd een
/// classificatie uit, alleen de herkomst verschilt. Een latere scan mag een
/// heuristische classificatie alsnog naar een LLM-oordeel upgraden (de
/// aanroeper-guard in IngestService her-classificeert zolang
/// ContentKindSource "heuristic" is); een LLM- of admin-oordeel wordt nooit
/// stilzwijgend overschreven.
///
/// <b>Transitioneel:</b> <see cref="Resolve"/> is de ene plek die consumers
/// (ClarificationMiningService, IngestService, SourceDossierService) gebruiken
/// om de EFFECTIEVE kind te bepalen: de gepersisteerde classificatie als die
/// er is, anders <see cref="HeuristicKind"/> — zodat een bron die nog niet
/// (opnieuw) gescand is sinds deze increment gewoon blijft werken zoals
/// vóór #188 increment 2, tot de eerstvolgende scan hem een echte
/// classificatie geeft (géén apart backfill-commando nodig, zelfde
/// "backfilt vanzelf"-patroon als <see
/// cref="RbRules.Infrastructure.ClarificationMiningService"/>).
///
/// <b>Dubbelzinnig/onzeker is "other" (#188-review, herziening van de
/// #185-tie-break):</b> de prompt instrueert het model dat een gemengd of
/// onduidelijk artikel (bv. "Rules FAQ and Patch Notes") als "other" telt —
/// neutraal: niet gemined als FAQ, niet geretract als patch-notes. De oude
/// "patch-notes wint"-regel uit #185 was destijds de veilige keuze omdat een
/// FAQ-misclassificatie lege aankondigings-"rulings" opleverde; sinds
/// increment 1 beschermt de operative-poort daar al op itemniveau tegen, en
/// sinds de consensus-poort in <see cref="RbRules.Infrastructure.
/// ClarificationMiningService"/> (RetractPatchNotesCorrectionsAsync) is een
/// patch-notes-misclassificatie niet meer destructief — neutraal-bij-twijfel
/// is nu de veiligste regel. De HEURISTISCHE tie-break (<see
/// cref="HeuristicKind"/>: patch-notes wint bij een dubbel-keyword-naam)
/// blijft wél zoals #185 hem koos — zie de docstring daar.
///
/// <b>Beheerder-override (#188-review, fix C):</b> <see
/// cref="TryApplyOverride"/> laat het bestaande source-PATCH-pad een kind
/// expliciet vastzetten (herkomst "admin" — wordt door de scan-guard nooit
/// geherclassificeerd en telt in de consensus-poort van de retractie als
/// menselijke bevestiging) of wissen (lege string ⇒ terug naar
/// herclassificatie bij de eerstvolgende scan, zelfde leeg-is-expliciet-
/// wissen-conventie als <c>FeedPatch.CategoryFilter</c>).</summary>
public static class SourceContentKind
{
    public const string Faq = "faq";
    public const string PatchNotes = "patch-notes";
    public const string Other = "other";

    /// <summary>Herkomst-labels voor <see cref="Source.ContentKindSource"/>.</summary>
    public const string LlmOrigin = "llm";
    public const string HeuristicOrigin = "heuristic";
    public const string AdminOrigin = "admin";

    /// <summary>Ruim genoeg om het onderwerp van een artikel te herkennen
    /// zonder de hele (soms lange) FAQ-/patch-notes-pagina mee te sturen —
    /// classificatie is een korte beslissing, geen extractie.</summary>
    public const int ContentSnippetLength = 1500;

    private static readonly HashSet<string> ValidKinds = [Faq, PatchNotes, Other];

    /// <summary>Engels (#187-lijn: consistent Engels houdt de mining
    /// eentalig — ClaimJudge werd om dezelfde reden vertaald, ook al slaat
    /// die alleen een enum op). "faq" is bewust beperkt tot Q&amp;A-/
    /// clarificatie-ARTIKELEN: een volledig rulebook of leer-het-spel-gids
    /// legt óók regels uit maar is geen verzameling losse verduidelijkingen —
    /// de clarify-mining zou er alleen ruis uit halen (#188-review, finding
    /// 2/4); de voorbeelden (core rules PDF, how-to-play, gameplay guide,
    /// deckbuilding primer) staan er letterlijk in. Tie-break: gemengd/
    /// onzeker ⇒ "other" (neutraal), zie de klasse-docstring.</summary>
    public const string SystemPrompt = """
        You classify ONE official source for a knowledge base about
        Riftbound, Riot Games' League of Legends trading card game. You are
        given the source's name, its URL and the first part of its content.
        Respond ONLY with JSON:
        {"kind": "faq"|"patch-notes"|"other"}
        - "faq": a Q&A/clarification ARTICLE — a news-style piece that
          answers questions about or clarifies EXISTING rules, mechanics or
          card interactions (what already applies), without announcing a
          rule change. Comprehensive rules documents and learn-to-play
          material are NOT "faq" even though they explain rules: a full
          rulebook or core rules PDF, a how-to-play or gameplay guide, and a
          deckbuilding primer are all "other"
        - "patch-notes": a patch-notes/rule-change article — it announces
          that a rule, card or mechanic has CHANGED (before/after, "X is now
          Y", an errata or ban announcement)
        - "other": everything else — rulebooks and guides (see above),
          set-release news, tournament announcements, general news, …
        - A MIXED or unclear article (e.g. one that contains both Q&A-style
          clarifications and rule changes, such as "Rules FAQ and Patch
          Notes") is "other" — when in doubt, answer "other"; never give a
          source two kinds
        No text outside the JSON.
        """;

    public static string BuildPrompt(string name, string url, string content) =>
        $"Name: {name}\nURL: {url}\n\nFirst part of the content:\n{Truncate(content)}";

    /// <summary>null bij onbruikbare output (geen JSON, geen geldige "kind"-
    /// waarde) — de aanroeper degradeert dan naar <see cref="HeuristicKind"/>,
    /// nooit een crash.</summary>
    public static string? Parse(string raw)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                // Objectvorm-guard (#188-inc1-reviewles, zelfde patroon als
                // ClarificationInformativeness.ParseOperative): LlmJson.
                // Candidates levert ook array-blokken op ("[402.3]",
                // "[true]"), en TryGetProperty op een niet-object gooit een
                // InvalidOperationException — géén JsonException, dus zonder
                // deze guard zou de catch hieronder 'm niet vangen en zou de
                // scan 500'en i.p.v. te degraderen naar de heuristiek.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && ClaimMiner.GetString(doc.RootElement, "kind") is { } kind
                    && ValidKinds.Contains(kind.ToLowerInvariant()))
                    return kind.ToLowerInvariant();
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    /// <summary>Deterministisch vangnet: de oude <see cref="ClarificationSources"/>-
    /// substring-heuristiek op Id/Url/Name, herverpakt tot dezelfde
    /// drieledige kind-waarde als het LLM-oordeel. Patch-notes wint hier bij
    /// een dubbel-keyword-naam — bewust ANDERS dan de prompt-tie-break
    /// ("other", zie de klasse-docstring): het vangnet bepaalt vooral of een
    /// bron uit de mining geweerd wordt (daar blijft het #185-conservatisme
    /// gewenst) en dient in de retractie als consensus-bevestiging, waar het
    /// destructieve pad juist dit sterke, deterministische keyword-signaal
    /// vereist.</summary>
    public static string HeuristicKind(string? id, string? url, string? name) =>
        ClarificationSources.IsPatchNotesSignal(id, url, name) ? PatchNotes
        : ClarificationSources.IsMatch(id, url, name) ? Faq
        : Other;

    /// <summary>De effectieve kind voor een bron: de gepersisteerde
    /// classificatie (<paramref name="contentKind"/>) als die er is, anders
    /// de heuristiek — transitioneel gedrag totdat elke trust-1-bron
    /// minstens één keer opnieuw gescand is sinds deze increment.</summary>
    public static string Resolve(string? contentKind, string? id, string? url, string? name) =>
        contentKind ?? HeuristicKind(id, url, name);

    /// <summary>Beheerder-override via het source-PATCH-pad (#188-review,
    /// fix C). Een geldige kind zet <see cref="Source.ContentKindSource"/> op
    /// "admin" — de scan-guard (<see cref="RbRules.Infrastructure.
    /// IngestService"/>) herclassificeert zo'n bron nooit meer en de
    /// consensus-poort van de retractie accepteert het als menselijke
    /// bevestiging. Een lege string wist de classificatie (beide kolommen
    /// null ⇒ herclassificatie bij de eerstvolgende scan) — zelfde
    /// leeg-is-expliciet-wissen-conventie als <c>FeedPatch.CategoryFilter</c>.
    /// false bij een ongeldige waarde (endpoint ⇒ 400), de bron blijft dan
    /// ongemoeid.</summary>
    public static bool TryApplyOverride(Source src, string value)
    {
        var kind = value.Trim().ToLowerInvariant();
        if (kind.Length == 0)
        {
            src.ContentKind = null;
            src.ContentKindSource = null;
            return true;
        }
        if (!ValidKinds.Contains(kind)) return false;
        src.ContentKind = kind;
        src.ContentKindSource = AdminOrigin;
        return true;
    }

    private static string Truncate(string s) =>
        s.Length > ContentSnippetLength ? s[..ContentSnippetLength] : s;
}
