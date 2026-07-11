namespace RbRules.Domain;

/// <summary>Variantgroepering (#54/#57): welke printing van een kaartnaam is
/// canoniek, en hoe resolven variant-verwijzingen naar die canonieke kaart.
/// Puur — unit-testbaar zonder infrastructuur.</summary>
public static class VariantGrouping
{
    /// <summary>Kiest de canonieke printing binnen een basisnaam-groep.
    /// Canoniek is gepind (#57): de printing waar bestaande varianten al naar
    /// verwijzen blijft canoniek, ook als een nieuwe set een printing brengt
    /// die volgens de rangorde zou winnen. Zo blijven graph-knopen,
    /// interacties en embedding-ankers op hetzelfde id hangen; een flip
    /// gebeurt alleen als de gepinde printing zelf uit de bron verdwijnt.</summary>
    public static Card ChooseCanonical(IReadOnlyCollection<Card> group)
    {
        var pinned = group
            .Where(c => group.Any(o => o.VariantOf == c.RiftboundId))
            .ToList();
        IReadOnlyCollection<Card> pool = pinned.Count > 0 ? pinned : group;
        return pool
            .OrderBy(c => c.Name == CardText.BaseName(c.Name) ? 0 : 1)
            .ThenBy(AltPrintingRank)
            .ThenBy(c => c.Rarity == "Showcase" ? 1 : 0)
            .ThenBy(c => c.RiftboundId, StringComparer.Ordinal)
            .First();
    }

    /// <summary>'ogn-119-298' = basisprinting (0); 'ogn-119a-298',
    /// 'sfd-227-star-221' en 'ven-sp3-006' zijn alt-varianten (1).</summary>
    public static int AltPrintingRank(Card c)
    {
        var parts = c.RiftboundId.Split('-');
        var numeric = parts.Length >= 2 && parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit)
            && (parts.Length < 3 || (parts[2].Length > 0 && parts[2].All(char.IsAsciiDigit)));
        return numeric ? 0 : 1;
    }

    /// <summary>Interactie-buren canonicaliseren (#57): rijen van vóór de
    /// variantgroepering kunnen nog variant-ids bevatten. Het resultaat wijst
    /// altijd naar canonieke kaarten, zonder duplicaten en zonder paren binnen
    /// de eigen variantgroep. Onbekende ids blijven zichtbaar (fouten zijn
    /// data — een dangling interactie mag niet stil verdwijnen).</summary>
    public static List<InteractionNeighbor> InteractionNeighbors(
        IEnumerable<CardInteraction> rows,
        IReadOnlySet<string> groupIds,
        IReadOnlyDictionary<string, Card> cardsById)
    {
        var result = new List<InteractionNeighbor>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var rawOther = groupIds.Contains(row.CardAId) ? row.CardBId : row.CardAId;
            var other = cardsById.GetValueOrDefault(rawOther);
            var otherId = other is null ? rawOther : CardText.CanonicalId(other);
            if (groupIds.Contains(otherId)) continue; // pre-groepering zelf-paar
            if (!seen.Add(otherId)) continue;
            result.Add(new(
                otherId,
                other is null ? rawOther : CardText.BaseName(other.Name),
                row.Kind,
                row.Explanation));
        }
        return result;
    }
}

/// <summary>Buur uit een geverifieerde interactie, op canoniek kaart-id.</summary>
public record InteractionNeighbor(string OtherId, string OtherName, string Kind, string Explanation);
