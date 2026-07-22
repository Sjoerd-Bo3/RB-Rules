using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Eén getypeerd predicaat-feit op een <see cref="PredicateHolder"/>
/// (fase 5, #229): (predicaat, object-token), al genormaliseerd. Bv.
/// <c>(triggers_on, "exhaust")</c>.</summary>
public readonly record struct PredicateFact(string Predicate, string ObjectToken);

/// <summary>Een drager van predicaten waarover de <see cref="HypothesisEngine"/>
/// abductief redeneert (fase 5, #229, §5) — meestal een kaart (agent/patient van
/// een kandidaat-interactie), maar de motor is holder-type-agnostisch zodat hij ook
/// op mechanic-niveau kan draaien. De predicaten komen (via de Infrastructure-
/// projectie, integratie-follow-up) van de kaart-eigen mechanics: kaart HAS_MECHANIC
/// M, M draagt <see cref="MechanicPredicateAssertion"/>'s. <see cref="Domains"/> voedt
/// de <c>deck_domain_compatible</c>-guard (§5) die same-deck-synergie/nonbo van
/// toevallige paren scheidt; leeg = onbekend/colorless → permissief (niet over-prunen).</summary>
public sealed record PredicateHolder(
    string Ref,
    EntityType Type,
    IReadOnlyCollection<string> Domains,
    IReadOnlyList<PredicateFact> Predicates);

/// <summary>Eén abductieve hypothese-regel (fase 5, #229, §5): een getypeerd
/// property-antagonisme/-synergie-patroon dat, wanneer twee holders het
/// COMPLEMENTAIR vervullen, een kandidaat-interactie oplevert. Dit is échte abductie
/// — van "X triggert op exhaust ∧ Y voorkomt exhaust" naar "misschien een nonbo(X,Y)"
/// — i.p.v. blinde lexicale overlap. De <see cref="AgentPredicate"/>/<see cref="PatientPredicate"/>
/// binden op tokens: bij <see cref="JoinTokens"/> moeten agent- en patient-token
/// GELIJK zijn (dezelfde "exhaust"), anders zijn <see cref="AgentToken"/>/
/// <see cref="PatientToken"/> vaste constanten.</summary>
/// <param name="Id">Stabiel regel-id, gekozen als <c>{kind}:{beschrijving}</c>
/// (bv. "nonbo:exhaust-payoff-vs-ready"). Belandt in het bewijs/de memo.</param>
/// <param name="Kind">De interactie-NATUUR (combo|synergy|counter|nonbo,
/// <see cref="HypothesisKinds"/>) — het label dat de LLM-verificatie later toetst.
/// NB: dit is niet de gereïficeerde ontologie-relatie (COUNTERS/MODIFIES/…); die
/// wijst de verificatie/extractie toe (fase 2).</param>
/// <param name="JoinTokens">Moeten agent- en patient-token gelijk zijn (antagonisme
/// op DEZELFDE as, bv. exhaust vs exhaust)? Zo niet: twee vaste constanten.</param>
/// <param name="RequireDomainCompatible">Alleen paren met verenigbare deck-domeinen?
/// Aan voor same-deck-synergie/nonbo; uit voor cross-deck-counters (agent en patient
/// staan dan aan tegenovergestelde kanten van het bord).</param>
public sealed record HypothesisRule(
    string Id,
    string Kind,
    string Description,
    string AgentPredicate,
    string? AgentToken,
    string PatientPredicate,
    string? PatientToken,
    bool JoinTokens,
    bool RequireDomainCompatible);

/// <summary>De interactie-natuur-labels (dezelfde vocabulaire als de fase-3
/// <see cref="InteractionMiner"/>): het gerichte antagonisme/de synergie die de
/// hypothese vermoedt.</summary>
public static class HypothesisKinds
{
    public const string Combo = "combo";
    public const string Synergy = "synergy";
    public const string Counter = "counter";
    public const string Nonbo = "nonbo";

