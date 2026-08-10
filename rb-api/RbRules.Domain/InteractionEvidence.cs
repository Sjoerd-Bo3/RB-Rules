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

/// <summary>Poort B (#330, verbreed in #335) — de woordvormpoort voor
/// keyword-doelen: een claim in een toekennende of vereisende soort
/// (GRANTS/REQUIRES) met een MECHANIC als patient eist dat de patient-naam in
/// KEYWORD-vorm in een dragende bewijs-eenheid staat. De gemeten drukconventie
/// (#211: 31 keywords over 1429 kaartteksten, állemaal gebracket) is hier
/// hergebruikt als poort: gebracket (<c>[Recycle]</c>) telt, en een
/// hoofdlettervorm die NIET aan een zinsbegin staat telt ("my controller gains X
/// XP" — XP als gedefinieerde term). Een kleine letter midden in de zin is de
/// werkwoord-/prozavorm en telt niet: "You may recycle it" (Vision/Predict) en
/// "become ready" (Accelerate) waren precies de gemeten overclaims — het
/// werkwoord recycle is geen toegekend keyword [Recycle].
///
/// <b>Verscherpt in #335 (klasse B, gemeten op de era-3-audit):</b> voor de
/// WERKWOORD-ACHTIGE keywords in <see cref="VerbLikeKeywords"/> telt alléén de
/// gebrackete vorm. "Ready me" (Hungry Wolf) is de gebiedende wijs midden in een
/// ability-zin en passeerde de hoofdletter-regel — het tot dan gedocumenteerde
/// restrisico, nu gematerialiseerd en gedicht. En <b>verbreed (klasse C1)</b>
/// naar REQUIRES: "may recycle it" strandde als GRANTS en kwam als REQUIRES
/// door de onbewaakte deur terug. MODIFIES/COUNTERS blijven bewust buiten de
/// poort: een MODIFIES-doel drukt legitiem in werkwoordsvorm ("channel 1 rune
/// exhausted" — Siphoning Strike, bevestigde era-3-promotie) en een
/// COUNTERS-doel in proza ("Deflect prevents Assault damage").
///
/// <b>Wat deze poort NIET garandeert.</b> Riot kapitaliseert ook spelwerkwoorden
/// midden in een zin — buiten de verb-like catalogus komt die vorm erdoor. En een
/// zins-initiële hoofdletter is ambigu en telt dáárom niet mee, ook als het wél
/// een keyword is. Noodzakelijke voorwaarde, geen voldoende; stranden =
/// Candidate, nooit stil weg.</summary>
public static class KeywordWordForm
{
    /// <summary>De werkwoord-achtige keywords (klasse B, #335) — catalogus als
    /// DATA, gekalibreerd op de audit-oordelen en de corpus-meting (1429
    /// kaartteksten): Ready 0× gebracket / 28× hoofdletter / 180× kleine letter,
    /// Recycle 0/26/51 — beide gemeten overclaims ("Ready me", "may recycle it" en
    /// §436.1 "whether or not to Recycle it"). Let op bij uitbreiden: 0× gebracket
    /// alléén is GEEN criterium — Channel meet óók 0/10/30 en is een BEVESTIGDE
    /// GRANTS op de hoofdlettervorm (Baccai Witherclaw, "Channel 2 runes
    /// exhausted"), en Disempower (0/25/5) is een bevestigde REQUIRES-kost
    /// ("Disempower this"). Kalibreer op audit-oordelen, niet op de telling.</summary>
    public static readonly IReadOnlySet<string> VerbLikeKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ready", "Recycle" };

    /// <summary>Geldt de woordvormpoort voor deze claim? De toekennende én
    /// vereisende soorten (GRANTS/REQUIRES, #335-C1) met een mechanic-patient —
    /// sinds #304 zijn keyword-rollen getypeerd als <see cref="EntityType.Mechanic"/>.
    /// Voor COUNTERS/MODIFIES is de patient-naam in prozavorm een normaal bewijs
    /// (zie de klasse-samenvatting; Siphoning Strike is de gemeten wachter).</summary>
    public static bool Applies(string? kind, EntityType patientType) =>
        (kind == InteractionKinds.Grants || kind == InteractionKinds.Requires)
        && OntologySchema.IsA(patientType, EntityType.Mechanic);

