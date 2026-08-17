using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Pure tests voor de aanbieding-planner (#286) — WELK vocabulaire krijgt
/// één extractie-aanroep te zien?
///
/// De aanleiding is een meting, geen smaak: zelfde kaarttekst, 3 refs → 200 na 49,0s,
/// 39 refs → afgekapt op de 90s-timeout. Het aangeboden vocabulaire drijft de duur, en
/// het groeit met elke set — dus de begrenzing is de enige vorm van de vraag die
/// meeschaalt. Deze tests bewaken zowel de bovengrens (dekking mag niet stiekem
/// terugkomen) als de ondergrens (dekking mag niet dalen).</summary>
public class InteractionOfferingTests
{
    // ── De begroting zelf ────────────────────────────────────────────────────

    [Fact]
    public void Budget_HoudtHetAnkerOokBovenDeCap()
    {
        var refs = OfferedRefBudget.Apply(
            [
                Candidate("card:a", OfferedRefTier.Anchor),
                Candidate("mechanic:B", OfferedRefTier.Printed),
                Candidate("mechanic:C", OfferedRefTier.Neighbour),
            ],
            maxRefs: 1);

        // Zonder onderwerp is de vraag zinloos; het anker overleeft altijd.
        Assert.Equal(["card:a"], refs.Select(r => r.Ref));
    }

    [Fact]
    public void Budget_OrdentOpTierDanGewicht_EnIsDeterministisch()
    {
        var refs = OfferedRefBudget.Apply(
            [
                Candidate("card:partner", OfferedRefTier.Context),
                Candidate("mechanic:Zwak", OfferedRefTier.Neighbour, weight: 1),
                Candidate("mechanic:Sterk", OfferedRefTier.Neighbour, weight: 9),
                Candidate("card:focus", OfferedRefTier.Anchor),
                Candidate("mechanic:Gedrukt", OfferedRefTier.Printed),
            ],
            maxRefs: 4);

        Assert.Equal(
            ["card:focus", "mechanic:Gedrukt", "mechanic:Sterk", "mechanic:Zwak"],
            refs.Select(r => r.Ref));
    }

    [Fact]
    public void Budget_DedupetOpRef()
    {
        var refs = OfferedRefBudget.Apply(
            [
                Candidate("card:a", OfferedRefTier.Anchor),
                Candidate("mechanic:B", OfferedRefTier.Printed),
                Candidate("mechanic:B", OfferedRefTier.Neighbour, weight: 5),
            ],
            maxRefs: 10);

        Assert.Equal(2, refs.Count);
    }

    // ── Kaart-niveau ─────────────────────────────────────────────────────────

    /// <summary>De kern van #286: een grote buurt mag nooit integraal de prompt in.</summary>
    [Fact]
    public void ForCard_GroteBuurt_BlijftBinnenDeBegroting()
    {
        var vocabulary = Enumerable.Range(1, 60).Select(i => $"Kw{i}").ToList();
        var partners = Enumerable.Range(1, 20)
            .Select(i => new OfferingCard(
                $"card:p{i}", $"Partner {i}", EntityType.Card,
                $"Tank works with Kw{i}.", ["Tank", $"Kw{i}"]))
            .ToList();

        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Focus", EntityType.Card, "Tank reduces damage.", ["Tank"]),
            partners, [], vocabulary, OfferingLimits.Card);

        Assert.True(plan.Refs.Count <= OfferingLimits.Card.MaxRefs);
        Assert.Contains("card:focus", plan.Refs.Select(r => r.Ref));
        Assert.Contains("mechanic:Tank", plan.Refs.Select(r => r.Ref));
    }

    /// <summary>Dekking mag niet dalen: een keyword dat de focus-kaart in haar EIGEN
    /// tekst noemt maar zelf niet draagt (kaart↔andermans-keyword, #249) moet worden
    /// aangeboden — óók als geen enkele partner het draagt. Vóór #286 kon dat niet: de
    /// kandidaten kwamen uitsluitend uit de partner-keywords.</summary>
    [Fact]
    public void ForCard_KeywordInEigenTekst_WordtAangeboden()
    {
        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Vanguard", EntityType.Card,
                "This unit counters Deflect.", ["Tank"]),
            partnerCandidates: [], sectionCandidates: [],
            vocabulary: ["Tank", "Deflect", "Assault"], OfferingLimits.Card);

        Assert.Contains("mechanic:Deflect", plan.Refs.Select(r => r.Ref));
        // Assault komt nergens voor ⇒ geen buur, dus niet aangeboden.
        Assert.DoesNotContain("mechanic:Assault", plan.Refs.Select(r => r.Ref));
    }

    /// <summary>Een keyword dat nergens samen met de kaart of haar keywords voorkomt is
    /// geen buur. Het aanbieden ervan koopt niets en kost redeneerruimte — precies de
    /// kostenpost die de timeout veroorzaakte.</summary>
    [Fact]
    public void ForCard_KeywordZonderCoOccurrence_WordtNietAangeboden()
    {
        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Focus", EntityType.Card, "It does something.", ["Tank"]),
            partnerCandidates: [], sectionCandidates: [],
            vocabulary: ["Tank", "Recycle", "Legion", "Deathknell"], OfferingLimits.Card);

        Assert.Equal(["card:focus", "mechanic:Tank"], plan.Refs.Select(r => r.Ref));
    }

    /// <summary>#286-review, blokkade 6: de Context-tier (partner-kaarten) was het
    /// EERSTE dat de begroting weggooide, dus bij ≥8 gedrukte keywords bleef
    /// <c>card:focus</c> als enige kaart-ref over. Dan draait de kaart-pass wél maar kan
    /// hij per constructie niets vinden dat álleen hij kan vinden — kaart↔kaart en
    /// kaart↔andermans-keyword hebben een tweede KAART-ref nodig. De reserve draait dat
    /// om: de gedrukte keywords wijken, want de paren die zíj opleveren zijn het werk
    /// van de mechanic-pass.</summary>
    [Fact]
    public void ForCard_VeelGedrukteKeywords_HoudtDePartnerRollen()
    {
        var printed = Enumerable.Range(1, 12).Select(i => $"Eigenkw{i}").ToList();
        var partners = Enumerable.Range(1, 3)
            .Select(i => new OfferingCard(
                $"card:p{i}", $"Partner {i}", EntityType.Card,
                $"Eigenkw1 works with Buurkw{i}.", ["Eigenkw1", $"Buurkw{i}"]))
            .ToList();

        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Focus", EntityType.Card, "Veel keywords.", printed),
            partners, [], [], OfferingLimits.Card);

        var refs = plan.Refs.Select(r => r.Ref).ToList();
        var cardRefs = refs.Where(r => r.StartsWith("card:")).ToList();

        // Anker + minstens twee partner-rollen. Bewust een LETTERLIJKE 3 en niet
        // 1 + OfferingLimits.Card.ReservedPartnerCards: een assertie tegen de constante
        // die ze bewaakt schuift mee zodra iemand de reserve op 0 zet — dezelfde fout
        // die de review in de vlaggenschip-test aanwees.
        Assert.True(
            cardRefs.Count >= 3,
            $"slechts {cardRefs.Count} kaart-refs; kaart↔kaart wordt dan onmogelijk");
        Assert.Contains("card:focus", refs);
        Assert.True(refs.Count <= 12);
    }

    /// <summary>Elke gekozen partner is per constructie ook een REF (#286-review,
    /// blokkade 3). Anders zou hij bij het scoren als identiteits-anker meetellen
    /// terwijl <c>BuildOffer</c> hem in de prompt geen ref-header geeft — en dan geeft
    /// de begroting een ref uit aan een buur die nooit kan promoveren.</summary>
    [Fact]
    public void ForCard_ElkeGekozenPartner_IsOokEenRef()
    {
        foreach (var printedCount in Enumerable.Range(0, 13))
        {
            var printed = Enumerable.Range(1, printedCount).Select(i => $"Eigenkw{i}").ToList();
            var partners = Enumerable.Range(1, 3)
                .Select(i => new OfferingCard(
                    $"card:p{i}", $"Partner {i}", EntityType.Card,
                    $"Text with Buurkw{i}.", [$"Buurkw{i}"]))
                .ToList();

            var plan = InteractionOffering.ForCard(
                new OfferingCard("card:focus", "Focus", EntityType.Card, "Tekst.", printed),
                partners, [], [], OfferingLimits.Card);

            var refs = plan.Refs.Select(r => r.Ref).ToHashSet();
            foreach (var partner in plan.Cards.Where(c => c.Ref != "card:focus"))
                Assert.True(refs.Contains(partner.Ref),
                    $"{partner.Ref} is bewijs maar geen rol bij {printedCount} gedrukte keywords");
            Assert.True(plan.Refs.Count <= OfferingLimits.Card.MaxRefs,
                $"{plan.Refs.Count} refs bij {printedCount} gedrukte keywords");
        }
    }

    /// <summary>Een regelsectie telt alleen als bewijs wanneer ze twee aangeboden
    /// labels noemt — dáár staat een keyword↔keyword-relatie officieel opgeschreven.</summary>
    [Fact]
    public void ForCard_Regelsectie_MetTweeLabels_GaatMeeAlsCiteerbaarAnker()
    {
        var section = new OfferingSection(
            "section:core-rules-pdf/704.2", "core-rules-pdf §704.2",
            "Tank reduces incoming damage before Snipe assigns its damage.");

        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Gamma", EntityType.Card, "It has some ability.", ["Tank"]),
            partnerCandidates: [], sectionCandidates: [section],
            vocabulary: ["Tank", "Snipe"], OfferingLimits.Card);

        Assert.Contains("mechanic:Snipe", plan.Refs.Select(r => r.Ref));
        Assert.Equal([section], plan.Sections);
    }

    /// <summary>#286-review, blokkade 4: er werd gescoord tegen ALLE kandidaat-secties
    /// (tot twaalf) terwijl er hooguit drie worden aangeboden. Een buur kon zo zijn
    /// gewicht ontlenen aan een sectie die de prompt nooit zag — een ref uitgegeven aan
    /// een paar dat per constructie onpromoveerbaar is. Nu is de gekozen sectie-set
    /// exact de set waartegen gescoord wordt.</summary>
    [Fact]
    public void ForCard_BuurUitEenNietGekozenSectie_WordtNietAangeboden()
    {
        // Vier secties die Tank elk met een ánder keyword paren; MaxSections is 3.
        var sections = new[] { "Aaa", "Bbb", "Ccc", "Ddd" }
            .Select((kw, i) => new OfferingSection(
                $"section:core/{i}", $"core §{i}", $"Tank interacts with {kw}."))
            .ToList();

        var plan = InteractionOffering.ForCard(
            new OfferingCard("card:focus", "Focus", EntityType.Card, "Tekst.", ["Tank"]),
            partnerCandidates: [], sectionCandidates: sections,
            vocabulary: ["Tank", "Aaa", "Bbb", "Ccc", "Ddd"], OfferingLimits.Card);

        Assert.Equal(OfferingLimits.Card.MaxSections, plan.Sections.Count);

        // Élke aangeboden buur moet in een AANGEBODEN bewijstekst staan.
        var offeredText = string.Join("\n", plan.Sections.Select(x => x.Text));
        foreach (var neighbour in plan.Refs.Where(r => r.Tier == OfferedRefTier.Neighbour))
            Assert.Contains(neighbour.Label, offeredText);
    }

    // ── Mechanic-niveau ──────────────────────────────────────────────────────

    /// <summary>Op mechanic-niveau zijn ALLEEN keywords een rol. Kaarten zijn bewijs —
    /// zonder die scheiding zou de pass terugvallen op de kaart↔eigen-keyword-
    /// tautologie die #249 uitroeide.</summary>
    [Fact]
    public void ForMechanic_BiedtAlleenKeywordRollenAan()
    {
        var carrier = new OfferingCard(
            "card:a", "Alpha", EntityType.Card,
            "Tank reduces the damage that Assault would deal.", ["Tank", "Assault"]);
        var section = new OfferingSection(
            "section:core-rules-pdf/704.2", "core-rules-pdf §704.2",
            "Tank reduces incoming damage before Assault applies.");

        var plan = InteractionOffering.ForMechanic(
            "Tank", ["Tank", "Assault", "Recycle"], [carrier], [section],
            OfferingLimits.Mechanic);

        Assert.All(plan.Refs, r => Assert.StartsWith("mechanic:", r.Ref));
        Assert.Contains("mechanic:Tank", plan.Refs.Select(r => r.Ref));
        Assert.Contains("mechanic:Assault", plan.Refs.Select(r => r.Ref));
        // Recycle komt in geen bewijstekst voor.
        Assert.DoesNotContain("mechanic:Recycle", plan.Refs.Select(r => r.Ref));
        // De kaart gaat wél mee als bewijs.
        Assert.Equal([carrier], plan.Cards);
    }

    /// <summary>#324 — de spiegel van de bewijstier-eis. Elk paar op mechanic-niveau
    /// is mechanic↔mechanic, en dat claim-niveau draagt kaarttekst niet: een buur die
    /// ALLEEN in een carrier-kaarttekst naast het subject staat (het Eclipse
    /// Herald-geval: "Stun … Ready" in één kaart-specifiek effect) kan per
    /// constructie nooit promoveren — hem aanbieden is de ref-verspilling van #286a.
    /// De aanroeper ziet dan &lt; 2 refs en slaat de LLM-call deterministisch over.</summary>
    [Fact]
    public void ForMechanic_BuurAlleenInCarrierKaarttekst_WordtNietAangeboden()
    {
        var carrier = new OfferingCard(
            "card:eclipse-herald", "Eclipse Herald", EntityType.Unit,
            "When this unit applies Stun to an enemy, Ready it.", ["Stun"]);

        var plan = InteractionOffering.ForMechanic(
            "Stun", ["Stun", "Ready"], [carrier], [], OfferingLimits.Mechanic);

        Assert.DoesNotContain("mechanic:Ready", plan.Refs.Select(r => r.Ref));
        Assert.Single(plan.Refs); // alleen het anker: niets om over te redeneren
        // De carrier blijft wel bewijs (context voor het verdict), geen buur-bron.
        Assert.Equal([carrier], plan.Cards);
    }

    /// <summary>#286-review, punt 5 — omgekeerde scheefheid: de DEFINITIE ging al als
    /// bewijs de prompt in en de lexicale poort las er al op, maar zij telde niet mee
    /// bij het kiezen van de buren. Een mech↔mech-paar dat alléén in de officiële
    /// keyword-definitie samen staat werd dus nooit aangeboden, terwijl het de poort wél
    /// zou passeren. Dat is de beste bron die er is — trust-tier-1-regeltekst, geen
    /// LLM-output — en die lag ongebruikt.</summary>
    [Fact]
    public void ForMechanic_PaarDatAlleenInDeDefinitieStaat_WordtAangeboden()
    {
        var plan = InteractionOffering.ForMechanic(
            "Tank", ["Tank", "Assault"],
            carrierCandidates: [], sectionCandidates: [], OfferingLimits.Mechanic,
            definition: "Tank reduces the damage that Assault would deal.");

        Assert.Contains("mechanic:Assault", plan.Refs.Select(r => r.Ref));
        Assert.Equal("Tank reduces the damage that Assault would deal.", plan.Definition);
    }

    /// <summary>Ook mechanic-niveau blijft begrensd: het vocabulaire groeit met elke
    /// set, dus "alle buren" is geen optie — de sterkste co-occurrences winnen. De
    /// co-occurrence staat hier in de DEFINITIE (regeltekst): sinds #324 is dat, naast
    /// secties, de enige bron waar buren uit mogen komen.</summary>
    [Fact]
    public void ForMechanic_VeelBuren_BlijftBinnenDeBegroting()
    {
        var vocabulary = new List<string> { "Tank" };
        vocabulary.AddRange(Enumerable.Range(1, 40).Select(i => $"Kw{i}"));
        var carriers = Enumerable.Range(1, 5)
            .Select(i => new OfferingCard(
                $"card:c{i}", $"C{i}", EntityType.Card,
                "Tank " + string.Join(" ", Enumerable.Range(1, 40).Select(k => $"Kw{k}")), []))
            .ToList();

        var plan = InteractionOffering.ForMechanic(
            "Tank", vocabulary, carriers, [], OfferingLimits.Mechanic,
            definition: "Tank " + string.Join(" ", Enumerable.Range(1, 40).Select(k => $"Kw{k}")));

        Assert.True(plan.Refs.Count <= OfferingLimits.Mechanic.MaxRefs);
        Assert.True(plan.Refs.Count > 1, "geen enkele buur uit de definitie gekozen");
        Assert.True(plan.Cards.Count <= OfferingLimits.Mechanic.MaxEvidenceCards);
    }

    /// <summary>Een subject zonder enige buur levert één ref op; de aanroeper slaat de
    /// LLM-call dan over — dat is deterministisch, geen uitval.</summary>
    [Fact]
    public void ForMechanic_ZonderBuren_LevertAlleenHetAnker()
    {
        var plan = InteractionOffering.ForMechanic(
            "Tank", ["Tank"], [], [], OfferingLimits.Mechanic);

        Assert.Single(plan.Refs);
    }

    private static OfferedRefCandidate Candidate(
        string reference, OfferedRefTier tier, int weight = 0) =>
        new(reference, reference, EntityType.Keyword, tier, weight);
}
