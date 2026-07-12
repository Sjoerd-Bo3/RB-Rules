using System.Globalization;

namespace RbRules.Domain;

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
    /// opslagformaat) wordt "section"; al het overige is een antwoord-ruling
    /// (de web-feedback slaat scope "answer" op, met up/down als Ref).</summary>
    public static string FromCorrectionScope(string? scope) =>
        scope?.Trim().ToLowerInvariant() switch
        {
            "card" => "card",
            "rule_section" or "section" => "section",
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
