using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Ontlede riftbound-id (#144/#145). Vormen in de bronnen:
/// basis "ogn-074-298", alt-art "ogn-119a-298", signature "sfd-239-star-221"
/// (Riot) of "sfd-239*-221" (riftcodex), specials zonder settotaal
/// ("unl-t04", "ven-r01") of met eigen subtotaal ("ven-sp3-006").</summary>
/// <param name="SetCode">Setcode in hoofdletters ("OGN") — sluit aan op CardSet.SetId.</param>
/// <param name="Number">Collector-nummer; bij specials het nummer binnen de reeks.</param>
/// <param name="Suffix">Printing-suffix: "" (basis), "a" (alt-art), "star" (signature);
/// bij specials het reeks-voorvoegsel ("t", "r", "sp").</param>
/// <param name="SetTotal">Settotaal uit het id ("298"); null bij tokens/runes.</param>
/// <param name="IsSpecial">Token/rune/special-reeks — telt nooit als basisnummer.</param>
public readonly record struct RiftboundIdParts(
    string SetCode, int Number, string Suffix, int? SetTotal, bool IsSpecial);

/// <summary>Parsen en normaliseren van riftbound-id's (#144/#145). Puur —
/// unit-getest op de echte id-vormen uit de Riot-gallery en riftcodex.</summary>
public static partial class RiftboundIds
{
    // riftcodex codeert signature-printings als "sfd-239*-221"; Riot als
    // "sfd-239-star-221". Alleen déze rewrite — verder blijft het id
    // byte-voor-byte intact (nulpadding is bronwaarheid, niet reconstrueren).
    [GeneratedRegex(@"^([a-z]+-\d+)\*(-\d+)$")]
    private static partial Regex StarForm();

    // set-NNN[a]-TTT en set-NNN-star-TTT (de reguliere kaartvormen).
    [GeneratedRegex(@"^(?<set>[a-z]+)-(?<num>\d+)(?<suffix>[a-z])?(?:-(?<star>star))?-(?<total>\d+)$")]
    private static partial Regex CardForm();

    // Specials: tokens "unl-t04", runes "ven-r01" (zonder totaal) en
    // subreeksen "ven-sp3-006" (met eigen subtotaal).
    [GeneratedRegex(@"^(?<set>[a-z]+)-(?<series>[a-z]{1,2})(?<num>\d+)(?:-(?<total>\d+))?$")]
    private static partial Regex SpecialForm();

    /// <summary>Canonieke id-vorm: de riftcodex-ster ("239*") wordt de
    /// suffixvorm die de Riot-route gebruikt ("239-star"); al het andere
    /// blijft onaangeraakt — onbekende vormen zijn data, geen gok.</summary>
    public static string Normalize(string riftboundId)
    {
        var m = StarForm().Match(riftboundId);
        return m.Success ? $"{m.Groups[1].Value}-star{m.Groups[2].Value}" : riftboundId;
    }

    /// <summary>Ontleedt een riftbound-id (na ster-normalisatie). Onbekende
    /// vormen geven false — de aanroeper beslist wat "onparseerbaar" betekent.</summary>
    public static bool TryParse(string riftboundId, out RiftboundIdParts parts)
    {
        parts = default;
        var id = Normalize(riftboundId.Trim().ToLowerInvariant());

        var card = CardForm().Match(id);
        if (card.Success)
        {
            parts = new(
                card.Groups["set"].Value.ToUpperInvariant(),
                int.Parse(card.Groups["num"].Value),
                card.Groups["star"].Success ? "star" : card.Groups["suffix"].Value,
                int.Parse(card.Groups["total"].Value),
                IsSpecial: false);
            return true;
        }

        var special = SpecialForm().Match(id);
        if (special.Success)
        {
            parts = new(
                special.Groups["set"].Value.ToUpperInvariant(),
                int.Parse(special.Groups["num"].Value),
                special.Groups["series"].Value,
                special.Groups["total"].Success ? int.Parse(special.Groups["total"].Value) : null,
                IsSpecial: true);
            return true;
        }
        return false;
    }
}
