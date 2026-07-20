using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Prioriteit van een aangeboden ref binnen één extractie-vraag (#286).
/// Lager = eerder gehouden wanneer de begroting knelt.</summary>
public enum OfferedRefTier
{
    /// <summary>Het ONDERWERP van de vraag (de focus-kaart, de subject-mechaniek).
    /// Valt nooit af — zonder anker is er geen vraag.</summary>
    Anchor = 0,

    /// <summary>Wat letterlijk op de kaart gedrukt staat: haar eigen keywords. Die
    /// zijn deterministisch te lezen (gebracket, zie <see cref="MechanicMiner"/>) en
    /// horen per definitie bij deze kaart.</summary>
    Printed = 1,

    /// <summary>Een DIRECTE buur: een keyword dat aantoonbaar samen met een gedrukt
    /// keyword in één bewijs-eenheid voorkomt. Alleen zulke buren maken een
    /// keyword↔keyword-vraag zinvol.</summary>
    Neighbour = 2,

    /// <summary>Context-rol: een partner-kaart die minstens één aangeboden keyword
    /// draagt. Nuttig voor kaart↔kaart en kaart↔ander-keyword, maar het eerste dat
    /// sneuvelt als de begroting knelt.</summary>
    Context = 3,
}

/// <summary>Eén kandidaat-ref met zijn prioriteit en gewicht, zoals die de
/// begroting (<see cref="OfferedRefBudget"/>) in gaat.</summary>
/// <param name="Weight">Hoe sterk deze ref bij het onderwerp hoort (co-occurrence-
/// telling). Hoger wint binnen dezelfde tier.</param>
public sealed record OfferedRefCandidate(
    string Ref, string Label, EntityType Type, OfferedRefTier Tier, int Weight = 0);

/// <summary>De harde begroting op het aangeboden vocabulaire per extractie-aanroep
/// (#286).
///
/// <b>Waarom dit bestaat.</b> De meting op productie (identieke kaarttekst, alleen
/// het vocabulaire verschilt): 3 refs → 200 na 49,0s; 39 refs → 500 na 92,1s, de
/// timeout van <c>EXTRACT_TIMEOUT_MS = 90_000</c>. Wat de duur drijft is het AANTAL
/// AANGEBODEN REFS, niet de kaarttekst — de 49s bij 3 refs is grotendeels vaste
/// SDK-opstartkost. Méér vrágen per aanroep is dus bijna gratis; méér vocabulaire is
/// peperduur, want het vermenigvuldigt de redeneerruimte.
///
/// En het is een SCHAALKLIP, geen vaste faalkans: we boden bij elke kaart het hele
/// keyword-vocabulaire van de buurt aan, en dat vocabulaire groeit met elke set. Hoe
/// meer het brein leert, hoe meer extracties omvielen. Een begroting die niet met de
/// kennisbank meegroeit is daarom geen optimalisatie maar de enige houdbare vorm van
/// de vraag.</summary>
public static class OfferedRefBudget
{
    /// <summary>Selecteert binnen de begroting: <see cref="OfferedRefTier.Anchor"/>
    /// blijft altijd staan (ook als de cap kleiner is dan het aantal ankers — zonder
    /// onderwerp is de vraag zinloos), de rest gaat op tier, dan gewicht (aflopend),
    /// dan ref (ordinaal) zodat de uitkomst volledig deterministisch is en niet van
    /// corpusvolgorde afhangt. Dubbele refs vallen weg; de eerst-aangeboden vorm
    /// wint.</summary>
    public static IReadOnlyList<OfferedRefCandidate> Apply(
        IEnumerable<OfferedRefCandidate> candidates, int maxRefs)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<OfferedRefCandidate>();
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c.Ref) && seen.Add(c.Ref)) unique.Add(c);

        var anchors = unique.Where(c => c.Tier == OfferedRefTier.Anchor).ToList();
        var rest = unique
            .Where(c => c.Tier != OfferedRefTier.Anchor)
            .OrderBy(c => (int)c.Tier)
            .ThenByDescending(c => c.Weight)
            .ThenBy(c => c.Ref, StringComparer.Ordinal)
            .Take(Math.Max(0, maxRefs - anchors.Count));

        return [.. anchors, .. rest];
    }
}

