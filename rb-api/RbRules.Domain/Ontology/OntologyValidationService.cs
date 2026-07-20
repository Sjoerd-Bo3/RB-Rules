namespace RbRules.Domain.Ontology;

/// <summary>Soort schending die de deterministische poort kan vaststellen.</summary>
public enum OntologyViolationCode
{
    UnknownEntityType,
    UnknownRelation,
    DomainMismatch,
    RangeMismatch,
    CardinalityExceeded,
    ReificationRequired,
    Disjointness,
    SubclassCycle,
}

/// <summary>Eén schending met machineleesbare code en een menselijke reden.</summary>
public sealed record OntologyViolation(OntologyViolationCode Code, string Message);

/// <summary>Gestructureerd validatie-resultaat: geldig-ja/nee, een korte reden
/// (samengevouwen schendingen, of <c>null</c> bij geldig) en de volledige lijst
/// schendingen. Geschikt als deterministische poort NAAST een LLM-oordeel: de
/// LLM adviseert, dit zegt hard of een triple überhaupt in het schema past.</summary>
public sealed record OntologyValidationResult(
    bool IsValid,
    IReadOnlyList<OntologyViolation> Violations)
{
    /// <summary>Korte, samengevouwen reden — <c>null</c> als er niets mis is.</summary>
    public string? Reason =>
        Violations.Count == 0 ? null : string.Join("; ", Violations.Select(v => v.Message));

    public static readonly OntologyValidationResult Valid = new(true, []);

    internal static OntologyValidationResult Fail(params OntologyViolation[] violations) =>
        new(false, violations);
}

/// <summary>Extra context voor de triple-validatie die niet uit de triple zelf
/// blijkt: of de edge als gereïficeerde <see cref="EntityType.Interaction"/>
/// wordt vastgelegd, en hoeveel van deze relatie er al vanuit het subject
/// bestaan (voor de functionele/max-kardinaliteits-toets).</summary>
public sealed record TripleContext(
    bool Reified = false,
    int ExistingOutgoingCount = 0);

/// <summary>Puur, deterministisch schema-orakel (geen IO): valideert een
/// kandidaat-triple <c>(subjectType, relationType, objectType[, context])</c>
/// tegen het ontologie-schema (<see cref="OntologySchema"/>). Toetst: relatie
/// bestaat; subject ∈ domein en object ∈ range mét subclass-overerving;
/// kardinaliteit (functioneel/max); reïficatie-dwang voor gekwalificeerde
/// relaties; en disjointness (bij SUBCLASS_OF-beweringen en bij multi-label).
/// Bewust een poort naast — niet in plaats van — het LLM-oordeel: dit weigert
/// wat schema-technisch onmogelijk is, de LLM/mens beslist over het overige.</summary>
public static class OntologyValidationService
{
    /// <summary>Typed variant: subject/relation/object zijn al opgelost naar
    /// het schema. Zie de string-variant voor rauwe LLM-output.</summary>
    public static OntologyValidationResult ValidateTriple(
        EntityType subjectType, RelationType relationType, EntityType objectType,
        TripleContext? context = null)
    {
        var ctx = context ?? new TripleContext();
        // TryGetValue i.p.v. de indexer: een toekomstige enum-waarde zonder
        // registratie degradeert zo gracieus tot UnknownRelation (zelfde
        // resultaat als het string-pad) i.p.v. een KeyNotFoundException-crash.
        if (!OntologySchema.Relations.TryGetValue(relationType, out var relation))
            return OntologyValidationResult.Fail(new OntologyViolation(
                OntologyViolationCode.UnknownRelation,
                $"Relatie {relationType} is niet in het ontologie-register geregistreerd."));
        var violations = new List<OntologyViolation>();

        // SUBCLASS_OF is de TBox-meta-relatie: bewaak acycliciteit en
        // disjointness i.p.v. de gewone domain/range (alles is een Thing).
        if (relationType == RelationType.SubclassOf)
        {
            ValidateSubclassAssertion(subjectType, objectType, violations);
            return Result(violations);
        }

        // Gekwalificeerde relatie: een kale edge is verboden. Alleen de
        // gereïficeerde vorm (via een Interaction) is toegestaan; de
        // rol-/conditie-edges daarvan worden apart gevalideerd. v0-keuze
        // (bewust): bij Reified=true keuren we hier alleen de kale-edge-dwang
        // goed — de HAS_ROLE/REQUIRES_CONDITION-edges van de Interaction
        // krijgen hun eigen validatie zodra die relaties gemodelleerd zijn.
        if (relation.MustReify)
        {
            if (!ctx.Reified)
                violations.Add(new(OntologyViolationCode.ReificationRequired,
                    $"{relation.EdgeName} is gekwalificeerd en mag niet als kale edge bestaan; " +
                    "reïficeer via een Interaction."));
            return Result(violations);
        }

        // Domain (subclass-polymorf): het subject moet een (subklasse van een)
        // toegestaan domein-type zijn.
        if (relation.Domain.Count > 0 && !relation.Domain.Any(d => OntologySchema.IsA(subjectType, d)))
            violations.Add(new(OntologyViolationCode.DomainMismatch,
                $"{subjectType} valt buiten het domein van {relation.EdgeName} " +
                $"({string.Join("/", relation.Domain)})."));

        // Range (subclass-polymorf).
        if (relation.Range.Count > 0 && !relation.Range.Any(r => OntologySchema.IsA(objectType, r)))
            violations.Add(new(OntologyViolationCode.RangeMismatch,
                $"{objectType} valt buiten de range van {relation.EdgeName} " +
                $"({string.Join("/", relation.Range)})."));

        // Kardinaliteit: functioneel/max — een nieuwe edge mag de bovengrens niet
        // overschrijden gegeven wat er al vanuit het subject uitgaat. v0-keuze
        // (bewust): alleen de bovengrens is triple-lokaal toetsbaar; MinCardinality
        // (1..*) is een completeness-eis over de héle knoop en wordt hier niet
        // afgedwongen — dat hoort bij een latere graaf-brede validatie.
        if (relation.MaxCardinality is int max && ctx.ExistingOutgoingCount >= max)
            violations.Add(new(OntologyViolationCode.CardinalityExceeded,
                $"{relation.EdgeName} staat ten hoogste {max} uitgaande edge(s) per subject toe " +
                $"(al {ctx.ExistingOutgoingCount})."));

        return Result(violations);
    }

