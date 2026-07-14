using System.Globalization;

namespace RbRules.Domain;

/// <summary>Correction.Provenance → een korte, stabiele "kind" voor de
/// graph-projectie (#191): de :Ruling-knoop draagt dit als property zodat
/// een tool/UI clarify-gemined en in-chat-rulings uit elkaar kan houden
/// zonder het rauwe Provenance-formaat ("clarify-mining:{sourceId}",
/// "chat-ruling:admin"/"chat-ruling:user") te hoeven kennen. Puur — geen I/O.</summary>
public static class RulingKind
{
    public static string FromProvenance(string? provenance) => provenance switch
    {
        null => "other",
        _ when provenance.StartsWith("clarify-mining:", StringComparison.Ordinal) => "clarify",
        _ when provenance.StartsWith("chat-ruling:", StringComparison.Ordinal) => "chat",
        "review-notitie" => "review-note",
        _ => "other",
    };
}

/// <summary>Correction (geverifieerde ruling) → ABOUT-doel voor de
/// graph-projectie (#191): dezelfde resolutie als Claim (ClaimTopicMapper),
/// via het gedeelde topic-vocabulaire (RulingsTopics) zodat Scope
/// "rule_section" eerst "section" wordt vóór de lookup. Scope "answer"
/// (chat-ruling zonder anker) en de review-notitie-promotiescopes
/// "claim"/"relation" hebben geen aanwijsbaar doel → null, dus geen
/// ABOUT-edge — precies het bestaande Claim-gedrag voor niet-resolvende
/// topics.</summary>
public static class RulingTopicMapper
{
    public static BrainRef? Resolve(ClaimTopicMapper mapper, string? scope, string? reference) =>
        mapper.Resolve(RulingsTopics.FromCorrectionScope(scope), reference);
}

/// <summary>Onderwerp-vocabulaire voor de publieke rulings-databank (#127):
/// geverifieerde rulings (Correction.Scope: card|rule_section|answer) en
/// community-claims (Claim.TopicType: card|mechanic|section|concept) spreken
/// op /rulings één filtertaal, zodat één set filterknoppen beide soorten
/// dekt. Puur — geen I/O.</summary>
public static class RulingsTopics
{
    /// <summary>Het gedeelde filtervocabulaire, in weergavevolgorde.</summary>
    public static readonly string[] All = ["card", "mechanic", "section", "concept", "answer"];

    /// <summary>Correction.Scope → gedeeld topic: "rule_section" (het
    /// opslagformaat) wordt "section"; "mechanic"/"concept" (#177,
    /// ClarificationMiningService) mappen 1-op-1; al het overige (incl. de
    /// "claim"/"relation"-scopes van review-notitie-promoties, #124) is een
    /// antwoord-ruling (de web-feedback slaat scope "answer" op, met up/down
    /// als Ref).</summary>
    public static string FromCorrectionScope(string? scope) =>
        scope?.Trim().ToLowerInvariant() switch
        {
            "card" => "card",
            "rule_section" or "section" => "section",
            "mechanic" => "mechanic",
            "concept" => "concept",
            _ => "answer",
        };

    /// <summary>Claim.TopicType → gedeeld topic. Onbekende types blijven
    /// zichtbaar als "concept" (het generiekste onderwerp) — een item mag
    /// nooit uit de databank verdwijnen doordat zijn type-label afwijkt.</summary>
    public static string FromClaimTopicType(string? topicType) =>
        topicType?.Trim().ToLowerInvariant() switch
        {
            "card" => "card",
            "mechanic" => "mechanic",
            "section" => "section",
            _ => "concept",
        };

    /// <summary>Filterwaarde uit de querystring: leeg/afwezig = geen filter
    /// (topic blijft null); anders moet de waarde in All staan.</summary>
    public static bool TryParseFilter(string? raw, out string? topic, out string fout)
    {
        topic = null;
        fout = "";
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var t = raw.Trim().ToLowerInvariant();
        if (Array.IndexOf(All, t) < 0)
        {
            fout = $"onbekend onderwerp-type '{raw.Trim()}' — kies uit: {string.Join(", ", All)}";
            return false;
        }
        topic = t;
        return true;
    }
}

/// <summary>Trust-label voor claims: corroboratie + score, met status en
/// officiële toets zodra die afwijken — de kennispiramide blijft in élk
/// koppelvlak expliciet (docs/BRAIN.md, leidend principe). Verhuisd uit
/// BrainService toen de rulings-databank (#127) de tweede afnemer werd.</summary>
public static class ClaimTrust
{
    public static string Label(
        int corroboration, double trustScore, string status, string officialStatus)
    {
        var basis = $"community ({corroboration} " +
            $"{(corroboration == 1 ? "bron" : "bronnen")}, " +
            $"trust {trustScore.ToString("0.00", CultureInfo.InvariantCulture)}";
        basis += officialStatus switch
        {
            "confirmed" => ", officieel bevestigd",
            "contradicted" => ", officieel tegengesproken",
            _ => "",
        };
        basis += status switch
        {
            "accepted" => "",
            "unreviewed" => ", status=unreviewed — nog niet gereviewd",
            _ => $", status={status} — weerlegd/vervangen, géén geldige kennis",
        };
        return basis + ")";
    }
}
