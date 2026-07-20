namespace RbRules.Domain.Ontology;

/// <summary>Het ontologie-schema v0 van het Poracle-brein als onveranderlijk,
/// machine-leesbaar register (kritiek A3: dit is de ÉNE bron waaruit later
/// prompt-enums, de parser-poort en Neo4j-constraints gegenereerd worden —
/// geen losse constanten elders). Bevat de klassenhiërarchie (SUBCLASS_OF,
/// transitief + acyclisch), de kern-relaties met domain/range/kardinaliteit/
/// logische eigenschappen, en de disjointness-assen. Alles pure data + pure
/// afleidingen (geen IO); <see cref="OntologyValidationService"/> gebruikt dit
/// als deterministische poort.
///
/// Bewuste modelleer-keuze t.o.v. de kale ASCII-boom in §2.1: <c>Card</c> hangt
/// NIET onder <c>Object</c>. De object-kaarttypes (Unit/Legend/Gear/Battlefield/
/// Rune/Token) erven van zowel <c>Card</c> als <c>Object</c> (multi-parent →
/// het multi-label <c>:Object:Card:Unit</c> dat §2.1 noemt), terwijl <c>Spell</c>
/// enkel een <c>Card</c> is. Zo blijft <c>MATCH (c:Card)</c> polymorf over álle
/// kaarten én blijft de bindende disjointness <c>Spell ⟂ Object</c> vervulbaar —
/// bij <c>Card ⊑ Object</c> zou elke Spell tegelijk Object én niet-Object zijn,
/// een lege (onvervulbare) klasse.</summary>
public static class OntologySchema
{
    // ── Klassenhiërarchie (SUBCLASS_OF-doelen per klasse) ────────────────────
    private static readonly OntologyClass[] ClassList =
    [
        new(EntityType.Thing, [], "Wortel van de hiërarchie."),

        new(EntityType.Object, [EntityType.Thing], "Bestaat fysiek in het spel."),
        new(EntityType.Card, [EntityType.Thing], "Gedrukte kaart {riotCardId, name, setId, printedText}."),
        // Object-kaarttypes: multi-parent Card + Object (multi-label :Object:Card:X).
        new(EntityType.Unit, [EntityType.Card, EntityType.Object], "Unit-kaart."),
        new(EntityType.Legend, [EntityType.Card, EntityType.Object], "Legend-kaart."),
        new(EntityType.Gear, [EntityType.Card, EntityType.Object], "Gear-kaart."),
        new(EntityType.Battlefield, [EntityType.Card, EntityType.Object], "Battlefield-kaart."),
        new(EntityType.Rune, [EntityType.Card, EntityType.Object], "Rune-kaart."),
        new(EntityType.Token, [EntityType.Card, EntityType.Object], "Token-kaart."),
        // Spell is Card maar DISJUNCT van Object (resolvet en verlaat het spel).
        new(EntityType.Spell, [EntityType.Card], "Spell-kaart; disjunct van Object."),

        new(EntityType.Concept, [EntityType.Thing], "Spel-abstractie."),
        new(EntityType.Mechanic, [EntityType.Concept], "Spelprocedure (bv. showdown, conquer)."),
        new(EntityType.Keyword, [EntityType.Concept], "Gedrukte kaarttekst met regel-betekenis (Deflect, Tank…)."),
        new(EntityType.Status, [EntityType.Concept], "Toestand die een Object heeft (Exhausted, Stunned…)."),
        new(EntityType.Zone, [EntityType.Concept], "Spelzone."),
        new(EntityType.Phase, [EntityType.Concept], "Spelfase."),
        new(EntityType.Window, [EntityType.Concept], "Timing-window."),
        new(EntityType.Trigger, [EntityType.Concept], "Trigger-conditie."),
        new(EntityType.Effect, [EntityType.Concept], "Effect."),
        new(EntityType.Cost, [EntityType.Concept], "Kosten."),
        new(EntityType.Domain, [EntityType.Concept], "Kleur/domein van een kaart."),

        new(EntityType.NormativeSource, [EntityType.Thing], "Normatieve bron; draagt authorityRank."),
        new(EntityType.RuleSection, [EntityType.NormativeSource], "Sectie uit de officiële regels."),
        new(EntityType.Ruling, [EntityType.NormativeSource], "Officiële ruling."),
        new(EntityType.Erratum, [EntityType.NormativeSource], "Erratum op een kaart/keyword."),

        new(EntityType.Claim, [EntityType.Thing], "Community-lezing; NOOIT authorityRank."),

        new(EntityType.Interaction, [EntityType.Thing], "Gereïficeerde n-aire relatie."),
        new(EntityType.Condition, [EntityType.Thing], "Gereïficeerde voorwaarde op een Interaction."),
        new(EntityType.Assertion, [EntityType.Thing], "Gereïficeerd feit-met-provenance (PROV-O)."),
        new(EntityType.Set, [EntityType.Thing], "Set / ontologie-versie-anker."),

        // Bewust GEEN Concept-subklasse (#304): een tag (factie/tribe) is
        // kaart-metadata zonder regelgedrag — zie de motivering bij EntityType.Tag.
        new(EntityType.Tag, [EntityType.Thing], "Factie/tribe-etiket op een kaart (Noxus, Yordle…)."),
    ];