    /// <summary>String-variant voor rauwe LLM-output: lost namen tolerant op
    /// (enum-naam of canonieke edge-naam, hoofdletterongevoelig) en geeft
    /// UnknownEntityType/UnknownRelation-schendingen bij onbekende namen — zo
    /// degradeert de poort netjes in plaats van te crashen.</summary>
    public static OntologyValidationResult ValidateTriple(
        string subjectType, string relationName, string objectType,
        TripleContext? context = null)
    {
        var violations = new List<OntologyViolation>();

        var subject = OntologySchema.ParseEntityType(subjectType);
        if (subject is null)
            violations.Add(new(OntologyViolationCode.UnknownEntityType,
                $"Onbekend subject-type '{subjectType}'."));

        var obj = OntologySchema.ParseEntityType(objectType);
        if (obj is null)
            violations.Add(new(OntologyViolationCode.UnknownEntityType,
                $"Onbekend object-type '{objectType}'."));

        var relation = ResolveRelation(relationName);
        if (relation is null)
            violations.Add(new(OntologyViolationCode.UnknownRelation,
                $"Onbekende relatie '{relationName}'."));

        if (subject is null || obj is null || relation is null)
            return Result(violations); // onbekende naam → niet verder toetsen

        return ValidateTriple(subject.Value, relation.Value, obj.Value, context);
    }

    /// <summary>Eén conditie-as van een gereïficeerde interactie (fase 2, #226),
    /// al opgelost naar het schema: op welk concept-type de voorwaarde slaat
    /// (Window/Status/Cost) — de range waartegen §3.1 het qualifier-lexicon sluit.</summary>
    public sealed record ReifiedCondition(EntityType OnConceptType);

