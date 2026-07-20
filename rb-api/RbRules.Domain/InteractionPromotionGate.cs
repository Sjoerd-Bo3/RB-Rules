namespace RbRules.Domain;

/// <summary>De uitslag van de promotie-poort (fase 2, #226, §3.4).</summary>
public enum InteractionGateOutcome
{
    /// <summary>schema ∧ (lexicaal ∨ consensus≥N) ∧ verdict → geaccepteerd.</summary>
    Promoted,

    /// <summary>Schema klopt en het verdict is positief, maar de deterministische
    /// steun (lexicaal/consensus) ontbreekt nog → reviewqueue, wacht op
    /// corroboratie.</summary>
    Candidate,

    /// <summary>Cold-start-tier (kritiek Risico 1): een emergente card×card-
    /// hypothese zonder lexicale/consensus-steun. NIET verworpen — geparkeerd in
    /// een eigen trust-tier met micro-reviewqueue, nooit stil weggegooid.</summary>
    ModelHypothesizedUnruled,

    /// <summary>Verworpen: schema-schending, een negatief verdict, of een levende
    /// <see cref="RejectionTombstone"/> op de dedupe-sleutel. Schrijft/behoudt een
    /// tombstone (herstelpad).</summary>
    Rejected,
}

/// <summary>De signalen die de poort weegt (fase 2, #226, §3.4). Alle drie de
/// deterministische signalen (schema, lexicaal, consensus) staan NAAST het
/// LLM-verdict — de rode draad: een LLM-oordeel draagt nooit alléén een
/// promoverende actie.</summary>
/// <param name="SchemaValid">Haalt de gereïficeerde vorm het ontologie-schema
/// (<c>OntologyValidationService.ValidateReifiedInteraction</c>)? Een kale
/// COUNTERS-edge of een verkeerde rol-range zet dit op false.</param>
/// <param name="SchemaReason">Menselijke reden bij een schema-schending (null als
/// geldig).</param>
/// <param name="LexicalSupport">Bevat het bewijsanker/de gecite RuleSection de
/// betrokken termen (via aliassen)? Geen bewijszin → geen lexicale steun.</param>
/// <param name="ConsensusCount">Aantal onafhankelijke bronnen/passes dat de
/// interactie steunt.</param>
/// <param name="ConsensusThreshold">De drempel N voor consensus-steun.</param>
/// <param name="LlmVerdictInteracts">Het LLM-verdict: is er een echte,
/// noemenswaardige interactie?</param>
/// <param name="IsEmergentCardCardPair">Is dit een card×card-paar (beide rollen
/// zijn kaarten)? Bepaalt de cold-start-uitzondering.</param>
/// <param name="HasBlockingTombstone">Bestaat er een levende (niet-opgeheven)
/// <see cref="RejectionTombstone"/> op de dedupe-sleutel? Zo ja, blokkeert de
/// poort stil-heropenen.</param>
/// <param name="RolesDistinct">Zijn agent en patient verschillende entiteiten
/// (#249)? Een self-loop is per definitie geen interactie.</param>
/// <param name="IsCardOwnKeywordPair">Is dit een kaart met haar EIGEN keyword
/// (#249)? Dat feit komt al deterministisch uit <c>Card.Mechanics[]</c> via de
/// graph-projectie (HAS_MECHANIC) en hoort geen tweede, LLM-afgeleid bestaan als
/// <see cref="Interaction"/> te krijgen.</param>
public sealed record InteractionGateSignals(
    bool SchemaValid,
    string? SchemaReason,
    bool LexicalSupport,
    int ConsensusCount,
    int ConsensusThreshold,
    bool LlmVerdictInteracts,
    bool IsEmergentCardCardPair,
    bool HasBlockingTombstone,
    bool RolesDistinct = true,
    bool IsCardOwnKeywordPair = false)
{
    /// <summary>Is er deterministische steun náást het verdict?</summary>
    public bool HasDeterministicSupport =>
        LexicalSupport || (ConsensusThreshold > 0 && ConsensusCount >= ConsensusThreshold);
}

/// <summary>De poort-beslissing: de tier + een memo die zégt welke poort
/// faalde/slaagde (§3.4 — zichtbaar in de reviewqueue, inzicht-thread #236).
/// <paramref name="WritesTombstone"/> zegt of een verwerping een <b>duurzame</b>
/// <see cref="RejectionTombstone"/> verdient (flip-flop-suppressie) — alleen wanneer
/// de verwerping deterministisch gegrond is. Een transiënte schema/structuur-fout
/// (bv. nog-slechte entity-resolution) en een <i>losstaand</i> negatief LLM-verdict
/// verdienen géén grafsteen: die zou een legitieme interactie permanent onderdrukken
/// en botst met de rode draad (#236) dat een LLM-oordeel alléén nooit een
/// destructieve/blijvende actie draagt.</summary>
public sealed record InteractionGateResult(
    InteractionGateOutcome Outcome, string StatusReason, bool WritesTombstone = false)
{
    /// <summary>De bijbehorende <see cref="InteractionStatus"/>-tier-string.</summary>
    public string Status => Outcome switch
    {
        InteractionGateOutcome.Promoted => InteractionStatus.Promoted,
        InteractionGateOutcome.Candidate => InteractionStatus.Candidate,
        InteractionGateOutcome.ModelHypothesizedUnruled => InteractionStatus.ModelHypothesizedUnruled,
        InteractionGateOutcome.Rejected => InteractionStatus.Rejected,
        _ => InteractionStatus.Candidate,
    };
}