    /// <summary>Alle klassen, geïndexeerd op type.</summary>
    public static readonly IReadOnlyDictionary<EntityType, OntologyClass> Classes =
        ClassList.ToDictionary(c => c.Type);

    // ── Disjointness-assen (ongeordende paren) ───────────────────────────────
    // Keyword ⟂ Mechanic ⟂ Status (drie disjuncte assen, paarsgewijs) en
    // Spell ⟂ Object. Overerving telt mee: Unit ⊑ Object, dus Unit ⟂ Spell.
    private static readonly (EntityType, EntityType)[] DisjointPairsRaw =
    [
        (EntityType.Keyword, EntityType.Mechanic),
        (EntityType.Mechanic, EntityType.Status),
        (EntityType.Keyword, EntityType.Status),
        (EntityType.Spell, EntityType.Object),
    ];

    /// <summary>De gedeclareerde disjuncte klassenparen (ongeordend). De
    /// effectieve disjointness houdt via <see cref="AreDisjoint"/> ook rekening
    /// met overerving.</summary>
    public static readonly IReadOnlyList<(EntityType A, EntityType B)> DisjointPairs = DisjointPairsRaw;

    // ── Kern-relaties (§2.2) ─────────────────────────────────────────────────
    private static readonly EntityType[] AnyCard =
        [EntityType.Card]; // subclass-gesloten: dekt Unit/Legend/…/Spell

