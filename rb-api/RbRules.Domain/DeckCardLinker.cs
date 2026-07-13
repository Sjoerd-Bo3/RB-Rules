using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Koppelt Piltover Archive-variantnummers ("OGN-126a") aan onze
/// kaarten (#15). Ons riftbound_id is "{set}-{collector}-{setgrootte}"
/// ("ogn-126a-298"); het PA-nummer is precies het stuk vóór de setgrootte.
/// Het resultaat is altijd het canonieke kaart-id (VariantGrouping/#54:
/// variant → VariantOf), zodat decks aan dezelfde knoop hangen als
/// interacties, embeddings en de graph. Onbekend blijft null — fouten zijn
/// data, de aanroeper telt ze. Puur; opgebouwd uit een kaart-snapshot.</summary>
public partial class DeckCardLinker
{
    private readonly Dictionary<string, Card> _byCode;
    private readonly Dictionary<string, string> _canonicalByBaseName;

    public DeckCardLinker(IEnumerable<Card> cards)
    {
        // Bij een (theoretische) code-botsing wint de basisprinting —
        // dezelfde rangorde als de variantgroepering (#57).
        _byCode = cards
            .OrderBy(VariantGrouping.AltPrintingRank)
            .ThenBy(c => c.RiftboundId, StringComparer.Ordinal)
            .GroupBy(c => PrintingCode(c.RiftboundId))
            .ToDictionary(g => g.Key, g => g.First());
        // Naam-vangnet: binnen consistente data wijst elke printing van een
        // basisnaam via CanonicalId naar dezelfde canonieke kaart.
        _canonicalByBaseName = _byCode.Values
            .GroupBy(c => CardText.BaseName(c.Name).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => CardText.CanonicalId(g.First()));
    }

    /// <summary>Printing-code van een riftbound_id: de setgrootte-staart valt
    /// af ("ogn-126a-298" → "ogn-126a", "unl-233-star-219" → "unl-233-star");
    /// ids zonder numerieke staart ("unl-t07") zijn al een code.</summary>
    public static string PrintingCode(string riftboundId)
    {
        var parts = riftboundId.Split('-');
        return parts.Length >= 3 && parts[^1].Length > 0 && parts[^1].All(char.IsAsciiDigit)
            ? string.Join('-', parts[..^1])
            : riftboundId;
    }

    /// <summary>Canoniek riftbound_id voor een PA-kaartverwijzing, of null
    /// als wij de kaart (nog) niet kennen. Volgorde: exact variantnummer →
    /// basisprinting (letter-suffix eraf: een alt-art die wij nog niet in de
    /// gallery hebben resolvet naar zijn basiskaart) → kaartnaam.</summary>
    public string? ResolveCanonical(string? variantNumber, string? cardName)
    {
        if (!string.IsNullOrWhiteSpace(variantNumber))
        {
            var code = variantNumber.Trim().ToLowerInvariant();
            if (_byCode.TryGetValue(code, out var hit))
                return CardText.CanonicalId(hit);
            var baseCode = TrailingLetters().Replace(code, "").TrimEnd('-');
            if (baseCode != code && _byCode.TryGetValue(baseCode, out var baseHit))
                return CardText.CanonicalId(baseHit);
        }
        if (!string.IsNullOrWhiteSpace(cardName)
            && _canonicalByBaseName.TryGetValue(
                CardText.BaseName(cardName).ToLowerInvariant(), out var byName))
            return byName;
        return null;
    }

    [GeneratedRegex("[a-z]+$")]
    private static partial Regex TrailingLetters();
}
