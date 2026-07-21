using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>De SOORT van één bewijs-eenheid (#324): waar komt de tekst vandaan?
/// Bepaalt sinds #324 welke claim-niveaus ze kan dragen — zie
/// <see cref="InteractionEvidence.CarriesClaimLevel"/>.</summary>
public enum EvidenceSourceKind
{
    /// <summary>Een kaarttekst — óók wanneer die kaart geen aangeboden rol is
    /// (de carrier-kaarten van de mechanic-pass). Een kaart bewijst hooguit iets
    /// over ZICHZELF: haar tekst draagt card↔X, nooit mechanic↔mechanic.</summary>
    CardText,

    /// <summary>Officiële regel-/definitietekst (trust-tier-1): een
    /// RuleSection-chunk of de officiële keyword-definitie
    /// (<c>CanonicalEntity.Definition</c>, #291). Het enige bewijsniveau dat een
    /// mechanic↔mechanic-claim kan dragen.</summary>
    RuleText,
}

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
/// verdrongen werd.
///
/// <b>Verscherpt in #324 — het bewijs moet het NIVEAU van de claim dragen.</b> De
/// eerste steekproef-audit (#255, opus, 9/10 afgekeurd) legde een ontwerpfout
/// bloot die geen sterker model repareert: <c>mechanic:Stun -[GRANTS]->
/// mechanic:Ready</c> promoveerde op het kaart-specifieke effect van Eclipse
/// Herald — beide termen stonden in díe kaarttekst, dus de anker-toets slaagde.
/// Maar een kaart-specifiek effect is geen eigenschap van de mechaniek: een kaart
/// bewijst hooguit iets over zichzelf. Daarom eist <see cref="ExpressesRelation"/>
/// nu naast de ankers óók dat de bewijsSOORT het claim-niveau draagt
/// (<see cref="CarriesClaimLevel"/>): mechanic↔mechanic alleen op regel-/
/// definitietekst (trust-tier-1), card↔X blijft promoveerbaar op kaarttekst — dat
/// ís daar het juiste bewijsniveau. De soort en de rol-typen zijn VERPLICHTE
/// parameters (#300-les: een poort die je kunt omzeilen is geen poort — de
/// typechecker dwingt af dat geen aanroeper de tier-toets kan overslaan).</summary>
public static class InteractionEvidence
{
    /// <summary>Drukt deze bewijs-eenheid een RELATIE tussen de twee rollen uit die
    /// ze ook mag DRAGEN? Drie eisen: de bewijssoort draagt het claim-niveau
    /// (<see cref="CarriesClaimLevel"/>, #324), beide rollen zijn verankerd, én
    /// minstens één van beide textueel — twee identiteits-ankers zijn per definitie
    /// geen relatie-bewijs (#249).</summary>
    public static bool ExpressesRelation(
        EvidenceAnchor from, EvidenceAnchor to,
        EvidenceSourceKind source, EntityType agentType, EntityType patientType) =>
        CarriesClaimLevel(source, agentType, patientType)
        && from != EvidenceAnchor.None && to != EvidenceAnchor.None
        && (from == EvidenceAnchor.Textual || to == EvidenceAnchor.Textual);

    /// <summary>Mag bewijs van deze soort een claim tussen deze twee rol-typen
    /// dragen (#324)? Regel-/definitietekst draagt alles; kaarttekst draagt alleen
    /// claims waar een KAART bij betrokken is (card↔card, card↔mechanic). Een claim
    /// zonder kaart-rol (mechanic↔mechanic) gaat over de mechanieken in het
    /// algemeen, en dáárover bewijst een individuele kaarttekst niets.</summary>
    public static bool CarriesClaimLevel(
        EvidenceSourceKind source, EntityType agentType, EntityType patientType) =>
        source == EvidenceSourceKind.RuleText
        || OntologySchema.IsA(agentType, EntityType.Card)
        || OntologySchema.IsA(patientType, EntityType.Card);
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