/// <summary>De grenzen van één extractie-aanbieding (#286). Twee vaste standen —
/// kaart-niveau en mechanic-niveau — zodat de begroting één plek heeft en niet als
/// losse constanten door de service zwerft.</summary>
/// <param name="MaxRefs">Harde bovengrens op het aantal aangeboden refs.</param>
/// <param name="MaxNeighbourKeywords">Hoeveel buur-keywords er maximaal bij mogen.</param>
/// <param name="MaxPartnerCards">Hoeveel partner-kaarten er als ROL meegaan
/// (kaart-niveau) — bij mechanic-niveau is dit 0: kaarten zijn daar bewijs, geen rol.</param>
/// <param name="MaxSections">Hoeveel officiële regelsecties er als bewijs meegaan.</param>
/// <param name="MaxEvidenceCards">Hoeveel kaartteksten er als bewijs meegaan
/// (mechanic-niveau; bij kaart-niveau zijn dat de partner-rollen zelf).</param>
public sealed record OfferingLimits(
    int MaxRefs, int MaxNeighbourKeywords, int MaxPartnerCards, int MaxSections,
    int MaxEvidenceCards = 0)
{
    /// <summary>Kaart-niveau: anker + eigen keywords + hooguit vier directe buren +
    /// hooguit drie partner-kaarten. In de praktijk ~8-12 refs waar het hele
    /// buurt-vocabulaire er 39 gaf.</summary>
    public static readonly OfferingLimits Card = new(
        MaxRefs: 12, MaxNeighbourKeywords: 4, MaxPartnerCards: 3, MaxSections: 3);

    /// <summary>Mechanic-niveau: alleen keyword-rollen (het anker + zijn directe
    /// buren). Kaarten en regelsecties zijn hier bewijstekst, geen rol — de vraag
    /// gaat expliciet over mechanic↔mechanic.</summary>
    public static readonly OfferingLimits Mechanic = new(
        MaxRefs: 8, MaxNeighbourKeywords: 6, MaxPartnerCards: 0, MaxSections: 3,
        MaxEvidenceCards: 3);
}

/// <summary>Eén kaart zoals de aanbieding-planner haar ziet: de ref, het
/// ontologie-type, de tekst (bewijs) en de canonieke keyword-labels die zij
/// draagt.</summary>
public sealed record OfferingCard(
    string Ref, string Name, EntityType Type, string Text, IReadOnlyList<string> KeywordLabels);

/// <summary>Eén officiële regelsectie zoals de planner haar ziet.
/// <paramref name="Ref"/> is de citeerbare <see cref="BrainRef.Section"/> — null
/// wanneer de chunk geen §-code draagt en dus niet als <c>governed_by</c>-anker kan
/// dienen (de tekst blijft dan gewoon bewijs).</summary>
public sealed record OfferingSection(string? Ref, string Label, string Text);

/// <summary>Het resultaat van de planner: precies wat de aanroep in gaat.</summary>
/// <param name="Refs">De aangeboden rollen, al door de begroting.</param>
/// <param name="Cards">De kaarten die als bewijs meegaan (bij kaart-niveau zijn dat
/// de focus-kaart plus de gekozen partners; de partner-rollen staan óók in
/// <paramref name="Refs"/>).</param>
/// <param name="Sections">De gekozen regelsecties (bewijs + citeerbare ankers).</param>
public sealed record OfferingPlan(
    IReadOnlyList<OfferedRefCandidate> Refs,
    IReadOnlyList<OfferingCard> Cards,
    IReadOnlyList<OfferingSection> Sections);

