namespace RbRules.Domain;

/// <summary>Hoe sterk één bewijs-eenheid (kaarttekst of regelsectie) een rol
/// verankert (#226 §3.4, verscherpt in #249).</summary>
public enum EvidenceAnchor
{
    /// <summary>De rol komt in deze bewijs-eenheid niet voor.</summary>
    None,

    /// <summary>Verankerd doordat de bewijs-eenheid de rol ZÉLF is: een kaart draagt
    /// het bewijs voor haar eigen card-rol in haar eigen tekst (de kaartnaam hoeft er
    /// niet in te staan). Waar, maar inhoudsloos — een identiteits-anker zegt niets
    /// over een RELATIE.</summary>
    Identity,

    /// <summary>Het rol-label staat letterlijk in de tekst van deze bewijs-eenheid —
    /// het enige anker dat bewijs voor een relatie kán zijn.</summary>
    Textual,
}

/// <summary>De lexicale bewijsregel voor de promotie-poort (#249). Vóór deze
/// verscherping volstond "beide rollen verankerd in één bewijs-eenheid"; daardoor
/// promoveerde kaart↔eigen-keyword altijd (de kaart ís de ene rol, haar keyword
/// staat gebracket in haar eigen tekst) en werd 69% van de interactie-tabel gevuld
/// met een feit dat al deterministisch uit <c>Card.Mechanics[]</c> in de graph
/// staat, terwijl het echte doel — mech↔mech en gekwalificeerde interacties —
/// verdrongen werd.</summary>
public static class InteractionEvidence
{
    /// <summary>Drukt deze bewijs-eenheid een RELATIE tussen de twee rollen uit?
    /// Beide rollen moeten verankerd zijn ÉN minstens één van beide textueel — twee
    /// identiteits-ankers zijn per definitie geen relatie-bewijs.</summary>
    public static bool ExpressesRelation(EvidenceAnchor from, EvidenceAnchor to) =>
        from != EvidenceAnchor.None && to != EvidenceAnchor.None
        && (from == EvidenceAnchor.Textual || to == EvidenceAnchor.Textual);
}

/// <summary>De tautologie-poort (#249): kaart↔eigen-keyword hoort GEEN
/// <see cref="Interaction"/> te worden.
///
/// Belangrijk: die kennis is niet waardeloos — ze is de ruggengraat van de
/// graph-verkenner. Maar ze bestaat al gratis en deterministisch:
/// <c>GraphSyncService</c> projecteert <c>Card.Mechanics[]</c> als kaart→mechanic-
/// edges (HAS_MECHANIC), en de keywords staan letterlijk gebracket in de kaarttekst.
/// De LLM-mining leidde hetzelfde feit nóg een keer af, betaalde daar tokens voor,
/// en verdrong de gekwalificeerde interacties waar de tabel voor bedoeld is.
///
/// Deze poort raakt de deterministische graph-projectie NIET aan — alleen het
/// LLM-mining-pad.</summary>
public static class InteractionTautology
{
    /// <summary>Is dit rollenpaar een kaart met haar EIGEN keyword? Richting-
    /// onafhankelijk: zowel (card:X → mechanic:K) als (mechanic:K → card:X) telt,
    /// want de tautologie zit in het paar, niet in de agent/patient-rolverdeling.
    /// <paramref name="ownKeywordRefsByCardRef"/> mapt een kaart-ref op de refs van
    /// haar eigen mechanics.</summary>
    public static bool IsCardOwnKeywordPair(
        string? fromRef, string? toRef,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownKeywordRefsByCardRef)
    {
        ArgumentNullException.ThrowIfNull(ownKeywordRefsByCardRef);
        return Owns(fromRef, toRef, ownKeywordRefsByCardRef)
            || Owns(toRef, fromRef, ownKeywordRefsByCardRef);
    }

    private static bool Owns(
        string? cardRef, string? keywordRef,
        IReadOnlyDictionary<string, IReadOnlySet<string>> map) =>
        cardRef is not null && keywordRef is not null
        && map.TryGetValue(cardRef, out var own) && own.Contains(keywordRef);
}
