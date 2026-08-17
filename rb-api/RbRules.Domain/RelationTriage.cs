using System.Text;
using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Uitkomst van één triage-oordeel (#199 v1). <see cref="Judged"/>
/// draagt het oordeel; <see cref="Unusable"/> is de degradatie-tak (AI-uitval
/// of onparseerbare/onbekende output) — de aanroeper slaat het voorstel dan
/// gewoon over: transiënt, geen marker, de volgende run probeert opnieuw
/// (zelfde discipline als <see cref="ClarificationAnchorRepair"/>).</summary>
public enum RelationTriageOutcome { Judged, Unusable }

/// <summary>Geparseerd triage-oordeel. Bij <see
/// cref="RelationTriageOutcome.Judged"/> zijn Recommendation/Reason gezet
/// ("accept"|"reject"|"unsure", genormaliseerd kleine letters) en Refs de
/// (mogelijk lege) lijst geraadpleegde §/mechaniek/concept-refs — die worden
/// NIET los opgeslagen (het datamodel blijft bewust tot drie nullable
/// kolommen op <see cref="Relation"/>) maar in de motivering gevouwen door de
/// aanroeper.</summary>
public sealed record RelationTriageJudgement(
    RelationTriageOutcome Outcome, string? Recommendation = null,
    string? Reason = null, IReadOnlyList<string>? Refs = null);

/// <summary>LLM-triage voor relatievoorstellen (issue #199 v1): een
/// AANBEVELINGS-machine, geen autoriteitspad — dit is bewust NIET de
/// optionele auto-accept uit de issue (die vereist een multi-judge-consensus
/// én een deterministisch vangnet, precies de #188-les dat een LLM-oordeel
/// alléén nooit een statuswijziging mag dragen). De machine sorteert voor
/// (aanbeveling + motivering), de mens klikt (los of via de bulk-actie op een
/// hele aanbevelingsgroep) — dat pakt vrijwel alle tijdswinst zonder een
/// nieuw, riskant autoriteitspad.
///
/// Prompt in het Engels (#187: eentalige mining, afgeleide kennis in de
/// brontaal). Het antwoord is één JSON-OBJECT (geen array zoals de
/// claims-/relatie-extractie), dus de parser gebruikt <see
/// cref="LlmJson.Candidates"/> rechtstreeks in plaats van <see
/// cref="LlmJson.ExtractItems"/> — met dezelfde objectvorm-guard als <see
/// cref="ClarificationAnchorRepair.ParseAnchorChoice"/>: <see
/// cref="LlmJson.Candidates"/> levert ook array-vormige blokken op ("[1]",
/// "[true]") uit toevallige bronvermeldingen in het antwoord, en
/// TryGetProperty (via <see cref="ClaimMiner.GetString"/>/GetBool) gooit op
/// een niet-object root een InvalidOperationException — geen JsonException,
/// dus de catch daaronder vangt 'm niet. Zonder de guard zou de triage-run
/// 500'en in plaats van netjes te degraderen.</summary>
public static class RelationTriage
{
    public const int MaxReasonLength = 300;
    public const int MaxRefLength = 40;
    public const int MaxRefs = 8;

    private static readonly HashSet<string> ValidRecommendations =
        new(StringComparer.OrdinalIgnoreCase) { "accept", "reject", "unsure" };

    public const string SystemPrompt = """
        You triage one proposed relation for a knowledge base about
        Riftbound, Riot Games' League of Legends trading card game. You get
        the proposed relation (from, to, kind, explanation) plus retrieved
        rules context for it. Judge whether the relation is correct and
        worth keeping, grounded ONLY in the given context — never on
        outside knowledge. Respond ONLY with JSON:
        {"recommendation": "accept"|"reject"|"unsure", "reason": "...", "refs": ["402.3", ...]}
        - recommendation: "accept" if the context supports the relation as
          stated, "reject" if the context contradicts it or it is not a
          meaningful/useful relation, "unsure" if the context is
          inconclusive — when in doubt, answer "unsure" rather than
          guessing
        - reason: one sentence in English, grounded in the given context
        - refs: the section numbers / mechanic / concept names from the
          given context that your judgement actually relies on (empty
          array if none apply)
        No text outside the JSON.
        """;

    public static string BuildPrompt(
        string fromRef, string toRef, string kind, string explanation,
        IReadOnlyList<string> contextLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Proposed relation: {fromRef} --[{kind}]--> {toRef}");
        sb.AppendLine($"Explanation given by the miner: {explanation}");
        sb.AppendLine();
        sb.AppendLine("Retrieved rules context:");
        if (contextLines.Count == 0) sb.AppendLine("(none found)");
        else foreach (var line in contextLines) sb.AppendLine(line);
        return sb.ToString();
    }

    public static RelationTriageJudgement Parse(string raw)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue; // geen geldige JSON op deze positie — volgende kandidaat
            }

            using (doc)
            {
                // Objectvorm-guard (#188 increment 3-les, zie summary hierboven).
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                var recommendation = ClaimMiner.GetString(doc.RootElement, "recommendation")
                    ?.ToLowerInvariant();
                if (recommendation is null || !ValidRecommendations.Contains(recommendation))
                    continue; // onbekende/ontbrekende waarde — volgende kandidaat, anders Unusable

                var reason = ClaimMiner.Truncate(
                    ClaimMiner.GetString(doc.RootElement, "reason"), MaxReasonLength);
                if (reason is null) continue;

                var refs = doc.RootElement.TryGetProperty("refs", out var refsEl)
                        && refsEl.ValueKind == JsonValueKind.Array
                    ? refsEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => ClaimMiner.Truncate(e.GetString()!.Trim(), MaxRefLength) ?? "")
                        .Where(s => s.Length > 0)
                        .Take(MaxRefs)
                        .ToList()
                    : [];

                return new(RelationTriageOutcome.Judged, recommendation, reason, refs);
            }
        }
        return new(RelationTriageOutcome.Unusable);
    }
}