    /// <summary>Staat <paramref name="label"/> ergens in <paramref name="text"/>
    /// in keyword-vorm: direct gebracket (<c>[Label</c>, dekt ook magnitudes als
    /// <c>[Assault 2]</c>) of met hoofdletter op een niet-zins-initiële positie?
    /// Voor de <see cref="VerbLikeKeywords"/> telt alléén de gebrackete vorm.
    /// Woordgrens-bewust (dezelfde grens als <see cref="TermMatch"/>).</summary>
    public static bool AppearsAsKeyword(string? text, string? label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label)) return false;
        var needle = label.Trim();
        return VerbLikeKeywords.Contains(needle)
            ? AppearsBracketed(text, needle)
            : Scan(text, needle, static (t, at) => IsKeywordForm(t, at));
    }

    /// <summary>Staat <paramref name="label"/> ergens DIRECT GEBRACKET in
    /// <paramref name="text"/> (<c>[Label</c>, dekt ook magnitudes)? De gemeten
    /// drukconventie voor een echt toegekend/gedefinieerd keyword (#211) — de
    /// enige vorm die telt voor verb-like keywords (klasse B) en voor
    /// resource-patients (klasse D, <see cref="ResourceMechanics"/>).</summary>
    public static bool AppearsBracketed(string? text, string? label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label)) return false;
        return Scan(text, label.Trim(), static (t, at) => at > 0 && t[at - 1] == '[');
    }

    private static bool Scan(string text, string needle, Func<string, int, bool> accepts)
    {
        var from = 0;
        while (from <= text.Length - needle.Length)
        {
            var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            if (TermMatch.IsBoundary(text, at, needle.Length) && accepts(text, at))
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

    internal static bool IsSentenceInitial(string text, int at)
    {
        var i = at - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsQuote(text[i]))) i--;
        return i < 0 || text[i] is '.' or '!' or '?';
    }

    private static bool IsQuote(char c) => c is '"' or '\'' or '“' or '”' or '‘' or '’';
}

/// <summary>Klasse A (#335) — eindpunt-aanwezigheid in keyword-gedaante: een claim
/// met een MECHANIC als agent eist dat het agent-label in een dragende
/// bewijs-eenheid in herkenbare keyword-gedaante staat — gebracket óf met
/// hoofdletter (zins-initiaal toegestaan: dit is een AANWEZIGHEIDS-check, milder
/// dan de woordvormpoort). De gemeten aanleiding: <c>Burn MODIFIES Flow</c>
/// promoveerde terwijl de audit vaststelt "the evidence never mentions a 'Burn'
/// keyword at all" — de hoofdletter-ongevoelige woordmatch van het lexicale anker
/// accepteerde een kleine-letter-prozavorm (het werkwoord burn, of een
/// <c>:rb_…:</c>-glyphtoken) als verankering van het keyword Burn.
///
/// De PATIENT-kant heeft deze check bewust NIET: haar aanwezigheid als woord is al
/// afgedwongen (textueel anker), en haar VORM is per soort geregeld
/// (<see cref="KeywordWordForm"/> voor GRANTS/REQUIRES,
/// <see cref="ResourceMechanics"/> voor resources) — een MODIFIES-doel drukt
/// legitiem in kleine-letter-werkwoordsvorm ("channel 1 rune exhausted",
/// Siphoning Strike, bevestigde era-3-promotie). Een CARD-agent is uitgezonderd:
/// die draagt zijn bewijs per identiteit in zijn eigen tekst (#249).</summary>
public static class InteractionEndpointPresence
{
    /// <summary>Geldt de poort voor deze agent? Alleen mechanic-agents — een
    /// kaart-agent is identiteits-verankerd en hoeft niet in zijn eigen tekst te
    /// staan.</summary>
    public static bool Applies(EntityType agentType) =>
        OntologySchema.IsA(agentType, EntityType.Mechanic);

    /// <summary>Staat <paramref name="label"/> ergens in <paramref name="text"/>
    /// in keyword-GEDAANTE: gebracket of met een beginhoofdletter (zins-initiaal
    /// toegestaan)? Kleine-letter-vormen (werkwoord, proza, glyphtoken) tellen
    /// niet — dat is geen vermelding van het keyword.</summary>
    public static bool MentionedAsKeyword(string? text, string? label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label)) return false;
        var needle = label.Trim();

        var from = 0;
        while (from <= text.Length - needle.Length)
        {
            var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            if (TermMatch.IsBoundary(text, at, needle.Length)
                && ((at > 0 && text[at - 1] == '[') || char.IsUpper(text[at])))
                return true;
            from = at + 1;
        }
        return false;
    }
}

