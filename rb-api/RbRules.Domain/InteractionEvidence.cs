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

/// <summary>Poort A (#330) — het kind-anker: per relatiesoort een GESLOTEN
/// catalogus van lexicale ankers waarvan er minstens één in een dragende
/// bewijs-eenheid moet staan vóór promotie. De aanleiding is gemeten: de eerste
/// fable-batch (23 promoties, opus-audit op 9) gaf 7 afkeuringen in ÉÉN
/// faalklasse — het model overclaimt de relatieSOORT op co-occurrence. #324
/// waarborgt dat het bewijs het NIVEAU van de claim draagt (mech↔mech alleen op
/// regeltekst), maar tier-1-co-occurrence zegt nog niet WELKE relatie: "[Reaction]
/// — Add [1]" werd COUNTERS, twee Dependent Keywords naast elkaar (§727.1.b)
/// werden MODIFIES. Deze poort eist dat de bewijstekst de soort ook uitdrukt.
///
/// De catalogus is DATA, gekalibreerd op de zeven betwiste en twee bevestigde
/// paren (meet eerst, #211): de betwiste bewijsteksten bevatten géén anker van
/// hun geclaimde soort, de bevestigde REQUIRES-paren halen hun anker op hun echte
/// sectieteksten ("must have 11 XP", "pay its Equip cost"). Twee bewuste
/// weglatingen bij GRANTS, allebei door de meting afgedwongen: <c>gains</c>
/// ("my controller gains X XP" — §823.1.c.1, precies de Hunt→XP-overclaim) en
/// <c>become/becomes/becoming</c> ("become ready" — §805.6, de Accelerate→Ready-
/// overclaim). Echte grants die met die werkwoorden geformuleerd zijn degraderen
/// dus naar Candidate (reviewqueue) — aanvaard: de poort is een NOODZAKELIJKE
/// voorwaarde, en een gemiste promotie wacht op review terwijl een valse
/// promotie het brein vervuilt. <c>has/have</c> staan er wél in: de gemeten
/// drukconventie voor granting is "Friendly units have [Deflect]" / "give a unit
/// [Ganking]" (live kaartcorpus).
///
/// <b>Wat deze poort NIET garandeert.</b> Ze matcht gedrukte woordvormen op
/// woordgrenzen binnen een dragende bewijs-eenheid — een anker-woord elders in
/// diezelfde tekst, over iets ánders, komt erdoor ("has" in een voorbeeldzin
/// die een kaarttekst citeert). Dat is aanvaard restrisico: noodzakelijk, niet
/// voldoende. De poort verwerpt bovendien niets — stranden = Candidate
/// (reviewqueue), zelfde soft-pad als de #324-bewijstier.</summary>
public static class InteractionKindAnchors
{
    /// <summary>De catalogus: relatiesoort → ankervormen (letterlijke gedrukte
    /// vormen, woordgrens-gematcht via <see cref="TermMatch.ContainsWord"/>;
    /// meerwoords toegestaan). Zowel rechte als typografische apostrofs — de
    /// regel-PDF drukt "can’t".</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Catalog =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [InteractionKinds.Grants] =
                ["grant", "grants", "granted", "give", "gives", "given", "has", "have"],
            [InteractionKinds.Counters] =
                ["counter", "counters", "countered", "can't", "can’t", "cannot",
                 "prevent", "prevents", "prevented", "negate", "negates", "negated",
                 "instead", "lose", "loses", "remove", "removes", "removed",
                 "reduce", "reduces", "reduced", "ignore", "ignores", "ignored",
                 "stop", "stops", "block", "blocks"],
            [InteractionKinds.Modifies] =
                ["modify", "modifies", "modified", "instead", "additional",
                 "as though", "treat", "treats", "treated", "rather than",
                 "replace", "replaces", "replaced"],
            [InteractionKinds.Requires] =
                ["require", "requires", "required", "need", "needs", "needed",
                 "must", "only if", "in order", "as long as",
                 "cost", "costs", "pay", "pays", "paid", "spend", "spends", "spent"],
        };

    /// <summary>Bevat <paramref name="evidenceText"/> minstens één anker van
    /// <paramref name="kind"/>? Onbekende/niet-canonieke soort → false (de
    /// schema-poort weigert die toch al; hier niet gokken).</summary>
    public static bool CarriesKind(string? kind, string? evidenceText) =>
        kind is not null
        && Catalog.TryGetValue(kind, out var anchors)
        && anchors.Any(a => TermMatch.ContainsWord(evidenceText, a));
}

