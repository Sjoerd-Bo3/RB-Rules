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
/// <param name="KindAnchorSupport">Poort A (#330): draagt een dragende
/// bewijs-eenheid een lexicaal anker van de geclaimde relatieSOORT
/// (<see cref="InteractionKindAnchors"/>)? Default true = niet van toepassing
/// (paden zonder tekstbewijs, zoals de hypothese-motor); het evidence-dragende
/// pad berekent hem VERPLICHT — <c>InteractionPromotionRequest</c> heeft er geen
/// default voor, zodat de typechecker afdwingt dat geen aanroeper de poort
/// stilzwijgend overslaat (#300-les).</param>
/// <param name="PatientWordFormSupport">Poort B (#330, verbreed in #335 naar
/// REQUIRES + verb-like catalogus): staat het keyword-doel van een toekennende/
/// vereisende claim in keyword-VORM in het bewijs (<see cref="KeywordWordForm"/>)?
/// Default true = niet van toepassing; zelfde verplichting op het request als bij
/// <paramref name="KindAnchorSupport"/>.</param>
/// <param name="EndpointPresenceSupport">Klasse A (#335): staat een mechanic-AGENT
/// in keyword-gedaante in het dragende bewijs
/// (<see cref="InteractionEndpointPresence"/>)? Default true = niet van
/// toepassing; verplicht veld op het request.</param>
/// <param name="RequiresNotOptional">Klasse C2 (#335): draagt het dragende bewijs
/// van een REQUIRES-claim minstens één anker-zin zónder may/optional(ly)
/// (<see cref="RequiresOptionality"/>)? Default true = niet van toepassing;
/// verplicht veld op het request.</param>
/// <param name="ResourcePatientSupport">Klasse D (#335): draagt het bewijs van een
/// GRANTS/MODIFIES-claim op een resource-patient de gebrackete keyword-vorm
/// (<see cref="ResourceMechanics"/>)? Default true = niet van toepassing;
/// verplicht veld op het request.</param>
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
    bool IsCardOwnKeywordPair = false,
    bool KindAnchorSupport = true,
    bool PatientWordFormSupport = true,
    bool EndpointPresenceSupport = true,
    bool RequiresNotOptional = true,
    bool ResourcePatientSupport = true)
{
    /// <summary>Is er deterministische steun náást het verdict?</summary>
    public bool HasDeterministicSupport =>
        LexicalSupport || (ConsensusThreshold > 0 && ConsensusCount >= ConsensusThreshold);
}

/// <summary>Machine-leesbare namen van de soort-poorten (#330/#335) — de waarde
/// van <see cref="InteractionGateResult.DegradedBy"/> en het label in run-detail
/// en status_reason. Bewust korte, stabiele tokens: beheer telt erop.</summary>
public static class InteractionGatePorts
{
    /// <summary>Poort A (#330): kind-anker ontbreekt in het dragende bewijs.</summary>
    public const string KindAnchor = "kind_anchor";

    /// <summary>Poort B (#330): keyword-doel staat alleen in werkwoord-/prozavorm.</summary>
    public const string WordForm = "word_form";

    /// <summary>Klasse A (#335): mechanic-agent komt niet in keyword-gedaante in
    /// het dragende bewijs voor.</summary>
    public const string EndpointPresence = "endpoint_presence";

    /// <summary>Klasse C2 (#335): elk REQUIRES-anker staat in een zin met
    /// may/optional(ly) — optioneel is geen vereiste.</summary>
    public const string Optionality = "optionality";

