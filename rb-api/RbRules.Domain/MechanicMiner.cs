using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Het LLM-deel van één gemínede kaart (#211): de mechanieken die de
/// gebracket-vorm NIET oplevert, plus de semantische velden. De gebrackete
/// mechanieken zitten hier bewust niet in — die komen deterministisch uit
/// <see cref="MechanicMiner.Analyze"/>.</summary>
public record MinedCard(string Id, string[] ExtraMechanics, string[] Triggers, string[] Effects);

/// <summary>Tekstfragment rond een keyword-voorkoming (#123); Match is de
/// volledige bracketed vorm ("[Assault 2]") zodat de UI die kan markeren.</summary>
public record KeywordSnippet(string Before, string Match, string After);

/// <summary>Deterministische voorbewerking van één kaarttekst (#211).
/// <paramref name="Bracketed"/> = de mechanieken die letterlijk gebracket in de
/// tekst staan (gratis, altijd beschikbaar, ook bij rb-ai-uitval);
/// <paramref name="Candidates"/> = bekende vocabulaire-termen die ONGEBRACKET in
/// de tekst voorkomen — het gesloten lijstje waarover het LLM-oordeel gaat.</summary>
public sealed record CardMechanics(string[] Bracketed, string[] Candidates);

/// <summary>Eén kaart plus zijn deterministische voorbewerking, zoals die de
/// prompt in gaat.</summary>
public sealed record MiningInput(Card Card, CardMechanics Analysis);

/// <summary>Prompt + parser voor het minen van kaartmechanieken (F3).
/// De LLM-call zelf loopt via rb-ai; dit deel is puur en getest.
///
/// <b>Werkverdeling sinds #211 (#188-restant, les uit #249).</b> Riot zet elke
/// keyword-mechaniek letterlijk tussen blokhaken in de kaarttekst
/// ("[Quick-Draw]", "[Equip]", "[Assault 2]"). Dat is deterministisch én
/// gratis af te leiden, dus doet <see cref="Analyze"/> dat met een regex —
/// een LLM inzetten voor wat de druk-vorm al zegt is pure verspilling (#249:
/// 69% van de brein-extractie herkauwde wat <c>Card.Mechanics[]</c> al wist).
/// Meting op de 1429 live kaartteksten met tekst: 31 verschillende keywords,
/// állemaal gebracket; slechts ~47 vermeldingen (≈3%) staan érgens zónder
/// blokhaken.
///
/// Precies díé rest is wél een oordeel, en dus het LLM-deel: Riot drukt
/// "Equip :rb_rune_body:" (Jagged Cutlass) en "Ganking (I can move…)"
/// (Laurent Bladekeeper) af zónder haken — daar HEEFT de kaart de mechaniek —
/// terwijl "Repeat this gear's play effect" (Sprite Fountain) gewoon Engels
/// is. Een naïeve woord-match zou die twee niet uit elkaar houden. De LLM
/// krijgt daarom een GESLOTEN kandidatenlijst (<see cref="CardMechanics.
/// Candidates"/>) en beslist alleen per term "spelterm of gewoon woord"; de
/// validatie achteraf (<see cref="MergeMechanics"/>) laat niets door dat niet
/// in die lijst stond. Nieuwe termen komen dus nooit via het LLM binnen —
/// die lopen via de kandidatenqueue langs een mens (#52).
///
/// Semantiek van <c>Card.Mechanics</c> is ongewijzigd: keyword-mechanieken die
/// in de tekst voorkomen of het gedrag van de kaart bepalen (een kaart die
/// "[Assault 2]" uitdeelt telt dus mee) — dat is wat de graph-projectie als
/// HAS_MECHANIC gebruikt.</summary>
public static partial class MechanicMiner
{
    /// <summary>Bekende Riftbound-mechanieken (basislijst; groeit via
    /// geaccepteerde MechanicKeywords, zie Vocabulary).</summary>
    public static readonly string[] SeedVocabulary =
    [
        "Accelerate", "Tank", "Deflect", "Hidden", "Shield", "Legion",
        "Deathknell", "Reaction", "Action", "Temporary", "Recycle",
    ];

    /// <summary>Langste aanvaarde keyword-vorm; langer is vrijwel zeker geen
    /// keyword maar een tussen haken gezette zin.</summary>
    private const int MaxKeywordLength = 30;

    public const string SystemPrompt = """
        Je analyseert Riftbound TCG kaartteksten.

        Keyword-mechanieken staan in de kaarttekst tussen blokhaken ("[Equip]",
        "[Assault 2]"). Die zijn AL deterministisch geëxtraheerd en staan per
        kaart achter "mechanics:". Neem ze NIET opnieuw op — daar is geen
        oordeel voor nodig.

        Extraheer per kaart:
        - "extraMechanics": uitsluitend termen uit de lijst achter "kandidaten:"
          van diezelfde kaart. Dat zijn bekende keywords die in die kaarttekst
          voorkomen ZONDER blokhaken. Beslis per term of hij daar als SPELTERM
          wordt gebruikt — bv. "Equip :rb_rune_body: (Attach this to a unit…)",
          "Ganking (I can move from battlefield to battlefield.)", "Buff a
          friendly unit" — of als gewoon Engels woord, bv. "Repeat this gear's
          play effect". Alleen speltermen opnemen. Geen kandidaten ⇒ lege
          array. Neem NOOIT een term op die niet in de kandidatenlijst van die
          kaart staat; nieuwe keywords worden elders gecureerd.
        - "triggers": condities die iets laten gebeuren, kort genormaliseerd in
          het Engels (bv. "when I conquer", "when a unit dies", "when played").
        - "effects": wat de kaart doet, kort genormaliseerd in het Engels
          (bv. "kill a unit", "draw a card", "buff might", "move a unit").
        Antwoord UITSLUITEND met een JSON-array:
        [{"id": "...", "extraMechanics": [...], "triggers": [...], "effects": [...]}]
        Eén element per kaart, zelfde ids als de input. Lege arrays zijn prima.
        Geen tekst buiten de JSON.
        """;

    /// <summary>Effectief vocabulaire: seed + geaccepteerde keywords,
    /// gededupliceerd (case-insensitive, seed-spelling wint).</summary>
    public static IReadOnlyList<string> Vocabulary(IEnumerable<string>? accepted = null)
    {
        if (accepted is null) return SeedVocabulary;
        var seen = new HashSet<string>(SeedVocabulary, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(SeedVocabulary);
        foreach (var term in accepted)
        {
            var t = term.Trim();
            if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    /// <summary>Deterministische voorbewerking van één kaarttekst (#211): wat
    /// de gebracket-vorm zegt, en waarover het LLM-oordeel nog moet gaan.
    /// <paramref name="rejected"/> zijn de door beheer verworpen termen — een
    /// mens heeft "dit is geen mechaniek" gezegd, dus die verschijnen noch als
    /// mechaniek noch als kandidaat.</summary>
    public static CardMechanics Analyze(
        string? textPlain,
        IEnumerable<string>? vocabulary = null,
        IEnumerable<string>? rejected = null)
    {
        if (string.IsNullOrWhiteSpace(textPlain)) return new([], []);
        var blocked = new HashSet<string>(rejected ?? [], StringComparer.OrdinalIgnoreCase);

        var bracketed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, term) in BracketedKeywords(textPlain))
            if (!blocked.Contains(term) && seen.Add(term)) bracketed.Add(term);

        // Kandidaten: vocabulaire-termen die ergens BUITEN de blokhaken staan.
        // Woord-voor-woord vergeleken, niet als substring — "Leveling" is geen
        // voorkoming van "Level".
        var words = Words(BracketedTerm().Replace(textPlain, " "));
        var candidates = new List<string>();
        foreach (var term in vocabulary ?? SeedVocabulary)
        {
            var t = term.Trim();
            if (t.Length == 0 || blocked.Contains(t) || seen.Contains(t)) continue;
            if (ContainsPhrase(words, Words(t)) && seen.Add(t)) candidates.Add(t);
        }
        return new([.. bracketed], [.. candidates]);
    }

    public static string BuildPrompt(IEnumerable<MiningInput> cards)
    {
        var sb = new StringBuilder("Kaarten:\n");
        foreach (var (c, analysis) in cards)
        {
            sb.AppendLine($"- id: {c.RiftboundId}");
            sb.AppendLine($"  naam: {c.Name} ({c.Type ?? "?"})");
            sb.AppendLine($"  tekst: {c.TextPlain ?? "(geen tekst)"}");
            sb.AppendLine($"  mechanics: {Join(analysis.Bracketed)}");
            sb.AppendLine($"  kandidaten: {Join(analysis.Candidates)}");
        }
        return sb.ToString();
    }

    /// <summary>Parseert het batch-antwoord. Er is bewust géén objectvorm-guard
    /// nodig (de #188-les die <c>ParseOperative</c> wél moest leren): het stuk
    /// dat hier geparsed wordt begint per constructie op '[', dus de root is
    /// altijd een array of het parsen faalt — <see cref="JsonElement.
    /// EnumerateArray"/> kan hier niet op een niet-array stuklopen. Datzelfde
    /// knippen redt bovendien het array uit een objectomhulsel
    /// (<c>{"cards": [...]}</c>). Alles wat overblijft is een lege lijst: de
    /// aanroeper laat de kaart dan in de wachtrij staan — nooit een crash.</summary>
    public static IReadOnlyList<MinedCard> ParseBatch(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var results = new List<MinedCard>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                // "mechanics" als terugvalsleutel: een model dat de oude naam
                // gebruikt loopt daarna gewoon door dezelfde poort (MergeMechanics).
                var extra = Strings(item, "extraMechanics");
                results.Add(new MinedCard(
                    id!,
                    extra.Length > 0 ? extra : Strings(item, "mechanics"),
                    Strings(item, "triggers"),
                    Strings(item, "effects")));
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Deterministische validatie ná het LLM-oordeel (#211, #188-les
    /// "geen promoverende actie op een LLM-oordeel alléén"). De gebrackete
    /// mechanieken staan vast — de LLM kan er alleen bij, nooit af — en een
    /// voorgestelde term telt alleen mee als hij in de gesloten kandidatenlijst
    /// van diezelfde kaart stond. Die lijst is zelf deterministisch afgeleid
    /// (bekend vocabulaire × letterlijke voorkoming in de kaarttekst), dus een
    /// gehallucineerde of elders vandaan gehaalde term valt weg. De spelling
    /// van de kandidatenlijst wint, zodat casing-variatie geen dubbele
    /// mechaniek oplevert.</summary>
    public static string[] MergeMechanics(
        IReadOnlyList<string> bracketed, IEnumerable<string> proposed, IReadOnlyList<string> offered)
    {
        var result = new List<string>(bracketed);
        var seen = new HashSet<string>(bracketed, StringComparer.OrdinalIgnoreCase);
        foreach (var value in proposed)
        {
            var match = offered.FirstOrDefault(
                o => o.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && seen.Add(match)) result.Add(match);
        }
        return [.. result];
    }

    /// <summary>Keyword-kandidaten in een kaarttekst (#52): bracketed termen
    /// ("[Ganking]", "[Assault 2]") die niet in het vocabulaire staan. Puur en
    /// deterministisch — geen LLM nodig. Numerieke parameters worden gestript
    /// ("Assault 2" → "Assault"); ruis als "[&gt;]" (icoon-pijl) en "[NO TEXT]"
    /// valt af doordat een keyword met een hoofdletter + kleine letter begint
    /// en verder alleen uit letters/spaties/koppeltekens bestaat.</summary>
    public static IReadOnlyList<string> ExtractKeywordCandidates(
        string? textPlain, IEnumerable<string> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(textPlain)) return [];
        var known = new HashSet<string>(vocabulary, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var (_, term) in BracketedKeywords(textPlain))
            if (known.Add(term)) found.Add(term); // dedupe + vocab-filter ineen
        return found;
    }

    /// <summary>Kort tekstfragment rond de eerste bracketed voorkoming van een
    /// keyword (#123): zelfde herkenning als <see cref="Analyze"/> — de
    /// numerieke parameter hoort bij de match ("[Assault 2]" is bewijs voor
    /// term "Assault") en de vergelijking is case-insensitive, net als de
    /// dedupe in de kandidaten-harvest. Drie delen (voor/match/na) zodat de
    /// UI de term kan markeren zonder {@html}.</summary>
    public static KeywordSnippet? SnippetFor(string? textPlain, string term, int context = 60)
    {
        if (string.IsNullOrWhiteSpace(textPlain) || string.IsNullOrWhiteSpace(term)) return null;
        foreach (var (m, inner) in BracketedKeywords(textPlain))
        {
            if (!inner.Equals(term.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var start = Math.Max(0, m.Index - context);
            var end = Math.Min(textPlain.Length, m.Index + m.Length + context);
            return new(
                (start > 0 ? "…" : "") + textPlain[start..m.Index],
                m.Value,
                textPlain[(m.Index + m.Length)..end] + (end < textPlain.Length ? "…" : ""));
        }
        return null;
    }

    /// <summary>Elke bracketed keyword-voorkoming met zijn FAMILIE-naam: de
    /// magnitude is gestript ("[Assault 2]" → "Assault"), want 'Assault 2' en
    /// 'Assault 3' zijn dezelfde mechaniek — zij mag nooit tot een aparte
    /// entiteit uiteenvallen (zie <see cref="CanonicalEntity.CanonicalLabel"/>);
    /// de magnitude wordt hier WEGGEGOOID (`NumericParameter().Replace`). De
    /// ontologie declareert weliswaar een `magnitude`-parameter op HAS_MECHANIC,
    /// maar de projectie schrijft geen edge-properties — die parameter is dus
    /// voorgenomen, niet bestaand (#274-review). Eén
    /// herkenner voor de mechaniek-extractie, de kandidaten-harvest én het
    /// bewijs-snippet — die drie mogen niet uiteenlopen.</summary>
    private static IEnumerable<(Match Match, string Term)> BracketedKeywords(string textPlain)
    {
        foreach (Match m in BracketedTerm().Matches(textPlain))
        {
            var term = NumericParameter().Replace(m.Groups[1].Value.Trim(), "");
            if (KeywordShape().IsMatch(term) && term.Length <= MaxKeywordLength)
                yield return (m, term);
        }
    }

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex BracketedTerm();

    /// <summary>Trailing numeriek argument van een keyword ("Deflect 2").</summary>
    [GeneratedRegex(@"\s+\d+$")]
    private static partial Regex NumericParameter();

    [GeneratedRegex(@"^[A-Z][a-z][A-Za-z' -]*$")]
    private static partial Regex KeywordShape();

    /// <summary>Woorden voor de ongebrackete vergelijking; een koppelteken
    /// hoort bij het woord ("Quick-Draw" is één term).</summary>
    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex WordToken();

    private static string[] Words(string text) => [.. WordToken().Matches(text).Select(m => m.Value)];

    /// <summary>Komt <paramref name="phrase"/> aaneengesloten in
    /// <paramref name="words"/> voor? Woord-voor-woord, zodat een keyword met
    /// spatie ("Death Knell") werkt en een langer woord ("Leveling") geen
    /// voorkoming van een korter keyword ("Level") is.</summary>
    private static bool ContainsPhrase(string[] words, string[] phrase)
    {
        if (phrase.Length == 0) return false;
        for (var i = 0; i + phrase.Length <= words.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < phrase.Length && hit; j++)
                hit = words[i + j].Equals(phrase[j], StringComparison.OrdinalIgnoreCase);
            if (hit) return true;
        }
        return false;
    }

    private static string Join(string[] values) => values.Length == 0 ? "(geen)" : string.Join(", ", values);

    private static string[] Strings(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return [.. arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .Distinct()];
    }
}