/// <summary>Poort B (#330) — de woordvormpoort voor keyword-doelen: een claim in
/// een toekennende soort (GRANTS) met een MECHANIC als patient eist dat de
/// patient-naam in KEYWORD-vorm in een dragende bewijs-eenheid staat. De gemeten
/// drukconventie (#211: 31 keywords over 1429 kaartteksten, állemaal gebracket)
/// is hier hergebruikt als poort: gebracket (<c>[Recycle]</c>) telt, en een
/// hoofdlettervorm die NIET aan een zinsbegin staat telt ("my controller gains X
/// XP" — XP als gedefinieerde term). Een kleine letter midden in de zin is de
/// werkwoord-/prozavorm en telt niet: "You may recycle it" (Vision/Predict) en
/// "become ready" (Accelerate) waren precies de gemeten overclaims — het
/// werkwoord recycle is geen toegekend keyword [Recycle].
///
/// <b>Wat deze poort NIET garandeert.</b> Riot kapitaliseert ook spelwerkwoorden
/// midden in een zin ("whether or not to Recycle it", §436.1) — die vorm komt
/// erdoor. En een zins-initiële hoofdletter is ambigu en telt dáárom niet mee,
/// ook als het wél een keyword is. Noodzakelijke voorwaarde, geen voldoende;
/// stranden = Candidate, nooit stil weg.</summary>
public static class KeywordWordForm
{
    /// <summary>Geldt de woordvormpoort voor deze claim? Alleen de toekennende
    /// soort (GRANTS) met een mechanic-patient — sinds #304 zijn keyword-rollen
    /// getypeerd als <see cref="EntityType.Mechanic"/>. Voor REQUIRES/COUNTERS/
    /// MODIFIES is de patient-naam in prozavorm een normaal bewijs
    /// ("Deflect prevents Assault damage").</summary>
    public static bool Applies(string? kind, EntityType patientType) =>
        kind == InteractionKinds.Grants
        && OntologySchema.IsA(patientType, EntityType.Mechanic);

    /// <summary>Staat <paramref name="label"/> ergens in <paramref name="text"/>
    /// in keyword-vorm: direct gebracket (<c>[Label</c>, dekt ook magnitudes als
    /// <c>[Assault 2]</c>) of met hoofdletter op een niet-zins-initiële positie?
    /// Woordgrens-bewust (dezelfde grens als <see cref="TermMatch"/>).</summary>
    public static bool AppearsAsKeyword(string? text, string? label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label)) return false;
        var needle = label.Trim();

        var from = 0;
        while (from <= text.Length - needle.Length)
        {
            var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            if (TermMatch.IsBoundary(text, at, needle.Length) && IsKeywordForm(text, at))
                return true;
            from = at + 1;
        }
        return false;
    }

    private static bool IsKeywordForm(string text, int at)
    {
        // Gebracket = de gemeten drukconventie, sterkste signaal — hoofdletter
        // doet er dan niet toe.
        if (at > 0 && text[at - 1] == '[') return true;
        // Kleine letter midden in de zin = werkwoord-/prozavorm.
        if (!char.IsUpper(text[at])) return false;
        // Hoofdletter telt alleen buiten het zinsbegin ("Ready" als eerste woord
        // is ambigu — kan net zo goed een gebiedende wijs zijn).
        return !IsSentenceInitial(text, at);
    }

    private static bool IsSentenceInitial(string text, int at)
    {
        var i = at - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsQuote(text[i]))) i--;
        return i < 0 || text[i] is '.' or '!' or '?';
    }

    private static bool IsQuote(char c) => c is '"' or '\'' or '“' or '”' or '‘' or '’';
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