    /// <summary>Klasse D (#335): GRANTS/MODIFIES op een resource-patient zonder
    /// gebrackete keyword-vorm in het bewijs.</summary>
    public const string ResourcePatient = "resource_patient";
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
/// <param name="DegradedBy">Wélke soort-poort (#330) een zou-promoveren-claim
/// naar Candidate degradeerde (<see cref="InteractionGatePorts"/>), of null als
/// er geen poort vuurde. Run-detail telt hierop (ADR-20: nooit stil).</param>
public sealed record InteractionGateResult(
    InteractionGateOutcome Outcome, string StatusReason, bool WritesTombstone = false,
    string? DegradedBy = null)
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
        //    Sinds #330 wegen de soort-poorten hier symmetrisch mee (de #324b-spiegel:
        //    dezelfde steun, dezelfde grens) — bewijs dat de relatieSOORT niet draagt is
        //    niet sterk genoeg om te promoveren, dus óók niet om permanent te sluiten.
        if (!s.LlmVerdictInteracts)
        {
            var durable = s.HasDeterministicSupport
                && s.KindAnchorSupport && s.PatientWordFormSupport
                && s.EndpointPresenceSupport && s.RequiresNotOptional
                && s.ResourcePatientSupport;
            return new(InteractionGateOutcome.Rejected,
                s.HasDeterministicSupport
                    ? "LLM-verdict: geen noemenswaardige interactie (ondanks deterministische steun)"
                    : "LLM-verdict: geen noemenswaardige interactie",
                WritesTombstone: durable);
        }

        // 4. Verdict positief + deterministische steun → promoveren, TENZIJ een
        //    soort-poort (#330/#335) strandt: dan Candidate (reviewqueue), nooit stil
        //    weg — zelfde soft-pad als de #324-bewijstier. Volgorde is bewust van
        //    specifiek naar generiek: B vóór het kind-anker (#330: "recycle is hier
        //    een werkwoord" is de scherpere diagnose), dan de #335-klassen —
        //    resource_patient en optionality zijn per constructie disjunct van het
        //    kind-anker-pad (beide veronderstellen een gevonden anker), en
        //    endpoint_presence sluit als meest generieke aanwezigheids-check de rij.
        if (s.HasDeterministicSupport)
        {
            if (!s.PatientWordFormSupport)
                return new(InteractionGateOutcome.Candidate,
                    "word_form-poort (#330): het keyword-doel staat alleen als werkwoord/" +
                    "prozavorm in het bewijs, niet in keyword-vorm ([…] of hoofdletter-term " +
                    "buiten zinsbegin; verb-like keywords eisen de gebrackete vorm, #335) — " +
                    "kandidaat, wacht op review",
                    DegradedBy: InteractionGatePorts.WordForm);
            if (!s.KindAnchorSupport)
                return new(InteractionGateOutcome.Candidate,
                    "kind_anchor-poort (#330): het dragende bewijs bevat geen lexicaal anker " +
                    "voor deze relatiesoort — co-occurrence zegt niet wélke relatie; " +
                    "kandidaat, wacht op review",
                    DegradedBy: InteractionGatePorts.KindAnchor);
            if (!s.ResourcePatientSupport)
                return new(InteractionGateOutcome.Candidate,
                    "resource_patient-poort (#335): het doel is een resource-mechanic (geen " +
                    "toekenbaar unit-keyword) en het bewijs spreekt in hoeveelheden — een " +
                    "GRANTS/MODIFIES-claim eist hier de gebrackete keyword-vorm; kandidaat, " +
                    "wacht op review",
                    DegradedBy: InteractionGatePorts.ResourcePatient);
            if (!s.RequiresNotOptional)
                return new(InteractionGateOutcome.Candidate,
                    "optionality-poort (#335): elk REQUIRES-anker in het dragende bewijs " +
                    "staat in een zin met may/optional(ly) — een optionele kost is geen " +
                    "vereiste; kandidaat, wacht op review",
                    DegradedBy: InteractionGatePorts.Optionality);
            if (!s.EndpointPresenceSupport)
                return new(InteractionGateOutcome.Candidate,
                    "endpoint_presence-poort (#335): het agent-keyword komt in het dragende " +
                    "bewijs niet in keyword-gedaante voor (gebracket of met hoofdletter) — " +
                    "co-occurrence met alleen een werkwoord-/prozavorm draagt de claim " +
                    "niet; kandidaat, wacht op review",
                    DegradedBy: InteractionGatePorts.EndpointPresence);

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