/// <summary>Puur en deterministisch: WELK vocabulaire krijgt één extractie-aanroep
/// aangeboden (#286)?
///
/// De oude aanbieding was "de focus-kaart, tot vier partner-kaarten, en de keywords
/// van die HELE buurt" — in de praktijk 39 refs, ongeacht of die keywords iets met
/// deze kaart te maken hadden. Dat is precies de aanbieding die de gemeten timeout
/// veroorzaakte, en ze groeit met elke set mee.
///
/// Hier staat de vervanging, langs de lijn van #211/#249 (ADR-17): <b>lees eerst wat
/// gedrukt is</b>. De keywords van de kaart zijn deterministisch bekend; een BUUR is
/// alleen een buur als hij aantoonbaar samen met zo'n gedrukt keyword in ÉÉN
/// bewijs-eenheid staat (partner-kaarttekst of officiële regelsectie). Een keyword
/// dat nergens met de focus samen voorkomt kán geen relatie met haar hebben die uit
/// dit bewijs blijkt — het aanbieden ervan koopt niets en kost redeneerruimte.
///
/// De LLM-vraag blijft GESLOTEN: alles wat hier uit komt is een enum in het
/// tool-schema, en de parser gooit weg wat er niet in stond. De planner maakt de
/// vraag kleiner, nooit ruimer.</summary>
public static class InteractionOffering
{
    /// <summary>Kaart-niveau: het vocabulaire rond één focus-kaart.
    ///
    /// <list type="number">
    /// <item>de focus-kaart is het anker;</item>
    /// <item>haar eigen keywords gaan altijd mee (gedrukt = deterministisch bekend,
    /// en de tautologie-poort heeft ze nodig om kaart↔eigen-keyword te herkennen);</item>
    /// <item>partner-kaarten uit de gedeelde-mechaniek-buurt, begrensd — zij zijn de
    /// kaart-rollen én de bewijstekst waarin buren zich moeten bewijzen;</item>
    /// <item>buur-keywords: keywords van díe partners die samen met een eigen keyword
    /// in één AANGEBODEN bewijs-eenheid staan, op co-occurrence-telling geordend.</item>
    /// </list></summary>
    public static OfferingPlan ForCard(
        OfferingCard focus, IReadOnlyList<OfferingCard> partnerCandidates,
        IReadOnlyList<OfferingSection> sectionCandidates,
        IReadOnlyList<string> vocabulary, OfferingLimits limits)
    {
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(partnerCandidates);
        ArgumentNullException.ThrowIfNull(sectionCandidates);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(limits);

        var own = new HashSet<string>(focus.KeywordLabels, StringComparer.OrdinalIgnoreCase);

        // Partners eerst, dán buren. De aanroeper levert alleen kaarten die minstens
        // één mechaniek met de focus delen, dus ze zijn per constructie relevant; de
        // volgorde is deterministisch (RiftboundId). Deze volgorde — niet andersom —
        // garandeert dat élke aangeboden buur in een AANGEBODEN bewijs-eenheid staat.
        // Een buur die zijn gewicht aan een tekst ontleent die de prompt niet haalt,
        // zou de lexicale poort nooit kunnen passeren.
        var partners = partnerCandidates.Take(limits.MaxPartnerCards).ToList();

        // Bewijs-eenheden waarin een buur zich mag bewijzen: de focus-tekst, de
        // partner-teksten en de officiële regelsecties.
        //
        // Kaartteksten zijn IDENTITEITS-verankerd: die kaart is zelf een aangeboden rol,
        // dus haar naam of haar eigen keywords hoeven er niet in te staan. Dat is niet
        // gemakzucht maar precies de kaart↔andermans-keyword-zaak: een kaart die "This
        // unit counters Deflect" drukt, beïnvloedt een keyword dat zij zélf niet draagt.
        // Zou de focus-tekst een ankerlabel moeten bevatten, dan viel juist die relatie
        // weg — een kaart draagt haar mechanics lang niet altijd letterlijk in haar
        // eigen tekst.
        var units = new List<EvidenceText> { new(focus.Text, IdentityAnchored: true) };
        units.AddRange(partners.Select(p => new EvidenceText(p.Text, IdentityAnchored: true)));
        units.AddRange(sectionCandidates.Select(s => new EvidenceText(s.Text, IdentityAnchored: false)));

        // Kandidaat-buren: de keywords van de partners ÉN het volledige canonieke
        // vocabulaire. Dat laatste is geen terugval naar "stuur alles mee" — het
        // vocabulaire wordt hier alleen GELEZEN om te scoren, en alleen wat
        // aantoonbaar co-occurreert haalt de (begrensde) aanbieding. Het maakt de
        // kaart-vraag zelfs breder dan voorheen: een keyword dat de focus-kaart in haar
        // tekst noemt maar dat geen enkele partner draagt, kón vroeger niet worden
        // aangeboden en kan dat nu wel.
        var neighbours = RankNeighbours(
            partners.SelectMany(p => p.KeywordLabels).Concat(vocabulary), own, own, units,
            limits.MaxNeighbourKeywords);

        var offeredLabels = new HashSet<string>(own, StringComparer.OrdinalIgnoreCase);
        foreach (var (label, _) in neighbours) offeredLabels.Add(label);

        var sections = PickSections(sectionCandidates, offeredLabels, limits.MaxSections);

        var candidates = new List<OfferedRefCandidate>
        {
            new(focus.Ref, focus.Name, focus.Type, OfferedRefTier.Anchor),
        };
        candidates.AddRange(focus.KeywordLabels.Select(
            l => new OfferedRefCandidate(
                BrainRef.Mechanic(l).Format(), l, EntityType.Keyword, OfferedRefTier.Printed)));
        candidates.AddRange(neighbours.Select(
            n => new OfferedRefCandidate(
                BrainRef.Mechanic(n.Label).Format(), n.Label, EntityType.Keyword,
                OfferedRefTier.Neighbour, n.Weight)));
        candidates.AddRange(partners.Select(
            p => new OfferedRefCandidate(p.Ref, p.Name, p.Type, OfferedRefTier.Context)));

        var refs = OfferedRefBudget.Apply(candidates, limits.MaxRefs);

        // Alleen de partners die de begroting overleefden gaan als bewijs mee: een
        // kaarttekst aanbieden waarvan de rol wegviel is ruis zonder rol.
        var keptRefs = refs.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        var cards = new List<OfferingCard> { focus };
        cards.AddRange(partners.Where(p => keptRefs.Contains(p.Ref)));

        return new(refs, cards, sections);
    }