/// <summary>De deterministische promotie-poort (fase 2, #226, §3.4) — puur, geen
/// IO. Combineert de drie deterministische signalen (schema, lexicaal, consensus)
/// met het LLM-verdict tot één tier-uitslag. Nooit LLM-alleen: promotie vereist
/// <c>schema=pass ∧ (lexicaal ∨ consensus≥N) ∧ verdict=interacts</c>. De twee
/// bindende bijzonderheden:
/// <list type="bullet">
/// <item>Een levende tombstone blokkeert stil-heropenen (herstelpad = expliciete
/// beheerdersactie), ook als een nieuw model "ja" zegt.</item>
/// <item>Cold-start (kritiek Risico 1): een emergente card×card-hypothese zonder
/// lexicale/consensus-steun wordt NIET verworpen maar getierd als
/// <see cref="InteractionGateOutcome.ModelHypothesizedUnruled"/> — nooit stil weg.</item>
/// </list></summary>
public static class InteractionPromotionGate
{
    public static InteractionGateResult Evaluate(InteractionGateSignals s)
    {
        ArgumentNullException.ThrowIfNull(s);

        // 1. Een eerder verworpen relatie mag niet stil heropenen — ook niet door
        //    een verbeterd model. Herstel is een expliciete beheerdersactie.
        if (s.HasBlockingTombstone)
            return new(InteractionGateOutcome.Rejected,
                "eerder verworpen (levende tombstone); heropenen vereist expliciete " +
                "herbeoordeling door een beheerder");

        // 1b. Rollen-poort (#249). Twee deterministische weigeringen die vóór elke
        //     andere weging komen, want ze zeggen dat er structureel niets te
        //     promoveren VALT:
        //     - een self-loop (agent == patient) is geen interactie;
        //     - kaart↔eigen-keyword bestaat al deterministisch als HAS_MECHANIC-edge
        //       uit Card.Mechanics[] (GraphSyncService). De LLM leidde dat feit nóg
        //       een keer af en de lexicale poort beloonde het (de kaart ís de ene
        //       rol, haar keyword staat in haar eigen tekst) — 69% van de tabel,
        //       terwijl mech↔mech op 1,3% bleef steken.
        //     GEEN grafsteen: er is niets verworpen wat later gegrond kan blijken;
        //     de kennis leeft gewoon in de graph. Een tombstone zou bovendien een
        //     latere, échte gekwalificeerde interactie op dezelfde sleutel blokkeren.
        if (!s.RolesDistinct)
            return new(InteractionGateOutcome.Rejected,
                "agent en patient zijn dezelfde entiteit; een self-loop is geen interactie",
                WritesTombstone: false);

        if (s.IsCardOwnKeywordPair)
            return new(InteractionGateOutcome.Rejected,
                "kaart met haar eigen keyword: dat feit staat al deterministisch in de " +
                "graph (HAS_MECHANIC uit Card.Mechanics) — geen LLM-afgeleide interactie",
                WritesTombstone: false);

        // 2. Schema-poort is hard: een kale gekwalificeerde edge of een rol/conditie
        //    buiten de ontologie kan geen feit worden (versla #3, structuurverlies).
        //    GEEN grafsteen: een schema/structuur-fout is doorgaans transiënt (nog-
        //    slechte entity-resolution levert een verkeerde rol-range); een tombstone
        //    zou een later-correct opgeloste, volledig gesteunde interactie permanent
        //    onderdrukken. Deze run verwerpen, maar de sleutel niet blijvend sluiten.
        if (!s.SchemaValid)
            return new(InteractionGateOutcome.Rejected,
                $"schema-schending: {s.SchemaReason ?? "reïficatie-vorm ongeldig"}",
                WritesTombstone: false);

        // 3. Het LLM-verdict is een noodzakelijke (niet voldoende) voorwaarde: geen
        //    interactie volgens het model → verwerpen. Een grafsteen (blijvende,
        //    destructieve actie) mag hier NIET op een losstaand LLM-verdict rusten
        //    (rode draad #236): alleen wanneer er deterministische steun (lexicaal/
        //    consensus) náást het verdict staat, is de verwerping duurzaam gegrond en
        //    verdient ze flip-flop-suppressie. Een kaal false-verdict → soft-reject,
        //    geen grafsteen, zodat een LLM-false-negative de sleutel niet permanent sluit.
        if (!s.LlmVerdictInteracts)
            return new(InteractionGateOutcome.Rejected,
                s.HasDeterministicSupport
                    ? "LLM-verdict: geen noemenswaardige interactie (ondanks deterministische steun)"
                    : "LLM-verdict: geen noemenswaardige interactie",
                WritesTombstone: s.HasDeterministicSupport);

        // 4. Verdict positief + deterministische steun → promoveren.
        if (s.HasDeterministicSupport)
        {
            var support = s.LexicalSupport
                ? "bewijszin gevonden"
                : $"consensus {s.ConsensusCount}/{s.ConsensusThreshold}";
            return new(InteractionGateOutcome.Promoted,
                $"schema-ok; {support}; LLM-verdict positief");
        }

        // 5. Verdict positief maar geen deterministische steun. Cold-start-vangnet:
        //    een emergente card×card-hypothese parkeren i.p.v. weggooien.
        if (s.IsEmergentCardCardPair)
            return new(InteractionGateOutcome.ModelHypothesizedUnruled,
                $"emergente card×card-hypothese zonder lexicale/consensus-steun " +
                $"(consensus {s.ConsensusCount}/{s.ConsensusThreshold}); LLM-hypothese — " +
                "micro-review, niet verworpen (cold-start)");

        // 6. Overig (bv. keyword-interactie zonder steun): kandidaat, wacht op
        //    corroboratie/bewijszin.
        return new(InteractionGateOutcome.Candidate,
            $"wacht op corroboratie: geen bewijszin en consensus " +
            $"{s.ConsensusCount}/{s.ConsensusThreshold}");
    }
}
