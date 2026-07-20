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

        var plan = InteractionOffering.ForMechanic(
            "Tank", ["Tank", "Assault", "Recycle"], [carrier], [], OfferingLimits.Mechanic);

        Assert.All(plan.Refs, r => Assert.StartsWith("mechanic:", r.Ref));
        Assert.Contains("mechanic:Tank", plan.Refs.Select(r => r.Ref));
        Assert.Contains("mechanic:Assault", plan.Refs.Select(r => r.Ref));
        // Recycle komt in geen bewijstekst voor.
        Assert.DoesNotContain("mechanic:Recycle", plan.Refs.Select(r => r.Ref));
        // De kaart gaat wél mee als bewijs.
        Assert.Equal([carrier], plan.Cards);
    }

    /// <summary>Ook mechanic-niveau blijft begrensd: het vocabulaire groeit met elke
    /// set, dus "alle buren" is geen optie — de sterkste co-occurrences winnen.</summary>
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
            "Tank", vocabulary, carriers, [], OfferingLimits.Mechanic);

        Assert.True(plan.Refs.Count <= OfferingLimits.Mechanic.MaxRefs);
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