    /// <summary>Mechanic-niveau: het vocabulaire rond één canonieke mechaniek (#286).
    ///
    /// 38 mechanics tegenover 1311 kaarten. "Equip modificeert Might" geldt voor élke
    /// kaart met <c>[Equip]</c>, en de graph-projectie waaiert mechanics al
    /// deterministisch naar kaarten uit — dezelfde dekking voor ~35× minder aanroepen.
    ///
    /// Hier zijn ALLEEN keywords een rol: het subject en zijn directe buren. Kaarten
    /// en regelsecties gaan als bewijs mee maar nooit als rol, zodat de vraag
    /// letterlijk "hoe grijpen deze twee mechanieken in elkaar?" is en niet stilletjes
    /// terugvalt op de kaart↔eigen-keyword-tautologie die #249 uitroeide.</summary>
    public static OfferingPlan ForMechanic(
        string subjectLabel, IReadOnlyList<string> vocabulary,
        IReadOnlyList<OfferingCard> carrierCandidates,
        IReadOnlyList<OfferingSection> sectionCandidates, OfferingLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectLabel);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(carrierCandidates);
        ArgumentNullException.ThrowIfNull(sectionCandidates);
        ArgumentNullException.ThrowIfNull(limits);

        var subject = new HashSet<string>([subjectLabel], StringComparer.OrdinalIgnoreCase);

        // Bewijskaarten eerst kiezen, dán buren scoren — zelfde reden als bij
        // ForCard: een buur moet zich bewijzen in tekst die de prompt ook echt haalt.
        var cards = carrierCandidates
            .Where(c => TermMatch.ContainsWord(c.Text, subjectLabel))
            .Take(limits.MaxEvidenceCards)
            .ToList();
        // Géén identiteits-ankers hier: op mechanic-niveau is geen enkele kaart een
        // aangeboden rol, dus elke buur moet zich TEXTUEEL naast het subject bewijzen.
        // Dat is strenger dan op kaart-niveau — en terecht, want het is precies wat de
        // lexicale poort straks ook eist.
        var units = cards.Select(c => new EvidenceText(c.Text, IdentityAnchored: false))
            .Concat(sectionCandidates.Select(s => new EvidenceText(s.Text, IdentityAnchored: false)))
            .ToList();

        var neighbours = RankNeighbours(
            vocabulary, subject, subject, units, limits.MaxNeighbourKeywords);

        var offeredLabels = new HashSet<string>(subject, StringComparer.OrdinalIgnoreCase);
        foreach (var (label, _) in neighbours) offeredLabels.Add(label);

        var sections = PickSections(sectionCandidates, offeredLabels, limits.MaxSections);

        var candidates = new List<OfferedRefCandidate>
        {
            new(BrainRef.Mechanic(subjectLabel).Format(), subjectLabel,
                EntityType.Keyword, OfferedRefTier.Anchor),
        };
        candidates.AddRange(neighbours.Select(
            n => new OfferedRefCandidate(
                BrainRef.Mechanic(n.Label).Format(), n.Label, EntityType.Keyword,
                OfferedRefTier.Neighbour, n.Weight)));

