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
/// ContentKind"/> (+ <see cref="Source.ContentKindSource"/> voor de herkomst).
///
/// <b>Degradatie:</b> AI-uitval of een onbruikbaar antwoord (<see
/// cref="Parse"/> geeft null) valt terug op <see cref="HeuristicKind"/> — de
/// oude <see cref="ClarificationSources"/>-substring-heuristiek, nu alleen nog
/// het deterministische vangnet. Nooit een harde 500: er komt altijd een
/// classificatie uit, alleen de herkomst verschilt. Een latere scan mag een
/// heuristische classificatie alsnog naar een LLM-oordeel upgraden (de
/// aanroeper-guard in IngestService her-classificeert zolang
/// ContentKindSource "heuristic" is); een LLM-oordeel wordt nooit stilzwijgend
/// door de heuristiek overschreven.
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
/// <b>Dubbelzinnig blijft veilig (#185-principe):</b> de prompt instrueert het
/// model expliciet dat een gemengd artikel (FAQ-stijl-uitleg ÉN een
/// regelwijziging in hetzelfde stuk, bv. "Rules FAQ and Patch Notes") als
/// "patch-notes" telt — het regelwijziging-signaal wint altijd, een bron
/// krijgt nooit twee kinds tegelijk.</summary>
public static class SourceContentKind
{
    public const string Faq = "faq";
    public const string PatchNotes = "patch-notes";
    public const string Other = "other";

    /// <summary>Herkomst-labels voor <see cref="Source.ContentKindSource"/>.</summary>
    public const string LlmOrigin = "llm";
    public const string HeuristicOrigin = "heuristic";

    /// <summary>Ruim genoeg om het onderwerp van een artikel te herkennen
    /// zonder de hele (soms lange) FAQ-/patch-notes-pagina mee te sturen —
    /// classificatie is een korte beslissing, geen extractie.</summary>
    public const int ContentSnippetLength = 1500;

    private static readonly HashSet<string> ValidKinds = [Faq, PatchNotes, Other];

    public const string SystemPrompt = """
        Je classificeert ÉÉN officiële bron voor een kennisbank over
        Riftbound, het League of Legends trading card game van Riot Games. Je
        krijgt de naam, de URL en het eerste deel van de inhoud van de bron.
        Antwoord UITSLUITEND met JSON:
        {"kind": "faq"|"patch-notes"|"other"}
        - "faq": een FAQ-/clarificatie-artikel — het legt bestaande regels,
          mechanieken of kaart-interacties uit (wat NU al geldt), zonder een
          regelWIJZIGING aan te kondigen
        - "patch-notes": een patch-notes-/regelwijziging-artikel — het
          kondigt aan dat een regel, kaart of mechaniek is VERANDERD (voor/na,
          "X is nu Y", een errata- of ban-aankondiging)
        - "other": iets anders (set-release-nieuws, toernooiaankondigingen,
          algemeen nieuws, …)
        - Een GEMENGD artikel (zowel FAQ-stijl-uitleg als een regelwijziging
          in hetzelfde stuk, bv. getiteld "Rules FAQ and Patch Notes") is
          "patch-notes" — het regelwijziging-signaal wint altijd; geef nooit
          een bron twee kinds tegelijk
        Geen tekst buiten de JSON.
        """;

    public static string BuildPrompt(string name, string url, string content) =>
        $"Naam: {name}\nURL: {url}\n\nEerste deel van de inhoud:\n{Truncate(content)}";

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
    /// drieledige kind-waarde als het LLM-oordeel. Patch-notes wint bij een
    /// dubbelzinnige naam (zelfde tie-break als vóór deze increment).</summary>
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

    private static string Truncate(string s) =>
        s.Length > ContentSnippetLength ? s[..ContentSnippetLength] : s;
}
