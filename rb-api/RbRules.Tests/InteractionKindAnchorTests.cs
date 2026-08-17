using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>De pure bouwstenen van de soort-poorten (#330): het kind-anker
/// (poort A, <see cref="InteractionKindAnchors"/>) en de woordvormpoort
/// (poort B, <see cref="KeywordWordForm"/>).
///
/// De kalibratie-teksten hieronder zijn de ÉCHTE bewijsteksten van de
/// opus-audit op de eerste fable-batch (2026-07-22: 23 promoties, 9 geauditeerd,
/// 7 betwist — allemaal dezelfde faalklasse: relatiesoort-overclaim op
/// co-occurrence) plus de echte sectieteksten van de 2 bevestigde
/// REQUIRES-paren. Alle asserts gebruiken UITGESCHREVEN literals (#286d/#293:
/// een assertie tegen de constante die ze bewaakt schuift mee) — wijzigt de
/// catalogus, dan hoort hier bewust iets rood te gaan.</summary>
public class InteractionKindAnchorTests
{
    // ── De echte bewijsteksten (fixtures uit de audit van 2026-07-22) ─────────

    // Pair 1 — mechanic:Level MODIFIES mechanic:Legion (betwist): §727.1.b.3 is
    // de enige sectie die beide keywords noemt — als voorbeelden van dezelfde
    // categorie, niet als interactie.
    private const string Rule727_1b3 =
        "If an ability has multiple Dependent Keywords, all of them must have their " +
        "Condition met in order for the ability to be active. Example: A unit reads " +
        "“[Level 11][>>][Legion][>] When you conquer, gain 1 point.” In order for " +
        "the conquer effect of the unit to be active, its controller must have 11 XP and " +
        "have finalized a card other than the unit that turn.";

    // Pair 2 — mechanic:Add COUNTERS mechanic:Reaction (betwist): §164.2.a citeert
    // de gedrukte vorm — Add wordt gebrúikt binnen Reaction-getimede abilities.
    private const string Rule164_2a = "[Reaction] — Add [1].";

    // Pair 3 — mechanic:Vision GRANTS mechanic:Recycle (betwist): §817.2.a —
    // recycle is hier het werkwoord (de optionele actie binnen Vision), geen
    // toegekend keyword.
    private const string Rule817_2a =
        "The player may choose to recycle or not recycle for each instance of Vision separately.";

    // Pair 4 — mechanic:Accelerate GRANTS mechanic:Ready (betwist): §805.6 —
    // "become ready" is een vervangingseffect-toestand, geen toegekend keyword;
    // §805.6.a zegt zelfs expliciet dat Accelerate NIET interacteert.
    private const string Rule805_6 =
        "Accelerate generates a delayed replacement effect that replaces a unit entering " +
        "the board exhausted with it entering ready. It does not enter exhausted and then " +
        "become ready.";
    private const string Rule805_6a =
        "Accelerate will not interact with, or trigger, abilities that are affected by " +
        "units becoming ready.";

    // Pair 5 — mechanic:Hunt GRANTS mechanic:XP (betwist): §823.1.c.1 — een SPELER
    // "gains X XP" (resource), geen keyword op een unit.
    private const string Rule823_1c1 =
        "Hunt is functionally short for: “When I Conquer or Hold, my controller gains " +
        "X XP.” See rule 728. XP for more information";

    // Pair 6 — mechanic:Tank MODIFIES mechanic:Backline (betwist): §465.2.c(.6) —
    // louter co-occurrence in één toewijzings-voorbeeld.
    private const string Rule465_2c6 =
        "A player must obey all requirements and restrictions on damage assignment if " +
        "able. Example: A player is assigning damage to the following units: a unit with " +
        "Tank (“I must be assigned combat damage first.”); a unit with Backline " +
        "(“I must be assigned combat damage last.”); and another unit without any " +
        "abilities. That player must assign combat damage first to the unit with Tank, " +
        "then to the unit with no abilities, then to the unit with Backline.";

    // Pair 7 — mechanic:Predict GRANTS mechanic:Recycle (betwist): definitie-tekst,
    // gereconstrueerd rond het letterlijke audit-citaat "You may recycle it".
    private const string PredictDefinition =
        "Predict is functionally short for: “Look at the top card of your deck. " +
        "You may recycle it.”";

    // Bevestigd 1 — mechanic:Level REQUIRES mechanic:XP: §727.1.b.2, de echte
    // conditie-zin ("As long as its controller has 3 XP").
    private const string Rule727_1b2 =
        "The Dependent Ability is Active exactly as written while the Condition is true " +
        "Example: Gustwalker has “[Level 3][>] I have +1 [M] and Ganking.” As " +
        "long as its controller has 3 XP, Gustwalker’s Ganking is active.";

    // Bevestigd 2 — mechanic:Weaponmaster REQUIRES mechanic:Equip: §821.1.b
    // ("pay its Equip cost").
    private const string Rule821_1b =
        "Weaponmaster is a Play Effect that chooses an Equipment you control and allows " +
        "you to pay its Equip cost at a discount.";

    // ── Poort A: de 7 betwiste teksten dragen het anker van hun soort NIET ────

    [Theory]
    [InlineData("MODIFIES", Rule727_1b3)]   // pair 1: Level↔Legion
    [InlineData("COUNTERS", Rule164_2a)]    // pair 2: Add↔Reaction
    [InlineData("GRANTS", Rule817_2a)]      // pair 3: Vision↔Recycle
    [InlineData("GRANTS", Rule805_6)]       // pair 4: Accelerate↔Ready
    [InlineData("GRANTS", Rule805_6a)]
    [InlineData("GRANTS", Rule823_1c1)]     // pair 5: Hunt↔XP ("gains" is bewust géén anker)
    [InlineData("MODIFIES", Rule465_2c6)]   // pair 6: Tank↔Backline
    [InlineData("GRANTS", PredictDefinition)] // pair 7: Predict↔Recycle
    public void CarriesKind_BetwisteBewijsteksten_DragenHunSoortNiet(string kind, string text) =>
        Assert.False(InteractionKindAnchors.CarriesKind(kind, text));

    // ── Poort A: de 2 bevestigde REQUIRES-paren halen hun anker wél ───────────

    [Theory]
    [InlineData(Rule727_1b2)]   // "As long as its controller has 3 XP"
    [InlineData(Rule727_1b3)]   // "must have 11 XP" — zelfde sectie, andere soort-vraag
    [InlineData(Rule821_1b)]    // "pay its Equip cost"
    public void CarriesKind_BevestigdeRequiresTeksten_DragenHunSoortWel(string text) =>
        Assert.True(InteractionKindAnchors.CarriesKind("REQUIRES", text));

    // ── Poort A: kalibratie-randen, expliciet vastgelegd ──────────────────────

    [Fact]
    public void CarriesKind_GainsIsBewustGeenGrantsAnker()
    {
        // De Hunt→XP-overclaim ("my controller gains X XP") is precies met dit
        // werkwoord geformuleerd — "gains" toelaten heropent de gemeten faalklasse.
        // Een echte grant die met "gains" is geformuleerd degradeert dus naar
        // Candidate (reviewqueue): aanvaard, de poort is noodzakelijk, niet voldoende.
        Assert.False(InteractionKindAnchors.CarriesKind("GRANTS", "this card gains [Text]"));
    }

    [Fact]
    public void CarriesKind_BecomeIsBewustGeenGrantsAnker() =>
        // "become ready" (§805.6) is de Accelerate→Ready-overclaim zelf.
        Assert.False(InteractionKindAnchors.CarriesKind(
            "GRANTS", "it does not enter exhausted and then become ready"));

    [Fact]
    public void CarriesKind_DeGemetenDrukconventiesVoorGranting_TellenWel()
    {
        // Live kaartcorpus: "Friendly units have [Deflect]" (Petricite Monument),
        // "give a unit [Ganking] this turn" (Gem Jammer) — dít is hoe granting
        // gedrukt wordt.
        Assert.True(InteractionKindAnchors.CarriesKind("GRANTS", "Friendly units have [Deflect]"));
        Assert.True(InteractionKindAnchors.CarriesKind("GRANTS", "give a unit [Ganking] this turn"));
        Assert.True(InteractionKindAnchors.CarriesKind("GRANTS", "grants the chosen unit [Tank]"));
    }

    [Fact]
    public void CarriesKind_CountersFamilie_DektDeBestaandeBewijszinnen()
    {
        // De bewijszinnen waarop de bestaande suite al promoveert.
        Assert.True(InteractionKindAnchors.CarriesKind(
            "COUNTERS", "Deflect prevents Assault damage during a Showdown."));
        Assert.True(InteractionKindAnchors.CarriesKind("COUNTERS", "This unit counters Deflect."));
        Assert.True(InteractionKindAnchors.CarriesKind(
            "COUNTERS", "Tank reduces the damage that Assault would deal."));
    }

    [Fact]
    public void CarriesKind_MatchtOpWoordgrens_NietOpWoorddeel() =>
        // "stop" is een anker; "unstoppable" bevat het alleen als woorddeel —
        // geen substring-promotie (dezelfde grens als TermMatch, #249-review).
        Assert.False(InteractionKindAnchors.CarriesKind("COUNTERS", "an unstoppable force"));

    [Fact]
    public void CarriesKind_OnbekendeSoort_IsNooitGedragen()
    {
        Assert.False(InteractionKindAnchors.CarriesKind(null, "grants [Tank]"));
        Assert.False(InteractionKindAnchors.CarriesKind("HAS_MECHANIC", "grants [Tank]"));
        Assert.False(InteractionKindAnchors.CarriesKind("GRANTS", null));
    }

    // ── Poort B: werkwoordvorm vs keyword-vorm, op de echte teksten ───────────

    [Theory]
    [InlineData(Rule817_2a, "Recycle")]        // "to recycle or not recycle" — werkwoord
    [InlineData(Rule805_6, "Ready")]           // "entering ready … become ready" — toestand
    [InlineData(Rule805_6a, "Ready")]          // "units becoming ready"
    [InlineData(PredictDefinition, "Recycle")] // "You may recycle it" — werkwoord
    public void AppearsAsKeyword_WerkwoordVormen_TellenNiet(string text, string label) =>
        Assert.False(KeywordWordForm.AppearsAsKeyword(text, label));

    [Fact]
    public void AppearsAsKeyword_NietZinsInitieleHoofdletter_TeltWel() =>
        // §823.1.c.1: "my controller gains X XP." — XP als gedefinieerde term
        // midden in de zin. (Pair 5 strandt dus op poort A, niet op poort B.)
        Assert.True(KeywordWordForm.AppearsAsKeyword(Rule823_1c1, "XP"));

    [Fact]
    public void AppearsAsKeyword_GebracketVorm_TeltAltijd()
    {
        // De gemeten drukconventie (#211: 31 keywords, allemaal gebracket).
        Assert.True(KeywordWordForm.AppearsAsKeyword("This grants [Recycle] to a unit.", "Recycle"));
        // Ook met magnitude: [Assault 2] blijft de familie Assault.
        Assert.True(KeywordWordForm.AppearsAsKeyword("[Assault 2] resolves first.", "Assault"));
    }

    [Fact]
    public void AppearsAsKeyword_ZinsInitieleHoofdletter_TeltNiet()
    {
        // "Ready" als eerste woord is ambigu (gebiedende wijs) — bewust niet geteld.
        Assert.False(KeywordWordForm.AppearsAsKeyword("Ready the unit.", "Ready"));
        Assert.False(KeywordWordForm.AppearsAsKeyword(
            "Look at the top card of your deck. Ready it afterwards.", "Ready"));
    }

    [Fact]
    public void AppearsAsKeyword_GekapitaliseerdWerkwoord_VerbLikeCatalogusDichtHetLek()
    {
        // Het #331-restrisico ("Riot kapitaliseert spelwerkwoorden ook midden in
        // een zin", §436.1) is in de era-3-audit gematerialiseerd ("Ready me",
        // Hungry Wolf) en in #335 gedicht: voor de verb-like keywords (Ready,
        // Recycle — gemeten 0× gebracket in 1429 kaartteksten) telt alléén de
        // gebrackete vorm.
        Assert.False(KeywordWordForm.AppearsAsKeyword(
            "Predicting a card is the act of looking at a single card from the top of the " +
            "Main Deck and choosing whether or not to Recycle it.", "Recycle"));
        Assert.False(KeywordWordForm.AppearsAsKeyword(
            ":rb_rune_order:: Ready me and give me +1 :rb_might: this turn.", "Ready"));
        // De gebrackete vorm blijft de legitieme route voor verb-like keywords.
        Assert.True(KeywordWordForm.AppearsAsKeyword("This grants [Recycle].", "Recycle"));
        // Buiten de catalogus blijft het restrisico bestaan (en gedocumenteerd):
        // een gekapitaliseerd werkwoord midden in de zin passeert nog steeds.
        Assert.True(KeywordWordForm.AppearsAsKeyword(
            "On its Deathknell, Channel 2 runes exhausted.", "Channel"));
    }

    [Fact]
    public void Applies_GrantsEnRequiresMetMechanicPatient()
    {
        Assert.True(KeywordWordForm.Applies("GRANTS", EntityType.Mechanic));
        // #335-C1: REQUIRES doet mee sinds de kind-omleiding (Predict↔Recycle
        // strandde als GRANTS en kwam als REQUIRES door de onbewaakte deur terug).
        Assert.True(KeywordWordForm.Applies("REQUIRES", EntityType.Mechanic));
        // COUNTERS/MODIFIES bewust niet: de prozavorm is daar normaal bewijs
        // ("Deflect prevents Assault damage"; "channel 1 rune exhausted" —
        // Siphoning Strike, bevestigde era-3-promotie).
        Assert.False(KeywordWordForm.Applies("COUNTERS", EntityType.Mechanic));
        Assert.False(KeywordWordForm.Applies("MODIFIES", EntityType.Mechanic));
        // Kaart-doelen hebben geen keyword-vorm.
        Assert.False(KeywordWordForm.Applies("GRANTS", EntityType.Unit));
        Assert.False(KeywordWordForm.Applies("GRANTS", EntityType.Card));
        Assert.False(KeywordWordForm.Applies(null, EntityType.Mechanic));
    }

    // ── De poort-integratie in InteractionPromotionGate ──────────────────────

    private static InteractionGateSignals Signals(
        bool kindAnchor = true, bool wordForm = true, bool verdict = true) => new(
        SchemaValid: true, SchemaReason: null, LexicalSupport: true,
        ConsensusCount: 1, ConsensusThreshold: 2, LlmVerdictInteracts: verdict,
        IsEmergentCardCardPair: false, HasBlockingTombstone: false,
        KindAnchorSupport: kindAnchor, PatientWordFormSupport: wordForm);

    [Fact]
    public void Gate_KindAnkerOntbreekt_DegradeertNaarCandidate_NooitStilWeg()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(kindAnchor: false));

        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);
        Assert.Equal("kind_anchor", r.DegradedBy);
        Assert.Contains("kind_anchor", r.StatusReason);
        Assert.False(r.WritesTombstone);
    }

    [Fact]
    public void Gate_WoordvormOntbreekt_DegradeertNaarCandidate()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(wordForm: false));

        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);
        Assert.Equal("word_form", r.DegradedBy);
        Assert.Contains("word_form", r.StatusReason);
    }

    [Fact]
    public void Gate_BeidePoortenStranden_WoordvormWintDeReden() =>
        // B vóór A: de woordvormpoort is de specifiekere diagnose ("recycle is
        // hier een werkwoord") en wint de status_reason bij een dubbele misser.
        Assert.Equal("word_form",
            InteractionPromotionGate.Evaluate(
                Signals(kindAnchor: false, wordForm: false)).DegradedBy);

    [Fact]
    public void Gate_BeidePoortenGehaald_PromoveertOngewijzigd()
    {
        var r = InteractionPromotionGate.Evaluate(Signals());

        Assert.Equal(InteractionGateOutcome.Promoted, r.Outcome);
        Assert.Null(r.DegradedBy);
    }

    [Fact]
    public void Gate_ZonderDeterministischeSteun_MaskerenDePoortenDeRedenNiet()
    {
        // Zonder steun promoveert het item toch niet; de bestaande
        // "wacht op corroboratie"-reden blijft dan de eerlijke diagnose.
        var r = InteractionPromotionGate.Evaluate(new InteractionGateSignals(
            SchemaValid: true, SchemaReason: null, LexicalSupport: false,
            ConsensusCount: 0, ConsensusThreshold: 2, LlmVerdictInteracts: true,
            IsEmergentCardCardPair: false, HasBlockingTombstone: false,
            KindAnchorSupport: false, PatientWordFormSupport: false));

        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);
        Assert.Null(r.DegradedBy);
        Assert.Contains("corroboratie", r.StatusReason);
    }

    [Fact]
    public void Gate_NegatiefVerdictMetSteun_MaarZonderSoortAnker_GeenGrafsteen()
    {
        // De #324b-spiegel (#330): bewijs dat de relatieSOORT niet draagt is niet
        // sterk genoeg om te promoveren, dus óók niet om permanent te sluiten —
        // anders staat de grafsteen alsnog op een losstaand LLM-verdict (#236).
        var r = InteractionPromotionGate.Evaluate(Signals(kindAnchor: false, verdict: false));

        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
        Assert.False(r.WritesTombstone);
    }

    [Fact]
    public void Gate_NegatiefVerdictMetVolledigeSteun_BlijftDuurzaamVerwerpen()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(verdict: false));

        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
        Assert.True(r.WritesTombstone);
    }
}