/// <summary>Klasse C2 (#335) — het tegen-anker voor REQUIRES: "may"/"optional(ly)"
/// in dezelfde ZIN als het REQUIRES-anker ondermijnt de claim, want optioneel is
/// geen vereiste. Gemeten aanleiding: <c>Predict REQUIRES Recycle</c> op "may
/// recycle it" — hetzelfde paar strandde een dag eerder als GRANTS op de
/// woordvormpoort en zocht de onbewaakte deur. De claim strandt alleen wanneer
/// ÉLKE anker-dragende zin in het dragende bewijs ondermijnd is; één schone
/// anker-zin (bv. "Spend 3 XP, exhaust: Draw 1.") draagt de claim gewoon.
///
/// <b>Restrisico (zin-scope):</b> een schoon anker élders in dezelfde eenheid over
/// iets ánders redt de claim — Safety Inspector draagt naast "You may spend 3 XP…"
/// een los "each player must kill…" en passeert dus. Zelfde grens als het
/// kind-anker (#330): noodzakelijk, niet voldoende.</summary>
public static class RequiresOptionality
{
    private static readonly IReadOnlyList<string> Underminers = ["may", "optional", "optionally"];

    /// <summary>Bevat <paramref name="text"/> ergens een REQUIRES-anker
    /// (<see cref="InteractionKindAnchors"/>), ondermijnd of niet?</summary>
    public static bool HasAnchor(string? text) =>
        Sentences(text).Any(HasAnchorIn);

    /// <summary>Bevat <paramref name="text"/> een ZIN met een REQUIRES-anker
    /// zónder may/optional(ly) — een anker dat de claim echt kan dragen?</summary>
    public static bool HasCleanAnchor(string? text) =>
        Sentences(text).Any(s => HasAnchorIn(s)
            && !Underminers.Any(u => TermMatch.ContainsWord(s, u)));

    private static bool HasAnchorIn(string sentence) =>
        InteractionKindAnchors.Catalog[InteractionKinds.Requires]
            .Any(a => TermMatch.ContainsWord(sentence, a));

    /// <summary>Zinnen op ./!/?-grenzen — bewust simpel: de teksten zijn korte
    /// gedrukte regels, en een te slimme splitser zou zelf een poort-omzeiling
    /// worden.</summary>
    private static IEnumerable<string> Sentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            if (i > start) yield return text[start..i];
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}

/// <summary>Klasse D (#335) — resource-vs-keyword: een GRANTS/MODIFIES-claim met
/// een RESOURCE-achtige mechanic als patient eist expliciete keyword-taal (de
/// gebrackete vorm) in het dragende bewijs. Gemeten aanleiding: "spend 3 XP"
/// (Safety Inspector) werd MODIFIES en "Gain 1 XP" (Gardens of Becoming) werd
/// GRANTS — maar XP is een resource, geen keyword op een unit; hoeveelheids-taal
/// verbruikt of produceert de resource en zegt niets over het mechanisme.
/// REQUIRES blijft buiten de poort: een kaart die XP spendeert HANGT er echt van
/// af (drie bevestigde era-3-rijen).
///
/// De catalogus is DATA, afgeleid uit de ontologie/definities: XP is expliciet
/// "not a Game Object" (officiële definitie) en meet in 1429 kaartteksten 0×
/// gebracket tegenover 84× hoeveelheids-taal ("gain/spend N XP"). Uitbreiden mag
/// alleen op diezelfde twee gronden — definitie zegt resource, corpus drukt in
/// hoeveelheden.</summary>
public static class ResourceMechanics
{
    /// <summary>De resource-achtige mechanics (gesloten lijst, zie klasse-doc).</summary>
    public static readonly IReadOnlySet<string> Labels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XP" };

    /// <summary>Geldt de resource-poort voor deze claim? Alleen GRANTS/MODIFIES
    /// met een mechanic-patient uit de catalogus.</summary>
    public static bool Applies(string? kind, EntityType patientType, string? patientLabel) =>
        (kind == InteractionKinds.Grants || kind == InteractionKinds.Modifies)
        && OntologySchema.IsA(patientType, EntityType.Mechanic)
        && patientLabel is not null && Labels.Contains(patientLabel.Trim());
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