    private static readonly OntologyRelation[] RelationList =
    [
        // Meta-relatie tussen klassen (TBox): transitief + acyclisch.
        new(RelationType.SubclassOf, "SUBCLASS_OF",
            Domain: [EntityType.Thing], Range: [EntityType.Thing],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Transitive | RelationTraits.Acyclic,
            Parameters: []),

        // HEET HAS_DOMAIN (#274), niet IN_DOMAIN: dezelfde naam-tweespalt als bij
        // HAS_MECHANIC hieronder, één relatie verderop. De projectie,
        // BrainQuery.EdgeTypes, docs/ENGINE.md en docs/BRAIN.md zeggen allemaal
        // HAS_DOMAIN; alleen dit register zei IN_DOMAIN, waardoor de gegenereerde
        // keten IN_DOMAIN ∘ GOVERNED_BY op een edge mikte die nergens bestaat.
        new(RelationType.HasDomain, "HAS_DOMAIN",
            Domain: AnyCard, Range: [EntityType.Domain],
            MinCardinality: 1, MaxCardinality: null,          // 1..* (Colorless = 1)
            Traits: RelationTraits.None, Parameters: []),

        // Kaart → de mechaniek die zij draagt. HEET HAS_MECHANIC (#274), niet
        // HAS_KEYWORD: dit schema is de ÉNE bron, dus het moet de relatie
        // beschrijven die er in Neo4j ECHT staat. GraphSyncService projecteert
        // Card.Mechanics[] deterministisch als (:Card)-[:HAS_MECHANIC]->(:Mechanic
        // {name}) met ref mechanic:{label}; datzelfde HAS_MECHANIC → :Mechanic
        // staat in het doelschema van docs/KNOWLEDGE.md, in BrainQuery.EdgeTypes
        // en in de brein-tools van rb-ai. Een tweede naam voor diezelfde relatie
        // maakte het schema onvalideerbaar én de reasoner inert: de gegenereerde
        // property-chain-Cypher matchte een edge (HAS_KEYWORD) en een knooplabel
        // (:Keyword) die nergens geschreven worden.
        // Range is dus Mechanic, niet Keyword.
        //
        // TWEE EERLIJKE KANTTEKENINGEN (#274-review), bewust niet weggepoetst:
        // (a) De tweespalt is hiermee VERPLAATST, niet opgelost. Card.Mechanics[]
        //     bevat gedrukte keywords die de canonieke laag als kind 'keyword'
        //     registreert (JobCatalog → RegisterExistingMechanicsAsync,
        //     BreinInteractionMiningService.ResolveKeywordLabelAsync), terwijl de
        //     projectie er :Mechanic-knopen van maakt en Keyword ⟂ Mechanic hierboven
        //     disjunct staat. Dezelfde entiteit draagt dus twee klassen die dit schema
        //     onverenigbaar noemt. Vóór #274 liep de breuk tussen schema en projectie,
        //     nu tussen schema en entiteit-laag. Geen live gevolg — ValidateTriple
        //     heeft buiten de tests geen aanroepers — dus dit is ontwerpschuld die
        //     hoort bij het samenvoegen van die twee lagen, niet bij een hernoeming.
        // (b) De magnitude-parameter hieronder is VOORGENOMEN, niet bestaand:
        //     MechanicMiner stript het getal weg en de projectie schrijft geen
        //     edge-properties. De declaratie blijft staan als vastgelegde bedoeling
        //     (Risico 2a: 'Assault 2' mag nooit een eigen entiteit worden).
        //
        // Keyword blijft een klasse: ERRATA_OF-range, canoniek entiteit-kind, en het
        // INVOKES-domein. Sinds #304 is Keyword géén rol-filler-type meer: HAS_ROLE
        // staat nu geregistreerd (hieronder) met de GEMETEN range Card/Mechanic —
        // de live graaf telt 492 × Card en 274 × Mechanic als filler, nul × Keyword.
        // INVOKES (Keyword → Mechanic) is na deze wijziging DOOD: geen enkele relatie
        // bereikt Keyword nog vanaf Card, v2 schrijft nergens een (:Keyword)-knoop, en
        // dus kan INVOKES in geen enkele gegenereerde regel meer voorkomen. Het blijft
        // hier staan als gedeclareerde modellering, niet als werkend pad.
        new(RelationType.HasMechanic, "HAS_MECHANIC",
            Domain: AnyCard, Range: [EntityType.Mechanic],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None,
            Parameters: ["magnitude"]),                        // Tank N, Accelerate N — niet wegstrippen

        new(RelationType.Invokes, "INVOKES",
            Domain: [EntityType.Keyword], Range: [EntityType.Mechanic],
            MinCardinality: 1, MaxCardinality: null,          // 1..*
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.HasStatus, "HAS_STATUS",
            Domain: [EntityType.Object], Range: [EntityType.Status],
            MinCardinality: 0, MaxCardinality: null,          // ABox spelstaat
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.GovernedBy, "GOVERNED_BY",
            Domain: [EntityType.Concept, EntityType.Card, EntityType.Interaction],
            Range: [EntityType.RuleSection],
            MinCardinality: 1, MaxCardinality: null,          // 1..*, vaak afgeleid
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.ErrataOf, "ERRATA_OF",
            Domain: [EntityType.Erratum], Range: [EntityType.Card, EntityType.Keyword],
            MinCardinality: 1, MaxCardinality: 1,             // precies 1, functioneel
            Traits: RelationTraits.Functional, Parameters: []),

        // Declareerde tot #296/#304 NormativeSource → NormativeSource — onwaar over
        // de graaf die we bouwen: de projectie schrijft al jaren
        // (:Erratum)-[:SUPERSEDES]->(:Card), gemeten op de live graaf, en dat is
        // inhoudelijk juist (het erratum vervangt de GEDRUKTE kaarttekst). De
        // declaratie volgt de meting (#270-les), niet andersom. Transitief is
        // hier vervallen: Erratum → Card kan per constructie nooit componeren
        // (geen Card is een Erratum), dus die trait was een belofte waar een
        // toekomstige reasoner-regel op zou kunnen bouwen zonder dat er ooit een
        // keten bestaat. Acyclisch blijft (triviaal waar over een bipartiete edge).
        new(RelationType.Supersedes, "SUPERSEDES",
            Domain: [EntityType.Erratum], Range: [EntityType.Card],
            MinCardinality: 0, MaxCardinality: 1,             // 0..1, functioneel via max
            Traits: RelationTraits.Acyclic, Parameters: []),

        new(RelationType.Corroborates, "CORROBORATES",
            Domain: [EntityType.Claim], Range: [EntityType.Claim, EntityType.NormativeSource],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        new(RelationType.Contradicts, "CONTRADICTS",
            Domain: [EntityType.Claim], Range: [EntityType.Claim, EntityType.NormativeSource],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Symmetric, Parameters: []),

        // HEET FROM_SET (#304), niet INTRODUCED_IN: het spiegelbeeld van de
        // #274-tweespalt, alleen liep de breuk hier tussen een DODE declaratie en
        // een levende projectie. GraphSyncService schrijft (:Card)-[:FROM_SET]->(:Set)
        // (gemeten: 963 rijen), BrainQuery.EdgeTypes, docs/ENGINE.md en docs/BRAIN.md
        // zeggen allemaal FROM_SET; alleen dit register zei INTRODUCED_IN — en dan
        // nog met een domein (Keyword/Mechanic) waarvoor nooit één edge is
        // geschreven. De naam én het domein volgen de projectie; wil iemand ooit
        // keyword→set-introducties vastleggen, dan is dat een NIEUWE, bewuste
        // declaratie met een bijbehorende projectie — geen half-dode restpost hier.
        new(RelationType.FromSet, "FROM_SET",
            Domain: AnyCard, Range: [EntityType.Set],
            MinCardinality: 1, MaxCardinality: 1,             // precies 1, functioneel
            Traits: RelationTraits.Functional, Parameters: []),

        // ── De zeven voorheen ongedeclareerde projectie-edges (#304) ─────────
        // Domain/range zijn de METING op de live graaf (#270-les: bevraag de bron,
        // geloof niet wat een mapper of de docs beweren), niet de bedoeling van
        // ooit. Geen van deze zeven levert een nieuwe reasoner-keten op:
        // InferenceRuleRegistry.GovernedByChains blijft exact
        // {HAS_DOMAIN, HAS_MECHANIC} ∘ GOVERNED_BY — gecontroleerd, want een keten
        // die per constructie nul rijen matcht is de stille #274-fout.

        // Claim/Ruling → het onderwerp waarover zij iets beweren (gemeten: 77
        // rijen over precies dit vierkant van 2×4 vormen). Min 0: een claim
        // waarvan het onderwerp niet resolvet blijft een knoop zonder edge
        // (bestaand, bewust projectie-gedrag).
        new(RelationType.About, "ABOUT",
            Domain: [EntityType.Claim, EntityType.Ruling],
            Range: [EntityType.Card, EntityType.Mechanic, EntityType.RuleSection, EntityType.Concept],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        // RuleSection → dichtstbijzijnde bestaande ouder-sectie binnen dezelfde
        // bron (gemeten: 2139). Mereologie: transitief + acyclisch, en ten
        // hoogste één DIRECTE ouder (0..1 — wortelsecties hebben er geen).
        new(RelationType.PartOf, "PART_OF",
            Domain: [EntityType.RuleSection], Range: [EntityType.RuleSection],
            MinCardinality: 0, MaxCardinality: 1,
            Traits: RelationTraits.Transitive | RelationTraits.Acyclic, Parameters: []),

        // Concept (primer-doc) → de RuleSection(s) waarop het gebaseerd is
        // (gemeten: 101). De kennispiramide-brug tussen afgeleide uitleg en
        // officiële tekst.
        new(RelationType.Explains, "EXPLAINS",
            Domain: [EntityType.Concept], Range: [EntityType.RuleSection],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        // Kaart → factie/tribe (gemeten: 982). Range Tag — zie de klasse-beslissing
        // bij EntityType.Tag (direct onder Thing, geen Concept).
        new(RelationType.HasTag, "HAS_TAG",
            Domain: AnyCard, Range: [EntityType.Tag],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        // Interaction → rol-filler, met de rol (agent|patient) als edge-parameter.
        // Range is de METING: 492 × Card, 274 × Mechanic, nul × Keyword — de
        // docs, de miner én ValidateReifiedInteraction beweerden alle drie
        // Card/Keyword en zaten er alle drie naast (#304). De projectie dwingt
        // deze range sinds #304 ook af (twee label-gebonden statements in
        // GraphSyncService, zoals ABOUT dat per doelsoort doet): een range
        // declareren die de projectie niet afdwingt is de #296-klasse fout.
        // Min 2: elke Interaction draagt een agent- én een patient-rol.
        new(RelationType.HasRole, "HAS_ROLE",
            Domain: [EntityType.Interaction], Range: [EntityType.Card, EntityType.Mechanic],
            MinCardinality: 2, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: ["role"]),

        // Interaction → gereïficeerde Condition (gemeten: 98; het statement dwingt
        // :Interaction af — de live knoop draagt óók :Concept, maar de guard toetst
        // wat de query garandeert). Niet verwarren met REQUIRES: dat is de
        // gekwalificeerde (reïficatie-plichtige) relatie, een ander ding.
        new(RelationType.RequiresCondition, "REQUIRES_CONDITION",
            Domain: [EntityType.Interaction], Range: [EntityType.Condition],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []),

        // Gekwalificeerde relaties: verboden als kale edge → altijd via Interaction.
        new(RelationType.Counters, "COUNTERS",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Modifies, "MODIFIES",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Grants, "GRANTS",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),
        new(RelationType.Requires, "REQUIRES",
            Domain: [], Range: [], MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.RequiresReification, Parameters: []),

        // Pre-ontologische hint (bestaande codebase): geen kennis, wel provenance.
        new(RelationType.InteractsWith, "INTERACTS_WITH",
            Domain: AnyCard, Range: AnyCard,
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.Symmetric,
            Parameters: ["status"]),                           // candidate|verified|promoted

        // Gedenormaliseerde retrieval-projectie (nooit bron van waarheid).
        new(RelationType.RelatesTo, "RELATES_TO",
            Domain: [EntityType.Concept, EntityType.Card],
            Range: [EntityType.Concept, EntityType.Card],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None,
            Parameters: ["kind", "window", "actor_status", "cost_delta", "tier"]),
    ];

    /// <summary>Alle relatietypes, geïndexeerd op type.</summary>
    public static readonly IReadOnlyDictionary<RelationType, OntologyRelation> Relations =
        RelationList.ToDictionary(r => r.Type);

    // ── Subproperty-as voor RELATES_TO-kinds (redeneer-laag, #227) ────────────
    // Legacy/alias-kind → canonieke super-kind. De reasoner materialiseert voor
    // elke alias-edge een canonieke edge (subproperty-collapse,
    // <see cref="RbRules.Domain.Reasoning.InferenceRuleRegistry"/>), zodat een
    // synoniem-kind niet naast zijn canonieke super-property als aparte relatie
    // blijft leven (wapening tegen synoniem-proliferatie, faalmodus #2). Dit
    // blijft de ÉNE schema-bron: de registry genereert automatisch precies één
    // collapse-regel per entry. v0: nog geen aliassen gedeclareerd — entries
    // komen erbij zodra concrete legacy-kinds opduiken (bewuste data-curatie,
    // geen code-wijziging). Hoofdletterongevoelig, zelfde lijn als de overige
    // kind-resolutie.
    public static readonly IReadOnlyDictionary<string, string> RelatesToKindSubProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, OntologyRelation> RelationsByEdgeName =
        RelationList.ToDictionary(r => r.EdgeName, StringComparer.OrdinalIgnoreCase);

    // ── Afleidingen over de hiërarchie ───────────────────────────────────────

    /// <summary>Alle strikte voorouders (transitief, exclusief het type zelf).
    /// De hiërarchie is per constructie acyclisch, dus de doorloop termineert.</summary>
    public static IReadOnlyCollection<EntityType> Ancestors(EntityType type)
    {
        var acc = new HashSet<EntityType>();
        void Walk(EntityType t)
        {
            if (!Classes.TryGetValue(t, out var def)) return;
            foreach (var p in def.DirectParents)
                if (acc.Add(p)) Walk(p);
        }
        Walk(type);
        return acc;
    }

    /// <summary>Reflexief-transitieve subklasse-toets: is <paramref name="sub"/>
    /// hetzelfde als of een (transitieve) subklasse van <paramref name="super"/>?
    /// Dit maakt domain/range subclass-polymorf — een Unit voldoet aan een
    /// Object-domein.</summary>
    public static bool IsA(EntityType sub, EntityType super) =>
        sub == super || Ancestors(sub).Contains(super);

    /// <summary>Strikte subklasse-toets (exclusief gelijkheid).</summary>
    public static bool IsStrictSubclassOf(EntityType sub, EntityType super) =>
        sub != super && Ancestors(sub).Contains(super);

    /// <summary>Zijn twee klassen (effectief) disjunct? Twee typen zijn disjunct
    /// zodra een voorouder-of-zichzelf van de één met een voorouder-of-zichzelf
    /// van de ander een gedeclareerd disjunct paar vormt. Zo erft Unit
    /// (⊑ Object) de disjointness met Spell.</summary>
    public static bool AreDisjoint(EntityType a, EntityType b)
    {
        var closureA = SelfAndAncestors(a);
        var closureB = SelfAndAncestors(b);
        foreach (var (x, y) in DisjointPairsRaw)
            if ((closureA.Contains(x) && closureB.Contains(y)) ||
                (closureA.Contains(y) && closureB.Contains(x)))
                return true;
        return false;
    }

    private static HashSet<EntityType> SelfAndAncestors(EntityType type)
    {
        var set = new HashSet<EntityType>(Ancestors(type)) { type };
        return set;
    }

    /// <summary>Zoek een relatie op canonieke edge-naam (SCREAMING_SNAKE_CASE),
    /// hoofdletterongevoelig — de vorm waarin de LLM-output binnenkomt.</summary>
    public static OntologyRelation? RelationByEdgeName(string edgeName) =>
        edgeName is not null && RelationsByEdgeName.TryGetValue(edgeName.Trim(), out var r) ? r : null;

    /// <summary>Parse een klassenaam (enum-naam, hoofdletterongevoelig).
    /// Retourneert <c>null</c> bij een onbekende naam — de deterministische
    /// poort geeft dan een UnknownEntityType-schending in plaats van te gokken.
    /// Accepteert UITSLUITEND een exacte enum-naam: <see cref="Enum.TryParse{T}(string,bool,out T)"/>
    /// slikt óók kale getallen ("5" → Gear) en OR-combinaties ("Card,Unit" →
    /// Unit), wat malformed rb-ai-output (gelekte index/id, aan-elkaar-geplakte
    /// labels) stil zou laten passeren; de naam-gelijkheids-guard verwerpt dat.</summary>
    public static EntityType? ParseEntityType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return Enum.TryParse<EntityType>(trimmed, ignoreCase: true, out var t)
            && Classes.ContainsKey(t)
            && t.ToString().Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            ? t : null;
    }
}
