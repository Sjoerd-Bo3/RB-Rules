using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eén uit een community-document gedestilleerde bewering (#50),
/// nog vóór dedupe/corroboratie.</summary>
public record ExtractedClaim(string TopicType, string TopicRef, string Statement, string? Quote);

/// <summary>Uitkomst van de "zelfde bewering?"-toets: same/contradicts wijzen
/// (1-based) naar een kandidaat; different betekent een nieuwe claim.</summary>
public record ClaimJudgement(string Verdict, int? Match);

/// <summary>Uitkomst van de toets tegen officiële §'s (officieel wint altijd).</summary>
public record OfficialVerdict(string Verdict, string? Reason);

/// <summary>Prompts + parsers voor de claims-pipeline (kennislaag 2, #50).
/// Puur en getest; de LLM-calls lopen via rb-ai (cheap-model — de pipeline is
/// nachtelijk en batched). Zelfde patroon als QueryRewriter/SourceScout:
/// uitval of onzin-output ⇒ null en de aanroeper degradeert netjes.</summary>
public static partial class ClaimMiner
{
    /// <summary>Cap per extractie-call — liever een handvol sterke claims dan
    /// een ongefilterde lijst die de reviewqueue verstopt.</summary>
    public const int MaxClaims = 25;
    public const int MaxStatementLength = 400;
    public const int MaxTopicRefLength = 120;
    /// <summary>Auteursrecht (docs/KNOWLEDGE.md): korte citaten, geen
    /// overgenomen teksten.</summary>
    public const int MaxQuoteLength = 200;

    private static readonly HashSet<string> TopicTypes =
        ["card", "mechanic", "section", "concept"];

    // #187: afgeleide/gesynthetiseerde kennis wordt in de brontaal (Engels)
    // opgeslagen, dicht bij de officiële bewoording — geen vertaalstap, dus
    // geen vertaalverlies en consistente semantiek met de Engelse kaart-/
    // regelbronnen zelf (docs/CONVENTIONS.md). UI en /ask-antwoorden blijven
    // Nederlands (AskService.BasePrompt regelt dat apart).
    public const string ExtractionSystemPrompt = """
        You are the claims extractor for a knowledge base about Riftbound,
        Riot Games' League of Legends trading card game. You receive text
        from a community source. Distill claims from it: statements about
        how rules, card interactions, mechanics, or conventions work in
        practice. Respond ONLY with JSON:
        {"claims": [{"topicType": "...", "topicRef": "...", "statement": "...", "quote": "..."}]}
        - topicType ∈ card | mechanic | section | concept
        - topicRef: the subject — the card name, mechanic name, the §
          number, or a short concept (e.g. "mulligan")
        - statement: the claim, PARAPHRASED in English, close to the
          official wording, 1-2 sentences, readable on its own
        - quote: a short literal quote from the source text as evidence
          (at most ~25 words)
        - Only statements about rules/interactions/conventions; no
          opinions, no marketing copy, no bare card statistics
        - At most 25 claims; prefer 8 strong ones over 25 weak ones
        - Nothing usable? Reply {"claims": []}
        No text outside the JSON.
        """;

    public static string BuildExtractionPrompt(string sourceName, string documentText) =>
        $"Bron: {sourceName}\n\nBrontekst:\n{documentText}";

    /// <summary>Tolerante JSON-extractie uit een LLM-antwoord. null bij
    /// mislukking (geen bruikbare JSON); een lege lijst betekent "geparsed,
    /// maar geen claims gevonden". Items zonder statement of topicRef vallen
    /// weg; een onbekend topicType degradeert naar "concept"; duplicaten
    /// binnen het antwoord (zelfde genormaliseerde bewering) vallen weg.</summary>
    public static IReadOnlyList<ExtractedClaim>? ParseClaims(string raw)
    {
        var items = LlmJson.ExtractItems(raw, "claims");
        if (items is null) return null;

        var seen = new HashSet<string>();
        var result = new List<ExtractedClaim>();
        foreach (var item in items)
        {
            if (result.Count >= MaxClaims) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var statement = Truncate(GetString(item, "statement"), MaxStatementLength);
            var topicRef = Truncate(GetString(item, "topicRef"), MaxTopicRefLength);
            if (string.IsNullOrEmpty(statement) || string.IsNullOrEmpty(topicRef)) continue;
            if (!seen.Add(NormalizeStatement(statement))) continue;

            var topicType = GetString(item, "topicType")?.ToLowerInvariant();
            result.Add(new ExtractedClaim(
                topicType is not null && TopicTypes.Contains(topicType) ? topicType : "concept",
                topicRef,
                statement,
                Truncate(GetString(item, "quote"), MaxQuoteLength)));
        }
        return result;
    }

    /// <summary>Vergelijkingsvorm voor idempotente dedupe: kleine letters,
    /// samengevouwen whitespace, zonder afsluitende leestekens. Bewust géén
    /// agressievere normalisatie — semantische duplicaten vangt de
    /// embedding-clustering + LLM-toets.</summary>
    public static string NormalizeStatement(string statement) =>
        WhitespaceRegex().Replace(statement, " ").Trim().TrimEnd('.', '!', '?', ' ')
            .ToLowerInvariant();

    internal static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim())
            : null;

    internal static string? Truncate(string? s, int max) =>
        s?.Length > max ? s[..max] : s;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>"Zelfde bewering?"-toets (issue #50, stap clustering): één cheap
/// LLM-call beslist of een nieuwe claim een bestaande corroboreert (same),
/// tegenspreekt (contradicts → Conflict-model) of nieuw is (different).</summary>
public static class ClaimJudge
{
    // #187: afgeleide-kennis-redenering in de brontaal (Engels) — het verdict
    // is een enum, maar de prompt hoort bij dezelfde claims-pijplijn die
    // voortaan Engels extraheert; consistent Engels houdt de mining eentalig.
    public const string SystemPrompt = """
        You compare a NEW claim about Riftbound TCG with existing, numbered
        claims. Respond ONLY with JSON:
        {"verdict": "same" | "contradicts" | "different", "match": N}
        - "same": the new claim says substantively the same thing as
          existing claim N (a paraphrase or partial repeat counts as the same)
        - "contradicts": the new claim is about the same subject as claim N
          but asserts the opposite
        - "different": the new claim stands apart from all existing claims
          (then omit match)
        When in doubt, choose "different".
        No text outside the JSON.
        """;

    public static string BuildPrompt(string statement, IReadOnlyList<string> candidates) =>
        $"Nieuwe bewering:\n{statement}\n\nBestaande beweringen:\n"
        + string.Join('\n', candidates.Select((c, i) => $"{i + 1}. {c}"));

    /// <summary>null bij onbruikbare output (aanroeper behandelt de claim dan
    /// als nieuw — de veilige kant: dedupe herstelt zich bij een latere run).</summary>
    public static ClaimJudgement? Parse(string raw, int candidateCount)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (Map(doc.RootElement, candidateCount) is { } judgement) return judgement;
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    private static ClaimJudgement? Map(JsonElement root, int candidateCount)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var verdict = ClaimMiner.GetString(root, "verdict")?.ToLowerInvariant();
        if (verdict is not ("same" or "contradicts" or "different")) return null;
        if (verdict == "different") return new(verdict, null);

        // same/contradicts vereisen een geldige kandidaat-verwijzing.
        if (!root.TryGetProperty("match", out var m)
            || m.ValueKind != JsonValueKind.Number
            || !m.TryGetInt32(out var match)
            || match < 1 || match > candidateCount) return null;
        return new(verdict, match);
    }
}

/// <summary>Toets van een claim tegen officiële regelsecties (laag 0 wint
/// altijd): bevestigd, tegengesproken (→ automatisch rejected/superseded met
/// verwijzing) of geen uitsluitsel.</summary>
public static class OfficialCheck
{
    public const int MaxReasonLength = 300;

    // #187: de reason-tekst wordt als weerlegging/misvatting in /ask gebruikt
    // (#125, negatieve kennis) — dus afgeleide kennis, en die slaan we in de
    // brontaal (Engels) op, dicht bij de officiële bewoording. De UI/‌ask-
    // antwoorden zelf blijven Nederlands (AskService.BasePrompt).
    public const string SystemPrompt = """
        You test a community claim about Riftbound TCG against official rule
        sections. Respond ONLY with JSON:
        {"verdict": "confirmed" | "contradicted" | "unclear", "reason": "..."}
        - "confirmed": the sections confirm the claim
        - "contradicted": the sections demonstrably contradict the claim
        - "unclear": the sections give no verdict — choose this when in doubt
        - reason: 1 sentence (English) with the relevant § reference
        No text outside the JSON.
        """;

    public static string BuildPrompt(
        string statement, IEnumerable<(string Section, string Text)> sections) =>
        $"Bewering:\n{statement}\n\nOfficiële regelsecties:\n"
        + string.Join("\n\n", sections.Select(s => $"§{s.Section}: {s.Text}"));

    /// <summary>null bij onbruikbare output — de claim blijft dan "unchecked"
    /// en komt bij een volgende run opnieuw aan de beurt.</summary>
    public static OfficialVerdict? Parse(string raw)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (Map(doc.RootElement) is { } verdict) return verdict;
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    private static OfficialVerdict? Map(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var verdict = ClaimMiner.GetString(root, "verdict")?.ToLowerInvariant();
        if (verdict is not ("confirmed" or "contradicted" or "unclear")) return null;
        return new(verdict,
            ClaimMiner.Truncate(ClaimMiner.GetString(root, "reason"), MaxReasonLength));
    }
}

/// <summary>Corroboratie-regels (issue #50 / docs/KNOWLEDGE.md): één bron =
/// ongecorroboreerd; elke extra onafhankelijke bron versterkt de claim met
/// afnemende meerwaarde. Puur en getest.</summary>
public static class ClaimScoring
{
    /// <summary>Bijdrage per bron op basis van de register-trust-tier
    /// (1 = officieel … 4 = laag): hoe betrouwbaarder de bron, hoe zwaarder
    /// één vermelding weegt. Community (tier 3) start op 0.5 — pas
    /// corroboratie tilt zo'n claim richting betrouwbaar.</summary>
    public static double TierWeight(short trustTier) => trustTier switch
    {
        <= 1 => 0.95,
        2 => 0.75,
        3 => 0.5,
        _ => 0.3,
    };

    /// <summary>Gewogen bron-trust × corroboratie als 1 − Π(1 − w): elke
    /// onafhankelijke bron verkleint de resterende onzekerheid. Twee
    /// community-bronnen (0.5) geven zo 0.75, vier geven ~0.94 — het
    /// "[community, 4 bronnen, trust 0.9]"-label uit docs/KNOWLEDGE.md.</summary>
    public static double TrustScore(IEnumerable<short> distinctSourceTiers)
    {
        var remaining = 1.0;
        var any = false;
        foreach (var tier in distinctSourceTiers)
        {
            any = true;
            remaining *= 1.0 - TierWeight(tier);
        }
        return any ? Math.Round(1.0 - remaining, 2) : 0.0;
    }

    /// <summary>Corroboratie = het aantal ONAFHANKELIJKE bronnen: dezelfde
    /// bron die iets twee keer zegt telt één keer.</summary>
    public static int Corroboration(IEnumerable<string> sourceIds) =>
        sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