    /// <summary>De reïficatie-vorm-poort (fase 2, #226, §2.3): valideert een
    /// gereïficeerde, gekwalificeerde <c>Interaction</c> i.p.v. een kale triple.
    /// Dwingt af (a) dat <paramref name="kind"/> een gereïficeerd-VERPLICHTE
    /// relatie is (COUNTERS/MODIFIES/GRANTS/REQUIRES) — een niet-gekwalificeerde
    /// relatie hoort NIET via een Interaction; (b) dat agent én patient een Card
    /// of Keyword zijn (de HAS_ROLE-range van §2.3); en (c) dat elke conditie op
    /// een geldig concept-type (Window/Status/Cost) slaat. Bewust de tegenpool van
    /// de kale-edge-poort: <see cref="ValidateTriple(EntityType,RelationType,EntityType,TripleContext)"/>
    /// weigert een kale COUNTERS-edge, deze keurt de gereïficeerde vorm ervan goed.</summary>
    public static OntologyValidationResult ValidateReifiedInteraction(
        EntityType agentType, EntityType patientType, RelationType kind,
        IReadOnlyCollection<ReifiedCondition>? conditions = null)
    {
        var violations = new List<OntologyViolation>();

        if (!OntologySchema.Relations.TryGetValue(kind, out var relation))
        {
            violations.Add(new(OntologyViolationCode.UnknownRelation,
                $"Relatie {kind} is niet in het ontologie-register geregistreerd."));
            return Result(violations);
        }

        // (a) Alleen een gekwalificeerde (reïficatie-verplichte) relatie hoort via
        // een Interaction. Een kale relatie reïficeren zou de kale-edge-poort
        // omzeilen — dat is net zo goed structuurverlies-omgekeerd.
        if (!relation.MustReify)
            violations.Add(new(OntologyViolationCode.ReificationRequired,
                $"{relation.EdgeName} is geen gekwalificeerde relatie en hoort niet via een " +
                "Interaction gereïficeerd te worden; gebruik de kale edge."));

        // (b) HAS_ROLE-range (§2.3): agent en patient zijn een Card of Keyword.
        if (!IsRoleFiller(agentType))
            violations.Add(new(OntologyViolationCode.RangeMismatch,
                $"Agent-rol {agentType} valt buiten de HAS_ROLE-range (Card/Keyword)."));
        if (!IsRoleFiller(patientType))
            violations.Add(new(OntologyViolationCode.RangeMismatch,
                $"Patient-rol {patientType} valt buiten de HAS_ROLE-range (Card/Keyword)."));

        // (c) Condities slaan op een gesloten concept-lexicon (Window/Status/Cost).
        foreach (var c in conditions ?? [])
            if (c.OnConceptType is not (EntityType.Window or EntityType.Status or EntityType.Cost))
                violations.Add(new(OntologyViolationCode.RangeMismatch,
                    $"Conditie op {c.OnConceptType} valt buiten het qualifier-lexicon " +
                    "(Window/Status/Cost)."));

        return Result(violations);
    }

    /// <summary>Een geldige HAS_ROLE-filler (§2.3): een Card (elke kaartsubklasse)
    /// of een Keyword.</summary>
    private static bool IsRoleFiller(EntityType type) =>
        OntologySchema.IsA(type, EntityType.Card) || OntologySchema.IsA(type, EntityType.Keyword);

    /// <summary>Valideert de labels op één knoop (multi-label in Neo4j): geen
    /// twee (effectief) disjuncte klassen tegelijk. Vangt bv. een knoop die
    /// zowel Keyword als Mechanic gelabeld is, of zowel Unit als Spell.</summary>
    public static OntologyValidationResult ValidateEntityLabels(IReadOnlyCollection<EntityType> labels)
    {
        var list = labels as IReadOnlyList<EntityType> ?? labels.ToList();
        var violations = new List<OntologyViolation>();
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                if (OntologySchema.AreDisjoint(list[i], list[j]))
                    violations.Add(new(OntologyViolationCode.Disjointness,
                        $"{list[i]} en {list[j]} zijn disjunct en mogen niet samen op één knoop."));
        return Result(violations);
    }

    private static void ValidateSubclassAssertion(
        EntityType sub, EntityType super, List<OntologyViolation> violations)
    {
        // Acyclisch: super mag geen subklasse-of-gelijke van sub zijn (dat zou
        // een cyclus sluiten), en sub ⊑ sub is geen zinnige bewering.
        if (sub == super || OntologySchema.IsA(super, sub))
            violations.Add(new(OntologyViolationCode.SubclassCycle,
                $"SUBCLASS_OF({sub}, {super}) zou de acyclische klassenhiërarchie schenden."));

        // Disjointness: sub zou een subklasse van super worden; dat mag niet als
        // super disjunct is met sub of één van sub's bestaande voorouders.
        if (OntologySchema.AreDisjoint(sub, super))
            violations.Add(new(OntologyViolationCode.Disjointness,
                $"SUBCLASS_OF({sub}, {super}) is onmogelijk: {sub} en {super} zijn disjunct."));
    }

    private static RelationType? ResolveRelation(string relationName)
    {
        if (string.IsNullOrWhiteSpace(relationName)) return null;
        var trimmed = relationName.Trim();
        if (OntologySchema.RelationByEdgeName(trimmed) is { } byEdge) return byEdge.Type;
        // UITSLUITEND een exacte enum-naam: TryParse slikt óók kale getallen
        // ("5") en OR-combinaties ("Invokes,HasMechanic" → Invokes → stil geldig);
        // de naam-gelijkheids-guard verwerpt die als UnknownRelation.
        if (Enum.TryParse<RelationType>(trimmed, ignoreCase: true, out var t)
            && OntologySchema.Relations.ContainsKey(t)
            && t.ToString().Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    private static OntologyValidationResult Result(List<OntologyViolation> violations) =>
        violations.Count == 0 ? OntologyValidationResult.Valid : new(false, violations);
}
