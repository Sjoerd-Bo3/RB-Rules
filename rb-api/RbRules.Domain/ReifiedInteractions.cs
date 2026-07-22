using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Fase 2 (#226) — de gereïficeerde, gekwalificeerde interactie: de
/// canonieke opslagvorm van elk COUNTERS/MODIFIES/GRANTS/REQUIRES-feit
/// (docs/ARCHITECTURE brein-epic §2.3). Versla faalmodus #3 (structuurverlies):
/// een kale <c>(:Card)-[:COUNTERS]->(:Card)</c>-edge is verboden (reïficatie-dwang,
/// <see cref="OntologyValidationService.ValidateReifiedInteraction"/>); condities
/// (window/status/cost) leven als losse, individueel weerlegbare
/// <see cref="InteractionCondition"/>-knopen i.p.v. platgeslagen in vrije proza.
///
/// Postgres is de bron van waarheid (SoT). De Neo4j-projectie (<c>:Interaction</c>/
/// <c>:Condition</c> + de gedenormaliseerde <c>RELATES_TO</c>-qualifier-cache) is
/// idempotent herbouwbaar uit deze rij — de cache is NOOIT de bron.
///
/// De rollen agent/patient zijn <see cref="BrainRef"/>'s naar een Card of Mechanic (#304)
/// (n-aire relatie met expliciete rol i.p.v. een richtingsloze binaire edge).
/// <see cref="Kind"/> is de SCREAMING_SNAKE_CASE-edge-naam van een
/// gereïficeerd-verplicht ontologie-relatietype (<see cref="RelationTraits.RequiresReification"/>).</summary>
public class Interaction
{
    public long Id { get; set; }

    /// <summary>HAS_ROLE {role:'agent'} — de handelende kant (BrainRef naar Card
    /// of Mechanic), bv. "mechanic:Deflect" of "card:ogn-011-298".</summary>
    public required string AgentRef { get; set; }

    /// <summary>HAS_ROLE {role:'patient'} — de ondergane kant (BrainRef naar Card
    /// of Mechanic).</summary>
    public required string PatientRef { get; set; }

    /// <summary>De gekwalificeerde relatie als canonieke edge-naam
    /// (<see cref="InteractionKinds"/>): COUNTERS | MODIFIES | GRANTS | REQUIRES.
    /// Precies de vier <see cref="RelationTraits.RequiresReification"/>-relaties —
    /// een kale edge hiervan is verboden, alleen deze reïficatie draagt ze.</summary>
    public required string Kind { get; set; }

    /// <summary>GOVERNED_BY (optioneel): BrainRef naar de normatieve RuleSection
    /// die de interactie verankert ("section:core-rules-pdf/7.4"). Null zolang de
    /// interactie nog niet officieel verankerd is (bv. een emergente hypothese).</summary>
    public string? GovernedByRef { get; set; }

    /// <summary>Levenscyclus-tier (<see cref="InteractionStatus"/>):
    /// candidate | verified | promoted | rejected | model_hypothesized_unruled.
    /// De promotie-poort (<see cref="InteractionPromotionGate"/>) bepaalt de tier
    /// deterministisch; een LLM-verdict draagt NOOIT alléén de promotie.</summary>
    public string Status { get; set; } = InteractionStatus.Candidate;

    /// <summary>Poort-uitslag als leesbare memo (zégt welke poort faalde/slaagde) —
    /// zichtbaar in de reviewqueue/inzicht-thread (#236). Bv. "consensus 1/2, wacht
    /// op corroboratie" of "schema-ok; bewijszin gevonden; LLM-verdict positief".</summary>
    public string? StatusReason { get; set; }

    /// <summary>0a-provenance (#233): de <see cref="MiningRun"/> die deze interactie
    /// aandroeg. De feit→herkomst-koppeling loopt bovendien via een
    /// <see cref="Assertion"/> met subject <c>interaction:{Id}</c>.</summary>
    public required string CreatedByRunId { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Wanneer de interactie promoveerde (Status → promoted); null zolang
    /// ze kandidaat/hypothese/afgewezen is.</summary>
    public DateTimeOffset? PromotedAt { get; set; }

    /// <summary>Provenance op de RIJ (#323, #299-les: run_log-regels verouderen —
    /// "welke rijen komen uit welk model?" moet een <c>WHERE</c> zijn): het
    /// rb-ai-model-ID (bv. "claude-fable-5") van de extractie die deze rij het
    /// laatst aandroeg. Null = het sonnet-tijdperk vóór #323. Wordt bij ELKE
    /// nieuwe extractie (her)geschreven, zodat de audit (#313) per model kan
    /// vergelijken.</summary>
    public string? ExtractModel { get; set; }

    /// <summary>1-based positie van de bronkaart binnen de rb-ai-batchsessie
    /// (#323). 1 bij een losse aanroep; null in het pre-#323-tijdperk. Dit is
    /// het meetkanaal voor aandachts-verval bij latere kaarten in een batch —
    /// zonder per-positie-cijfer is "K werkt" niet te claimen (issue-rand).</summary>
    public int? ExtractBatchPosition { get; set; }

    /// <summary>Individueel weerlegbare condities (window/status/cost).</summary>
    public List<InteractionCondition> Conditions { get; set; } = [];

    public BrainRef Ref => BrainRef.Interaction(Id);

    /// <summary>Genormaliseerde, richting-plus-kind-unieke dedupe-sleutel
    /// (agent|patient|kind) — deelt de vorm met <see cref="RejectionTombstone.DedupeKey"/>
    /// zodat een verworpen interactie niet als nieuwe kandidaat heropent.</summary>
    public string DedupeKey => InteractionDedupe.Key(AgentRef, PatientRef, Kind);
}

/// <summary>Fase 2 (#226) — een gereïficeerde voorwaarde op een
/// <see cref="Interaction"/> (§2.3). Elke conditie is een eigen knoop met een
/// expliciete <see cref="SubjectRole"/> (op wie de voorwaarde slaat) zodat
/// meervoudige condities zonder structuurverlies naast elkaar bestaan en
/// afzonderlijk weerlegbaar zijn (retire één conditie → de interactie blijft,
/// zwakker gekwalificeerd).</summary>
public class InteractionCondition
{
    public long Id { get; set; }

    public required long InteractionId { get; set; }
    public Interaction? Interaction { get; set; }

    /// <summary>De conditie-as (<see cref="InteractionConditionKinds"/>):
    /// WINDOW (timing, bv. Showdown) | STATUS (toestand, bv. Exhausted) |
    /// COST (kosten-floor/-delta, bv. reduce damage floor 0).</summary>
    public required string OnKind { get; set; }

    /// <summary>Op wie de voorwaarde slaat (<see cref="InteractionRoles"/>):
    /// agent | patient. Null = op de interactie als geheel (bv. een window geldt
    /// voor de hele interactie, niet voor één rol).</summary>
    public string? SubjectRole { get; set; }

    /// <summary>De genormaliseerde waarde uit het gesloten qualifier-lexicon:
    /// WINDOW → een Window-label ("Showdown"), STATUS → een Status-label
    /// ("Exhausted"), COST → een cost-descriptor ("reduce:damage:floor=0"). De
    /// LLM kan geen waarde buiten het lexicon noemen (tool-forcing, §3.1).</summary>
    public required string Value { get; set; }

    /// <summary>Optionele operator uit §2.3 (equals | lte | reduce | …) — de
    /// semantiek van <see cref="Value"/>. Null = simpele gelijkheid.</summary>
    public string? Operator { get; set; }

    public BrainRef Ref => BrainRef.Condition(Id);
}

/// <summary>Fase 2 (#226) — het herstelpad tegen stil-heropenen (rode draad #236,
/// §3.4). Een door de poort/mens verworpen interactie laat een tombstone achter
/// op zijn <see cref="DedupeKey"/> (agent|patient|kind); die overleeft runs én
/// model-upgrades. Een nieuw model mag de relatie niet stil heropenen — dat
/// vereist een expliciete beheerdersactie ("herbeoordeel de rejects",
/// <see cref="RestorePath"/>). Nooit een hard-delete: de grafsteen ís het
/// herstelpad.</summary>
public class RejectionTombstone
{
    public long Id { get; set; }

    /// <summary>agent|patient|kind (<see cref="InteractionDedupe.Key"/>) — de
    /// sleutel die de poort raadpleegt vóór ze een kandidaat overweegt.</summary>
    public required string DedupeKey { get; set; }

    public required string AgentRef { get; set; }
    public required string PatientRef { get; set; }
    public required string Kind { get; set; }

    /// <summary>Waarom verworpen (poort-uitslag of admin-motivatie) — de memo
    /// tegen flip-flop.</summary>
    public required string Reason { get; set; }

    /// <summary>Wie/wat verwierp: "gate" (deterministische poort) of "admin".</summary>
    public required string Actor { get; set; }

    /// <summary>0a-provenance (#233): de run die de verwerping vastlegde.</summary>
    public required string RunId { get; set; }

    /// <summary>Het expliciete herstelpad: hoe een beheerder de rejectie opheft
    /// ("admin: herbeoordeel rejects"). Tekstueel, want de actie is bewust
    /// handmatig — geen automatische heropening.</summary>
    public string RestorePath { get; set; } = "admin:reappraise-rejects";

    /// <summary>Opgeheven (unconsolidate): de tombstone blijft als audit-spoor
    /// bestaan, maar blokkeert de dedupe-sleutel niet langer.</summary>
    public bool Lifted { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Fase 2 (#226) — de promotie-BESLISSING als first-class knoop (rode
/// draad #236: geen promoverende/destructieve actie zonder expliciete memo en
/// herstelpad). Legt de poort-uitslag vast (welke signalen vuurden) zodat de
/// beheerder van beoordelaar naar arbiter verschuift en de beslissing zichtbaar
/// is in inzicht-thread #236 — niets levert onzichtbare state.</summary>
public class InteractionDecision
{
    public long Id { get; set; }

    public required long InteractionId { get; set; }

    /// <summary>De poort-uitslag (<see cref="InteractionGateOutcome"/> als string):
    /// promoted | candidate | model_hypothesized_unruled | rejected.</summary>
    public required string Outcome { get; set; }

    /// <summary>De memo: welke poorten vuurden (schema/lexicaal/consensus/verdict)
    /// en waarom deze uitslag.</summary>
    public required string Memo { get; set; }

    /// <summary>0a-provenance (#233): de run die de poort draaide.</summary>
    public required string RunId { get; set; }

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>De ENIGE toegestane <see cref="Interaction.Kind"/>-waarden — precies de
/// vier gereïficeerd-verplichte ontologie-relaties (§2.2). Eén bron zodat service,
/// poort, projectie en tests niet uiteenlopen; afgeleid uit — en gevalideerd
/// tegen — <see cref="OntologySchema"/> (<see cref="RelationTraits.RequiresReification"/>).</summary>
public static class InteractionKinds
{
    public const string Counters = "COUNTERS";
    public const string Modifies = "MODIFIES";
    public const string Grants = "GRANTS";
    public const string Requires = "REQUIRES";

    /// <summary>De reïficatie-verplichte edge-namen, dynamisch uit de ontologie
    /// (nooit een losse hardcoded lijst die uit sync raakt). Volgorde-stabiel op
    /// enum-declaratie voor deterministische prompt-enums.</summary>
    public static readonly IReadOnlyList<string> All =
        Enum.GetValues<RelationType>()
            .Select(t => OntologySchema.Relations[t])
            .Where(r => r.MustReify)
            .Select(r => r.EdgeName)
            .ToList();

    /// <summary>Canoniek (hoofdletter-genormaliseerd) kind, of <c>null</c> als het
    /// geen gereïficeerd-verplichte relatie is — de poort weigert dan i.p.v. te
    /// gokken (dezelfde tolerante-maar-strikte lijn als <see cref="OntologySchema.ParseEntityType"/>).</summary>
    public static string? Canonicalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var relation = OntologySchema.RelationByEdgeName(kind.Trim());
        return relation is { MustReify: true } ? relation.EdgeName : null;
    }

    public static bool IsValid(string? kind) => Canonicalize(kind) is not null;
}

/// <summary>Canonieke <see cref="Interaction.Status"/>-tiers (§3.4). Bevat de
/// cold-start-tier <see cref="ModelHypothesizedUnruled"/>: een emergente
/// card×card-hypothese zonder lexicale/consensus-steun wordt NIET verworpen maar
/// hier geparkeerd (eigen trust-label, micro-reviewqueue) — nooit stil weggegooid
/// (kritiek Risico 1).</summary>
public static class InteractionStatus
{
    public const string Candidate = "candidate";
    public const string Verified = "verified";
    public const string Promoted = "promoted";
    public const string Rejected = "rejected";
    public const string ModelHypothesizedUnruled = "model_hypothesized_unruled";

    public static readonly IReadOnlyList<string> All =
        [Candidate, Verified, Promoted, Rejected, ModelHypothesizedUnruled];

    public static bool IsValid(string? status) => status is not null && All.Contains(status);

    /// <summary>Sterkte-orde voor de demotiegarantie (#313, verbreed in #332):
    /// <see cref="Verified"/> (menselijk/ruling-geverifieerd; nog geen automatische
    /// schrijver) staat bóven <see cref="Promoted"/> (poort-promotie); alles
    /// daaronder — <see cref="Candidate"/>, <see cref="ModelHypothesizedUnruled"/>,
    /// <see cref="Rejected"/> — is een VOORSTEL-toestand en telt als gelijk-zwak.
    /// De werk-tiers zijn onderling bewust NIET geordend: candidate ↔ hypothese ↔
    /// verwerping is het normale pipeline-verloop (corroboratie, soft-rejects,
    /// tombstones), geen demotie van een vastgesteld feit. De promotie-poort
    /// gebruikt deze orde om een bestaande rij nooit automatisch te VERLAGEN —
    /// degradaties komen uit de audit + reviewqueue, nooit uit de poort zelf.
    /// Onbekend/null telt als zwakst (beschermt niets, kan zichzelf herstellen).</summary>
    public static int Strength(string? status) => status switch
    {
        Verified => 2,
        Promoted => 1,
        _ => 0,
    };
}

/// <summary>De conditie-assen (<see cref="InteractionCondition.OnKind"/>) — het
/// gesloten qualifier-lexicon uit §2.3. Elke as mapt op een ontologie-concept-type
/// zodat de waarde tegen een gesloten lexicon valideerbaar is.</summary>
public static class InteractionConditionKinds
{
    public const string Window = "WINDOW";
    public const string Status = "STATUS";
    public const string Cost = "COST";

    public static readonly IReadOnlyList<string> All = [Window, Status, Cost];

    /// <summary>Het ontologie-<see cref="EntityType"/> dat de waarde van deze
    /// conditie-as bewoont (WINDOW → Window-concept, STATUS → Status-concept,
    /// COST → Cost-concept) — de range waartegen §3.1 het lexicon sluit.</summary>
    public static EntityType? ConceptType(string? onKind) => Canonicalize(onKind) switch
    {
        Window => EntityType.Window,
        Status => EntityType.Status,
        Cost => EntityType.Cost,
        _ => null,
    };

    public static string? Canonicalize(string? onKind)
    {
        if (string.IsNullOrWhiteSpace(onKind)) return null;
        var upper = onKind.Trim().ToUpperInvariant();
        return All.Contains(upper) ? upper : null;
    }

    public static bool IsValid(string? onKind) => Canonicalize(onKind) is not null;
}

/// <summary>De n-aire rollen (§2.3): agent (handelend) en patient (ondergaand).
/// Gebruikt zowel op <see cref="Interaction"/>-rollen als op
/// <see cref="InteractionCondition.SubjectRole"/>.</summary>
public static class InteractionRoles
{
    public const string Agent = "agent";
    public const string Patient = "patient";

    public static readonly IReadOnlyList<string> All = [Agent, Patient];

    public static bool IsValid(string? role) => role is not null && All.Contains(role);
}

/// <summary>Genormaliseerde dedupe-sleutel voor interacties en hun tombstones:
/// <c>agent|patient|kind</c>. Gericht (agent≠patient-volgorde blijft betekenisvol —
/// "A countert B" ≠ "B countert A"), kind hoofdletter-genormaliseerd, refs
/// getrimd. Eén bron zodat <see cref="Interaction.DedupeKey"/> en
/// <see cref="RejectionTombstone.DedupeKey"/> nooit uiteenlopen.</summary>
public static class InteractionDedupe
{
    public static string Key(string agentRef, string patientRef, string kind) =>
        $"{(agentRef ?? "").Trim()}|{(patientRef ?? "").Trim()}|{(kind ?? "").Trim().ToUpperInvariant()}";
}