    public static readonly IReadOnlyList<string> All = [Combo, Synergy, Counter, Nonbo];

    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>Eén gegenereerde interactie-hypothese (fase 5, #229, §5): een gericht
/// kandidaat-paar met zijn DETERMINISTISCHE bewijs (<see cref="Reason"/> = regel-id +
/// antecedent-tuples). Dit paar gaat naar de GERICHTE LLM-verificatie (i.p.v. blind
/// N²); een positief verdict zonder onafhankelijke lexicale/consensus-steun landt via
/// de fase-2-poort in <c>model_hypothesized_unruled</c> (cold-start), nooit stil weg.</summary>
public sealed record InteractionHypothesis(
    string AgentRef, EntityType AgentType,
    string PatientRef, EntityType PatientType,
    string Kind, string RuleId, string Reason)
{
    /// <summary>De ongeordende paar-sleutel (voor dedup/precisie-meting): een
    /// interactie is als kandidaat-generatie symmetrisch, de richting draagt alleen
    /// de agent/patient-rol.</summary>
    public string UnorderedPairKey =>
        string.CompareOrdinal(AgentRef, PatientRef) <= 0
            ? $"{AgentRef}|{PatientRef}"
            : $"{PatientRef}|{AgentRef}";

    /// <summary>Emergent card×card-paar? (Beide rollen zijn een (subklasse van)
    /// Card.) Bepaalt de cold-start-uitzondering in de fase-2-poort.</summary>
    public bool IsEmergentCardCardPair =>
        OntologySchema.IsA(AgentType, EntityType.Card)
        && OntologySchema.IsA(PatientType, EntityType.Card);
}

/// <summary>De abductieve hypothese-motor (fase 5, #229, §5) — puur en getest, géén
/// IO. Tilt kandidaatgeneratie van LEXICALE overlap (fase 3, ~0,3% precisie op ~N²
/// paren) naar GETYPEERD property-antagonisme (~O(n·k), veel hogere precisie) door de
/// predicaten geïnverteerd te indexeren op (predicaat, token) en alleen de holders te
/// paren die een regel COMPLEMENTAIR vervullen. Zo hoeft niemand het paar te bedenken:
/// zodra "Fury-payoff triggert op exhaust" én "Accelerate voorkomt exhaust" in de data
/// staan, vuurt de nonbo-hypothese vanzelf.
///
/// Live rb-ai-verificatie en de Neo4j-projectie zijn een integratie-follow-up
/// (ARCHITECTURE §8); deze klasse levert de deterministische kandidaat + het bewijs,
/// <see cref="HypothesisPromotion"/> koppelt dat aan de fase-2-poort, en
/// <see cref="HypothesisYield"/> maakt de precisie-/kostenwinst MEETBAAR (geen
/// verzonnen vaste factor — kritiek B7).</summary>
public static class HypothesisEngine
{
    /// <summary>De v0-regelset (§5). Bewust drie regels — precies de spec-voorbeelden —
    /// zodat elke afgeleide hypothese een expliciet, herleidbaar patroon draagt. De
    /// set is data en breidt uit met de ontologie/nieuwe sets (CLAUDE.md: mee-evolueren),
    /// nooit met een ad-hoc match elders.</summary>
    public static readonly IReadOnlyList<HypothesisRule> DefaultRules =
    [
        // nonbo: X betaalt exhaust uit, Y houdt de unit juist ready → ze bijten
        // elkaar (same-deck). Antagonisme op DEZELFDE as (exhaust) → JoinTokens.
        new("nonbo:exhaust-payoff-vs-ready", HypothesisKinds.Nonbo,
            "Payoff dat op exhaust triggert vs. mechaniek die exhaust juist voorkomt.",
            AgentPredicate: MechanicPredicateKinds.TriggersOn, AgentToken: null,
            PatientPredicate: MechanicPredicateKinds.Prevents, PatientToken: null,
            JoinTokens: true, RequireDomainCompatible: true),

        // counter: X wil een unit targeten, Y verleent hidden (untargetable) → Y
        // counteren X. Cross-deck: geen domein-eis.
        new("counter:hidden-vs-targeted", HypothesisKinds.Counter,
            "Gerichte targeting vs. hidden (untargetable) — de één ontkracht de ander.",
            AgentPredicate: MechanicPredicateKinds.RequiresTarget, AgentToken: "unit",
            PatientPredicate: MechanicPredicateKinds.Grants, PatientToken: "hidden",
            JoinTokens: false, RequireDomainCompatible: false),

        // synergy: tank (dwingt aanvallen op zich) + deflect (verplaatst schade) →
        // redirect-synergie (same-deck).
        new("synergy:tank-redirect-into-deflect", HypothesisKinds.Synergy,
            "Tank trekt aanvallen aan; deflect verplaatst de schade — redirect-synergie.",
            AgentPredicate: MechanicPredicateKinds.Grants, AgentToken: "tank",
            PatientPredicate: MechanicPredicateKinds.Grants, PatientToken: "deflect",
            JoinTokens: false, RequireDomainCompatible: true),
    ];

    /// <summary>Genereer alle interactie-hypotheses uit de holders + regels. O(n·k):
    /// bouwt één geïnverteerde index (predicaat → token → holders) en paart per regel
    /// alleen de complementair-vervullende holders — nooit het volle N²-kruisproduct.
    /// Deterministisch geordend (paar-sleutel, dan regel-id) en per (agent,patient,regel)
    /// gededupliceerd, met de gematchte antecedent-tokens gebundeld in het bewijs.</summary>
    public static IReadOnlyList<InteractionHypothesis> Generate(
        IReadOnlyList<PredicateHolder> holders,
        IReadOnlyList<HypothesisRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(holders);
        rules ??= DefaultRules;

        // Geïnverteerde index: predicaat → token → holders (op Ref gededupliceerd
        // per token zodat een holder met hetzelfde feit twee keer niet dubbeltelt).
        var index = new Dictionary<string, Dictionary<string, List<PredicateHolder>>>(StringComparer.Ordinal);
        var seenPerToken = new HashSet<(string, string, string)>();
        foreach (var h in holders)
        {
            foreach (var f in h.Predicates)
            {
                var pred = MechanicPredicateKinds.Canonicalize(f.Predicate);
                if (pred is null) continue;
                var token = MechanicPredicateLexicon.Normalize(f.ObjectToken);
                if (token.Length == 0) continue;
                if (!seenPerToken.Add((pred, token, h.Ref))) continue;
                if (!index.TryGetValue(pred, out var byToken))
                    index[pred] = byToken = new(StringComparer.Ordinal);
                if (!byToken.TryGetValue(token, out var list))
                    byToken[token] = list = [];
                list.Add(h);
            }
        }

        // Accumuleer per (agentRef, patientRef, ruleId) de antecedent-tuples.
        var acc = new Dictionary<(string, string, string), (HypothesisRule Rule, PredicateHolder A, PredicateHolder B, SortedSet<string> Ante)>();

        void Emit(HypothesisRule rule, PredicateHolder a, PredicateHolder b, string agentToken, string patientToken)
        {
            if (a.Ref == b.Ref) return;
            if (rule.RequireDomainCompatible && !DomainCompatible(a, b)) return;
            var key = (a.Ref, b.Ref, rule.Id);
            if (!acc.TryGetValue(key, out var entry))
                acc[key] = entry = (rule, a, b, new SortedSet<string>(StringComparer.Ordinal));
            entry.Ante.Add($"{rule.AgentPredicate}({a.Ref}, {agentToken}) ∧ {rule.PatientPredicate}({b.Ref}, {patientToken})");
        }

        foreach (var rule in rules)
        {
            var agentPred = MechanicPredicateKinds.Canonicalize(rule.AgentPredicate);
            var patientPred = MechanicPredicateKinds.Canonicalize(rule.PatientPredicate);
            if (agentPred is null || patientPred is null) continue;
            if (!index.TryGetValue(agentPred, out var agentByToken)) continue;
            if (!index.TryGetValue(patientPred, out var patientByToken)) continue;

            if (rule.JoinTokens)
            {
                // Antagonisme op dezelfde as: itereer over de GEDEELDE tokens (klein),
                // kruis alleen die twee holder-lijsten.
                foreach (var (token, agentList) in agentByToken)
                {
                    if (!patientByToken.TryGetValue(token, out var patientList)) continue;
                    foreach (var a in agentList)
                    foreach (var b in patientList)
                        Emit(rule, a, b, token, token);
                }
            }
            else
            {
                var at = MechanicPredicateLexicon.Normalize(rule.AgentToken);
                var pt = MechanicPredicateLexicon.Normalize(rule.PatientToken);
                if (!agentByToken.TryGetValue(at, out var agentList)) continue;
                if (!patientByToken.TryGetValue(pt, out var patientList)) continue;
                foreach (var a in agentList)
                foreach (var b in patientList)
                    Emit(rule, a, b, at, pt);
            }
        }

        return acc.Values
            .Select(e => new InteractionHypothesis(
                e.A.Ref, e.A.Type, e.B.Ref, e.B.Type,
                e.Rule.Kind, e.Rule.Id,
                $"{e.Rule.Id}: " + string.Join(" ; ", e.Ante)))
            .OrderBy(h => h.UnorderedPairKey, StringComparer.Ordinal)
            .ThenBy(h => h.RuleId, StringComparer.Ordinal)
            .ThenBy(h => h.AgentRef, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><c>deck_domain_compatible</c> (§5): verenigbare deck-domeinen? Leeg aan
    /// één kant (onbekend/colorless) → permissief; anders een niet-lege doorsnede.
    /// Hoofdletterongevoelig, in lijn met de overige token-resolutie.</summary>
    public static bool DomainCompatible(PredicateHolder a, PredicateHolder b)
    {
        if (a.Domains.Count == 0 || b.Domains.Count == 0) return true;
        var set = new HashSet<string>(a.Domains, StringComparer.OrdinalIgnoreCase);
        return b.Domains.Any(set.Contains);
    }
}

/// <summary>De koppeling hypothese → fase-2-promotie-poort (fase 5, #229, §5). Een
/// gegenereerde <see cref="InteractionHypothesis"/> is een STRUCTUREEL vermoeden; ze
/// draagt géén lexicale of consensus-steun uit zichzelf (het bewijs is property-
/// antagonisme, geen bewijszin uit een RuleSection). Daarom vertaalt ze naar
/// <see cref="InteractionGateSignals"/> met <c>LexicalSupport=false, ConsensusCount=0</c>
/// TENZIJ de aanroeper onafhankelijke steun meegeeft. Gevolg via de ONVERANDERDE
/// fase-2-poort: een positief LLM-verdict zonder onafhankelijke steun landt in
/// <c>model_hypothesized_unruled</c> (cold-start, micro-review) — nooit stil weg, en
/// nooit een stille promotie op enkel LLM+structuur (rode draad #236). Vindt de
/// verificatie tóch een bewijszin of corroboratie, dan promoveert het paar regulier —
/// en dan gelden ook de soort-poorten (#330) over die bewijszin: sinds #333 zijn ze
/// VERPLICHTE parameters op <see cref="ToSignals"/>, zodat de fase-5-bedrading ze
/// niet kán overslaan (#300-patroon; de gate-defaults op
/// <see cref="InteractionGateSignals"/> zijn hier dus bewust onbereikbaar).</summary>
public static class HypothesisPromotion
{
    /// <summary>Bouw de poort-signalen voor een hypothese + LLM-verdict. Het
    /// structurele bewijs (regel-id + antecedenten) leeft in de <see cref="Interaction"/>-
    /// memo, niet als promotie-dragend signaal. <paramref name="lexicalSupport"/>/
    /// <paramref name="consensusCount"/> zijn de eventuele ONAFHANKELIJKE steun die de
    /// gerichte verificatie alsnog vond (default: geen).</summary>
    /// <param name="kindAnchorSupport">Soort-poort A (#330): draagt de gevonden
    /// bewijszin een lexicaal anker van de geclaimde relatiesoort
    /// (<c>InteractionKindAnchors</c>)? VERPLICHT zonder default (#333, #300-patroon):
    /// de fase-5-verificatie die een bewijszin vindt moet de poort over díé zin
    /// berekenen, net als het mining-pad — zonder tekstbewijs (geen
    /// <paramref name="lexicalSupport"/>) is de poort niet van toepassing en geeft de
    /// aanroeper true door. De typechecker dwingt af dat niemand hem kan vergeten.</param>
    /// <param name="patientWordFormSupport">Soort-poort B (#330): staat het
    /// keyword-doel van een toekennende claim in keyword-VORM in de bewijszin
    /// (<c>KeywordWordForm</c>)? Zelfde verplichting als
    /// <paramref name="kindAnchorSupport"/>; true wanneer niet van toepassing.</param>
    public static InteractionGateSignals ToSignals(
        InteractionHypothesis hypothesis,
        bool llmVerdictInteracts,
        bool kindAnchorSupport,
        bool patientWordFormSupport,
        bool schemaValid = true,
        string? schemaReason = null,
        bool lexicalSupport = false,
        int consensusCount = 0,
        int consensusThreshold = InteractionPromotionService_ConsensusDefault,
        bool hasBlockingTombstone = false)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        return new InteractionGateSignals(
            SchemaValid: schemaValid,
            SchemaReason: schemaReason,
            LexicalSupport: lexicalSupport,
            ConsensusCount: consensusCount,
            ConsensusThreshold: consensusThreshold,
            LlmVerdictInteracts: llmVerdictInteracts,
            IsEmergentCardCardPair: hypothesis.IsEmergentCardCardPair,
            HasBlockingTombstone: hasBlockingTombstone,
            KindAnchorSupport: kindAnchorSupport,
            PatientWordFormSupport: patientWordFormSupport);
    }

    // Spiegelt InteractionPromotionService.DefaultConsensusThreshold zonder de
    // Infrastructure-laag in Domain te trekken (lagen strikt éénrichting).
    public const int InteractionPromotionService_ConsensusDefault = 2;
}