        return new(OfferedRefBudget.Apply(candidates, limits.MaxRefs), cards, sections);
    }

    /// <summary>Eén bewijstekst zoals de buur-weging haar ziet.
    /// <paramref name="IdentityAnchored"/> is waar wanneer de tekst van een AANGEBODEN
    /// KAART-ROL is: die kaart is dan haar eigen anker en de ankerlabels hoeven er niet
    /// letterlijk in te staan. Voor regelsecties (en voor kaarten die alleen bewijs
    /// zijn) is dat onwaar.</summary>
    private sealed record EvidenceText(string Text, bool IdentityAnchored);

    /// <summary>De buur-regel, één keer geschreven voor beide niveaus.
    ///
    /// Deze regel is bewust een SPIEGEL van de lexicale promotie-poort
    /// (<see cref="InteractionEvidence.ExpressesRelation"/>): een kandidaat-label is een
    /// buur wanneer er een bewijs-eenheid bestaat waarin het label textueel staat én de
    /// andere kant verankerd is — hetzij door identiteit (de eenheid ÍS een aangeboden
    /// kaart-rol), hetzij textueel (een ankerlabel staat er ook in). Dat is precies de
    /// voorwaarde waaronder de poort later steun kan vinden, dus we bieden geen buren
    /// aan die per constructie nooit een relatie-bewijs kunnen krijgen — en we laten er
    /// ook geen weg die dat wél kan.
    ///
    /// Het gewicht is het aantal eenheden waar dat gebeurt. Woordgrens-bewust
    /// (<see cref="TermMatch.ContainsWord"/>), zodat "Tank" niet op willekeurig
    /// regelproza valt. Nul = geen buur: zulke labels worden niet aangeboden.</summary>
    private static List<(string Label, int Weight)> RankNeighbours(
        IEnumerable<string> candidateLabels, IReadOnlySet<string> exclude,
        IReadOnlySet<string> anchors, IReadOnlyList<EvidenceText> units, int max)
    {
        var scored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in candidateLabels)
        {
            var label = (raw ?? "").Trim();
            if (label.Length == 0 || exclude.Contains(label) || scored.ContainsKey(label)) continue;

            var weight = units.Count(u =>
                TermMatch.ContainsWord(u.Text, label)
                && (u.IdentityAnchored || anchors.Any(a => TermMatch.ContainsWord(u.Text, a))));
            if (weight > 0) scored[label] = weight;
        }

        return [.. scored
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, max))
            .Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>Regelsecties die minstens TWEE aangeboden labels noemen — daar staat
    /// een keyword↔keyword-relatie officieel opgeschreven. De begrotings-diversiteit
    /// uit de #249-review blijft: een sectie telt alleen mee als ze minstens één nog
    /// niet gedekt label-PAAR toevoegt, anders vullen drie vroege secties die
    /// toevallig hetzelfde paar noemen de hele begroting en hangt de uitslag aan de
    /// corpusvolgorde in plaats van aan het bewijs.</summary>
    private static List<OfferingSection> PickSections(
        IReadOnlyList<OfferingSection> candidates, IReadOnlySet<string> labels, int max)
    {
        var picked = new List<OfferingSection>();
        if (labels.Count < 2 || max <= 0) return picked;

        var covered = new HashSet<(string, string)>();
        foreach (var s in candidates)
        {
            if (picked.Count >= max) break;
            var hits = labels.Where(l => TermMatch.ContainsWord(s.Text, l)).ToList();
            if (hits.Count < 2) continue;

            var pairs = LabelPairs(hits).ToList();
            if (pairs.All(covered.Contains)) continue;
            covered.UnionWith(pairs);
            picked.Add(s);
        }
        return picked;
    }

    /// <summary>Alle ongeordende label-paren uit één sectie, ordinaal genormaliseerd
    /// zodat (K1,K2) en (K2,K1) hetzelfde paar zijn.</summary>
    private static IEnumerable<(string, string)> LabelPairs(IReadOnlyList<string> labels)
    {
        for (var i = 0; i < labels.Count; i++)
            for (var j = i + 1; j < labels.Count; j++)
                yield return string.CompareOrdinal(labels[i], labels[j]) <= 0
                    ? (labels[i], labels[j])
                    : (labels[j], labels[i]);
    }
}
