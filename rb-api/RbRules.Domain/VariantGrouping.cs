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

/// <summary>Leespad-projectie van de GEREÏFICEERDE interactielaag (#226) naar de
/// kaart-buur-vorm die het kaartdetail toont (#258). Puur — unit-testbaar zonder
/// EF, net als <see cref="VariantGrouping.InteractionNeighbors"/> voor de oude
/// laag.
///
/// Twee dingen die de reïficatie wél heeft en de oude tabel niet, en die hier dus
/// niet mogen wegvallen: de ROL (agent/patient — "A countert B" is iets anders dan
/// "B countert A", de oude tabel was richtingsloos) en de CONDITIES (window/status/
/// cost als losse, weerlegbare knopen i.p.v. platgeslagen proza). Beide gaan de
/// weergavetekst in; de <see cref="Interaction.Kind"/> wordt kleingeschreven zodat
/// hij als chip-label naast de oude kinds (combo/synergy/counter/nonbo) leesbaar
/// blijft.</summary>
public static class ReifiedInteractionDisplay
{
    /// <summary>Het BrainRef-prefix van een kaart. Publiek omdat het leespad hem
    /// in zijn EF-query nodig heeft: het kaart↔kaart-filter moet server-side
    /// staan, vóór de Take (#287-review) — <see cref="IsCardRef"/> is de
    /// in-memory tegenhanger voor wat de query al heeft opgehaald.</summary>
    public const string CardRefPrefix = "card:";

    /// <summary>De statussen die het publieke leespad mag tonen: alleen wat de
    /// promotie-poort heeft goedgekeurd. <c>candidate</c> en
    /// <c>model_hypothesized_unruled</c> zijn expliciet NIET tonbaar — daar bestaat
    /// die poort voor; een ongetoetste modelhypothese als "geverifieerde interactie"
    /// tonen zou precies de autoriteit claimen die #226 wil vermijden.</summary>
    public static readonly IReadOnlyList<string> DisplayableStatuses =
        [InteractionStatus.Promoted, InteractionStatus.Verified];

    /// <summary>Is dit een kaart-ref ("card:ogn-011-298")? Alleen kaart↔kaart-
    /// interacties passen in de buur-vorm: het kaartdetail linkt de buur door naar
    /// /cards/{id}. Een card↔keyword-interactie (bv. "Deflect MODIFIES deze kaart")
    /// is geen buur maar een eigenschap — die hoort in het kaartdossier, niet hier,
    /// en wordt overgeslagen in plaats van naar een niet-bestaande kaartpagina te
    /// wijzen.</summary>
    public static bool IsCardRef(string? brainRef) =>
        brainRef is not null && brainRef.StartsWith(CardRefPrefix, StringComparison.Ordinal);

    public static string CardIdOf(string brainRef) => brainRef[CardRefPrefix.Length..];

    /// <summary>Interactie-buren uit de gereïficeerde laag, met dezelfde
    /// canonicalisatie-afspraken als de oude laag (varianten → canoniek id, geen
    /// zelf-paren, geen duplicaten, onbekende ids blijven zichtbaar).</summary>
    public static List<InteractionNeighbor> Neighbors(
        IEnumerable<Interaction> rows,
        IReadOnlySet<string> groupIds,
        IReadOnlyDictionary<string, Card> cardsById)
    {
        var result = new List<InteractionNeighbor>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            if (!IsCardRef(row.AgentRef) || !IsCardRef(row.PatientRef)) continue;
            var agentId = CardIdOf(row.AgentRef);
            var patientId = CardIdOf(row.PatientRef);

            // Welke kant is "deze kaart"? De andere kant is de buur.
            var thisIsAgent = groupIds.Contains(agentId);
            if (!thisIsAgent && !groupIds.Contains(patientId)) continue;
            var rawOther = thisIsAgent ? patientId : agentId;

            var other = cardsById.GetValueOrDefault(rawOther);
            var otherId = other is null ? rawOther : CardText.CanonicalId(other);
            if (groupIds.Contains(otherId)) continue; // zelf-paar binnen de variantgroep
            if (!seen.Add(otherId)) continue;

            var otherName = other is null ? rawOther : CardText.BaseName(other.Name);
            result.Add(new(
                otherId,
                otherName,
                row.Kind.ToLowerInvariant(),
                Describe(row, otherName, thisIsAgent)));
        }
        return result;
    }

    /// <summary>De weergavetekst: rol + gekwalificeerde condities + eventueel de
    /// normatieve verankering. Geen LLM-proza (dat heeft de reïficatie bewust niet
    /// meer) maar een letterlijke lezing van wat er is vastgelegd.</summary>
    public static string Describe(Interaction row, string otherName, bool thisIsAgent)
    {
        var kind = row.Kind.ToLowerInvariant();
        var parts = new List<string>
        {
            thisIsAgent
                ? $"Deze kaart {kind} {otherName}."
                : $"{otherName} {kind} deze kaart.",
        };

        if (row.Conditions.Count > 0)
        {
            var conditions = row.Conditions
                .OrderBy(c => c.OnKind, StringComparer.Ordinal)
                .ThenBy(c => c.Value, StringComparer.Ordinal)
                .Select(c =>
                {
                    var op = string.IsNullOrWhiteSpace(c.Operator) ? "" : $" {c.Operator}";
                    var role = c.SubjectRole is null ? "" : $" ({c.SubjectRole})";
                    return $"{c.OnKind}{op} {c.Value}{role}";
                });
            parts.Add($"Voorwaarden: {string.Join("; ", conditions)}.");
        }

        if (row.GovernedByRef is { Length: > 0 } governed)
            parts.Add($"Verankerd in {governed}.");

        // Alleen bij de lagere tier vermelden: "promoted" is de norm en hoeft
        // geen slag om de arm, "verified" wacht nog op de promotiepoort.
        if (row.Status == InteractionStatus.Verified)
            parts.Add("Geverifieerd, nog niet gepromoveerd.");

        return string.Join(" ", parts);
    }
}
