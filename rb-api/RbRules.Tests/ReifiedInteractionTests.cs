using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Fase 2 (#226) — reïficatie &amp; gekwalificeerde relaties. Dekt de
/// reïficatie-vorm-poort (kale COUNTERS afgekeurd), de deterministische
/// promotie-poort (schema ∧ (lexicaal ∨ consensus) ∧ verdict → promoted; cold-start
/// → model_hypothesized_unruled, niet rejected), de conditie-modellering, de
/// herbouwbare RELATES_TO-projectie, de tool-forced extractie-vorm, en de service
/// (persistentie + provenance + tombstone/herstelpad).</summary>
public class ReifiedInteractionTests
{
    // ── Reïficatie-vorm-poort (OntologyValidationService, versla #3) ──────────

    [Fact]
    public void ReifiedInteraction_KeywordCountersKeyword_WithWindow_IsValid()
    {
        var r = OntologyValidationService.ValidateReifiedInteraction(
            EntityType.Keyword, EntityType.Keyword, RelationType.Counters,
            [new(EntityType.Window)]);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void BareCounters_IsRejected_ReificationRequired()
    {
        // Kale (niet-gereïficeerde) gekwalificeerde edge → afgekeurd.
        var bare = OntologyValidationService.ValidateTriple(
            EntityType.Card, RelationType.Counters, EntityType.Card,
            new TripleContext(Reified: false));
        Assert.False(bare.IsValid);
        Assert.Contains(bare.Violations, v => v.Code == OntologyViolationCode.ReificationRequired);

        // Dezelfde relatie mag WÉL gereïficeerd (via een Interaction).
        var reified = OntologyValidationService.ValidateTriple(
            EntityType.Card, RelationType.Counters, EntityType.Card,
            new TripleContext(Reified: true));
        Assert.True(reified.IsValid);
    }

    [Fact]
    public void ReifiedInteraction_NonQualifiedKind_IsRejected()
    {
        // HAS_MECHANIC is geen gekwalificeerde relatie en hoort niet gereïficeerd.
        var r = OntologyValidationService.ValidateReifiedInteraction(
            EntityType.Card, EntityType.Card, RelationType.HasMechanic);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.ReificationRequired);
    }

    [Fact]
    public void ReifiedInteraction_RoleOutOfRange_IsRejected()
    {
        // Een Mechanic is geen geldige HAS_ROLE-filler (Card/Keyword).
        var r = OntologyValidationService.ValidateReifiedInteraction(
            EntityType.Mechanic, EntityType.Keyword, RelationType.Counters);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.RangeMismatch);
    }

    [Fact]
    public void ReifiedInteraction_ConditionOutsideLexicon_IsRejected()
    {
        var r = OntologyValidationService.ValidateReifiedInteraction(
            EntityType.Card, EntityType.Card, RelationType.Modifies,
            [new(EntityType.Phase)]); // Phase ∉ {Window,Status,Cost}
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.RangeMismatch);
    }

    // ── Promotie-poort (puur) ────────────────────────────────────────────────

    private static InteractionGateSignals Signals(
        bool schema = true, string? schemaReason = null, bool lexical = false,
        int consensus = 0, int threshold = 2, bool verdict = true,
        bool cardCard = false, bool tombstone = false) =>
        new(schema, schemaReason, lexical, consensus, threshold, verdict, cardCard, tombstone);

    [Fact]
    public void Gate_SchemaLexicalVerdict_Promotes()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(lexical: true));
        Assert.Equal(InteractionGateOutcome.Promoted, r.Outcome);
        Assert.Equal(InteractionStatus.Promoted, r.Status);
    }

    [Fact]
    public void Gate_SchemaConsensusVerdict_Promotes()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(consensus: 2, threshold: 2));
        Assert.Equal(InteractionGateOutcome.Promoted, r.Outcome);
    }

    [Fact]
    public void Gate_MissingSupport_NonCardCard_IsCandidate()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(consensus: 1, threshold: 2, cardCard: false));
        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);
        Assert.Equal(InteractionStatus.Candidate, r.Status);
    }

    [Fact]
    public void Gate_ColdStart_EmergentCardCard_IsHypothesized_NotRejected()
    {
        // Kritiek Risico 1: emergente card×card zonder steun → NIET verworpen.
        var r = InteractionPromotionGate.Evaluate(Signals(lexical: false, consensus: 0, cardCard: true));
        Assert.Equal(InteractionGateOutcome.ModelHypothesizedUnruled, r.Outcome);
        Assert.Equal(InteractionStatus.ModelHypothesizedUnruled, r.Status);
        Assert.NotEqual(InteractionGateOutcome.Rejected, r.Outcome);
    }

    [Fact]
    public void Gate_SchemaInvalid_IsRejected()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(schema: false, schemaReason: "kale edge", cardCard: true));
        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
    }

    [Fact]
    public void Gate_NegativeVerdict_IsRejected()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(lexical: true, verdict: false));
        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
    }

    [Fact]
    public void Gate_BlockingTombstone_IsRejected_EvenWithFullSupport()
    {
        var r = InteractionPromotionGate.Evaluate(Signals(lexical: true, consensus: 5, tombstone: true));
        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
        Assert.Contains("tombstone", r.StatusReason);
    }

    // ── Kind/status-vocabulaire ──────────────────────────────────────────────

    [Fact]
    public void InteractionKinds_AreExactlyTheReificationRequiredRelations()
    {
        Assert.Equal(
            new[] { "COUNTERS", "MODIFIES", "GRANTS", "REQUIRES" }.OrderBy(x => x),
            InteractionKinds.All.OrderBy(x => x));
        Assert.Equal("COUNTERS", InteractionKinds.Canonicalize("counters"));
        Assert.Null(InteractionKinds.Canonicalize("HAS_MECHANIC"));
        Assert.Null(InteractionKinds.Canonicalize("nonsense"));
    }

    // ── Conditie-modellering & projectie (herbouwbaar) ────────────────────────

    private static Interaction Ix(string status, params InteractionCondition[] conditions) => new()
    {
        AgentRef = "mechanic:Deflect", PatientRef = "mechanic:Assault",
        Kind = InteractionKinds.Counters, Status = status, CreatedByRunId = "run",
        Conditions = [.. conditions],
    };

    private static InteractionCondition Cond(string onKind, string value, string? role = null) =>
        new() { InteractionId = 0, OnKind = onKind, Value = value, SubjectRole = role };

    [Fact]
    public void Projection_SingleWindowCondition_IsCarriedInCache()
    {
        var q = InteractionProjection.ToQualifiers(
            Ix(InteractionStatus.Promoted, Cond(InteractionConditionKinds.Window, "Showdown")));
        Assert.Equal("counters", q.Kind);
        Assert.Equal("Showdown", q.Window);
        Assert.False(q.ReifiedOnly);
        Assert.Equal(1, q.Tier);
    }

    [Fact]
    public void Projection_MultipleConditionsOnAxis_MarkReifiedOnly()
    {
        var q = InteractionProjection.ToQualifiers(Ix(InteractionStatus.Promoted,
            Cond(InteractionConditionKinds.Window, "Showdown"),
            Cond(InteractionConditionKinds.Window, "Combat")));
        Assert.Null(q.Window);       // dubbelzinnig → cache draagt het niet
        Assert.True(q.ReifiedOnly);  // consument moet de reïficatie lezen
    }

    [Fact]
    public void Projection_PatientRoleCondition_MarksReifiedOnly()
    {
        var q = InteractionProjection.ToQualifiers(Ix(InteractionStatus.Promoted,
            Cond(InteractionConditionKinds.Status, "Exhausted", InteractionRoles.Patient)));
        Assert.True(q.ReifiedOnly);
    }

    [Fact]
    public void Projection_IsRebuildableFromInteraction_Deterministic()
    {
        var ix = Ix(InteractionStatus.Promoted, Cond(InteractionConditionKinds.Window, "Showdown"));
        // Twee keer projecteren uit dezelfde bron → identiek (cache is een functie
        // van de reïficatie, nooit andersom).
        Assert.Equal(InteractionProjection.ToQualifiers(ix), InteractionProjection.ToQualifiers(ix));
    }

    [Fact]
    public void Projection_Rows_SkipRejected_CacheOnlyForAnchored()
    {
        var promoted = Ix(InteractionStatus.Promoted, Cond(InteractionConditionKinds.Window, "Showdown"));
        var candidate = Ix(InteractionStatus.Candidate);
        candidate.AgentRef = "card:a"; candidate.PatientRef = "card:b";
        var rejected = Ix(InteractionStatus.Rejected);
        rejected.AgentRef = "card:c"; rejected.PatientRef = "card:d";

        var rows = InteractionProjection.BuildProjectionRows([promoted, candidate, rejected]);
        Assert.Equal(2, rows.Nodes.Count);          // rejected overgeslagen
        Assert.Single(rows.RelatesToCache);         // alleen de promoted zaait cache
        Assert.Single(rows.ConditionNodes);
    }

    // ── Tool-forced extractie-vorm (§3.1) ─────────────────────────────────────

    private static ExtractionVocab Vocab() => new(
        Refs:
        [
            new("card:a", "Alpha", EntityType.Card),
            new("card:b", "Beta", EntityType.Card),
        ],
        WindowLexicon: ["Showdown"],
        StatusLexicon: ["Exhausted"]);

    [Fact]
    public void Extraction_ToolSchema_ClosesEnumsOnVocabulary()
    {
        var schema = InteractionExtraction.BuildToolSchema(Vocab());
        Assert.Contains("emit_interactions", schema);
        Assert.Contains("COUNTERS", schema);
        Assert.Contains("card:a", schema);
        Assert.Contains("Showdown", schema);
    }

    [Fact]
    public void Extraction_Parse_DropsOutOfVocabRefsAndKinds()
    {
        var raw = """
        {"interactions":[
          {"from":"card:a","to":"card:b","kind":"COUNTERS","interacts":true,
           "conditions":[{"on_kind":"WINDOW","window":"Showdown"}]},
          {"from":"card:a","to":"card:ZZZ","kind":"COUNTERS","interacts":true},
          {"from":"card:a","to":"card:b","kind":"HAS_MECHANIC","interacts":true}
        ]}
        """;
        var parsed = InteractionExtraction.Parse(raw, Vocab());
        Assert.Single(parsed);                             // alleen de eerste overleeft
        Assert.Equal("COUNTERS", parsed[0].Kind);
        Assert.Single(parsed[0].Conditions);
        Assert.Equal("Showdown", parsed[0].Conditions[0].Value);
    }

    [Fact]
    public void Extraction_Parse_DropsLexiconViolatingCondition()
    {
        var raw = """
        {"interactions":[
          {"from":"card:a","to":"card:b","kind":"MODIFIES","interacts":true,
           "conditions":[{"on_kind":"WINDOW","window":"NotAWindow"}]}
        ]}
        """;
        var parsed = InteractionExtraction.Parse(raw, Vocab());
        Assert.Single(parsed);
        Assert.Empty(parsed[0].Conditions);                // conditie viel weg
    }

    [Fact]
    public void Extraction_Parse_CarriesInteractsVerdict_BothPolarities()
    {
        // Het interacts-verdict wordt de promotie-blokkerende LlmVerdictInteracts —
        // beide polariteiten moeten trouw doorkomen (mutatie die het hardcodet zou
        // elke "geen interactie" stil positief maken).
        var raw = """
        {"interactions":[
          {"from":"card:a","to":"card:b","kind":"COUNTERS","interacts":true},
          {"from":"card:b","to":"card:a","kind":"MODIFIES","interacts":false}
        ]}
        """;
        var parsed = InteractionExtraction.Parse(raw, Vocab());
        Assert.Equal(2, parsed.Count);
        Assert.True(parsed[0].Interacts);
        Assert.False(parsed[1].Interacts);
    }

    // ── #251-review: vorm-fout ≠ leeg antwoord ──────────────────────────────────
    // Een afgekapte body of schema-drift werd stil tot [] gereduceerd; de mining-lus
    // telde dat als 'geldig, leeg' (geslaagd werk) en de uitvalmeting van #251 zag
    // parse-fouten dus structureel niet. AiCallOutcome.Unparseable bestond wel maar
    // was vanuit dit pad onbereikbaar.
    [Theory]
    [InlineData("""{"interactions":[{"from":"card:a","to":"card:b" """)]   // afgekapt
    [InlineData("""{"interactions":"none"}""")]                            // schema-drift
    [InlineData("""{"resultaat":"geen"}""")]                               // envelop mist
    [InlineData("")]                                                        // niets
    public void Extraction_ParseDetailed_KapotteEnvelop_IsMalformed(string raw)
    {
        var parsed = InteractionExtraction.ParseDetailed(raw, Vocab());

        Assert.True(parsed.Malformed);
        Assert.Empty(parsed.Items);
    }

    [Theory]
    [InlineData("""{"interactions":[]}""")]
    [InlineData("[]")]
    public void Extraction_ParseDetailed_GeldigeLegeEnvelop_IsGeenVormFout(string raw)
    {
        var parsed = InteractionExtraction.ParseDetailed(raw, Vocab());

        Assert.False(parsed.Malformed);   // "het model wist niets" ≠ "het model gaf onzin"
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public void Extraction_ParseDetailed_ItemsBuitenHetVocabulaire_BlijvenGeldigWerk()
    {
        // De envelop klopt; alleen de INHOUD haalt de tweede muur niet. Dat is geen
        // vorm-fout — anders zou elke te-strenge poort als rb-ai-uitval tellen.
        var raw = """{"interactions":[{"from":"card:a","to":"card:ZZZ","kind":"COUNTERS"}]}""";

        var parsed = InteractionExtraction.ParseDetailed(raw, Vocab());

        Assert.False(parsed.Malformed);
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public void Extraction_Parse_DropsSelfLoop()
    {
        // from == to (een kaart die zichzelf countert) is een write-guard-schending
        // en moet vallen.
        var raw = """
        {"interactions":[
          {"from":"card:a","to":"card:a","kind":"COUNTERS","interacts":true}
        ]}
        """;
        var parsed = InteractionExtraction.Parse(raw, Vocab());
        Assert.Empty(parsed);
    }

    [Fact]
    public void RelationTypeConstraint_AllowsCardCardCounters_RejectsBareStructural()
    {
        Assert.True(RelationTypeConstraint.Allows(EntityType.Card, "COUNTERS", EntityType.Card));
        Assert.True(RelationTypeConstraint.Allows(EntityType.Keyword, "GRANTS", EntityType.Card));
        Assert.False(RelationTypeConstraint.Allows(EntityType.Card, "HAS_MECHANIC", EntityType.Mechanic));
        Assert.False(RelationTypeConstraint.Allows(EntityType.Mechanic, "COUNTERS", EntityType.Card));
    }

    // ── BrainRef round-trip ──────────────────────────────────────────────────

    [Fact]
    public void BrainRef_InteractionAndCondition_RoundTrip()
    {
        Assert.Equal("interaction:42", BrainRef.Interaction(42).Format());
        Assert.Equal("condition:7", BrainRef.Condition(7).Format());
        Assert.True(BrainRef.TryParse("interaction:42", out var ix));
        Assert.Equal(BrainRefKind.Interaction, ix.Kind);
        Assert.True(BrainRef.TryParse("condition:7", out var c));
        Assert.Equal(BrainRefKind.Condition, c.Kind);
    }

    // ── Service (Postgres = SoT; InMemory) ────────────────────────────────────

    private static async Task<string> SeedRunAsync(RbRulesDbContext db)
    {
        var run = new MiningRun { Id = Ulid.NewUlid(), Kind = FactKinds.Interaction, LlmModel = "claude-opus-4-8" };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static InteractionPromotionRequest Req(
        EntityType agentType = EntityType.Card, EntityType patientType = EntityType.Card,
        string kind = "COUNTERS", bool lexical = false, int consensus = 0, bool verdict = true,
        string agent = "card:a", string patient = "card:b",
        IReadOnlyList<InteractionConditionInput>? conditions = null,
        string? governedBy = null) =>
        new(agent, agentType, patient, patientType, kind,
            DerivedFromRef: governedBy ?? "section:core-rules-pdf/7.4",
            GovernedByRef: governedBy,
            Conditions: conditions ?? [],
            LexicalSupport: lexical, ConsensusCount: consensus, LlmVerdictInteracts: verdict);

    [Fact]
    public async Task Service_PromotedPath_WritesInteractionAssertionDecision()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(
            lexical: true, governedBy: "section:core-rules-pdf/7.4",
            conditions: [new(InteractionConditionKinds.Window, null, "Showdown", "equals")]), runId);

        Assert.Equal(InteractionGateOutcome.Promoted, r.Outcome);
        var ix = await db.Interactions.Include(x => x.Conditions).SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
        Assert.NotNull(ix.PromotedAt);
        Assert.Single(ix.Conditions);
        Assert.Equal("Showdown", ix.Conditions[0].Value);

        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal($"interaction:{ix.Id}", assertion.Subject);
        Assert.Equal(FactKinds.Interaction, assertion.FactKind);
        Assert.Equal(runId, assertion.MiningRunId);

        var decision = await db.InteractionDecisions.SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, decision.Outcome);
        Assert.False(await db.RejectionTombstones.AnyAsync());
    }

    [Fact]
    public async Task Service_KeywordPairNoSupport_IsCandidate_NoTombstone()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(
            agentType: EntityType.Keyword, patientType: EntityType.Keyword,
            agent: "mechanic:Deflect", patient: "mechanic:Assault"), runId);

        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Candidate, ix.Status);
        Assert.False(await db.RejectionTombstones.AnyAsync());
    }

    [Fact]
    public async Task Service_EmergentCardCard_IsModelHypothesized_NotRejected()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(lexical: false, consensus: 0, verdict: true), runId);

        Assert.Equal(InteractionGateOutcome.ModelHypothesizedUnruled, r.Outcome);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.ModelHypothesizedUnruled, ix.Status);
        // Cold-start: geparkeerd, geen tombstone (niet weggegooid).
        Assert.False(await db.RejectionTombstones.AnyAsync());
    }

    [Fact]
    public async Task Service_NegativeVerdict_WritesTombstone_NoGraphNode()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(lexical: true, verdict: false), runId);

        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
        Assert.False(await db.Interactions.AnyAsync());     // geen interactie-knoop
        var tomb = await db.RejectionTombstones.SingleAsync();
        Assert.Equal("gate", tomb.Actor);
        Assert.False(tomb.Lifted);
    }

    [Fact]
    public async Task Service_Tombstone_BlocksReopening_UntilLifted()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        // 1. Verwerp → tombstone.
        await svc.PromoteAsync(Req(lexical: true, verdict: false), runId);

        // 2. Zelfs met volledige steun blijft het verworpen (stil-heropenen geblokkeerd).
        var blocked = await svc.PromoteAsync(Req(lexical: true, consensus: 5, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Rejected, blocked.Outcome);
        Assert.Contains("tombstone", blocked.StatusReason);
        Assert.False(await db.Interactions.AnyAsync());

        // 3. Herstelpad: hef de tombstone op (expliciete beheerdersactie).
        var key = InteractionDedupe.Key("card:a", "card:b", "COUNTERS");
        var lifted = await svc.LiftTombstonesAsync(key);
        Assert.Equal(1, lifted);
        Assert.True((await db.RejectionTombstones.SingleAsync()).Lifted);

        // 4. Nu promoveert dezelfde interactie wél.
        var reopened = await svc.PromoteAsync(Req(lexical: true, consensus: 5, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Promoted, reopened.Outcome);
        Assert.Equal(InteractionStatus.Promoted, (await db.Interactions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Service_NonReifiableKind_IsRejected_ReificationDwang()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        // Een niet-gekwalificeerde relatie kan geen gereïficeerde interactie worden.
        var r = await svc.PromoteAsync(Req(kind: "HAS_MECHANIC", lexical: true), runId);
        Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
        Assert.Contains("schema", r.StatusReason);
    }

    [Fact]
    public async Task Service_Repromotion_IsIdempotent_RebuildsConditions()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        await svc.PromoteAsync(Req(lexical: true,
            conditions: [new(InteractionConditionKinds.Window, null, "Showdown", null)]), runId);
        await svc.PromoteAsync(Req(lexical: true,
            conditions: [new(InteractionConditionKinds.Status, InteractionRoles.Agent, "Exhausted", null)]), runId);

        var ix = await db.Interactions.Include(x => x.Conditions).SingleAsync();
        Assert.Single(await db.Interactions.ToListAsync());   // geen duplicaat
        Assert.Single(ix.Conditions);
        Assert.Equal(InteractionConditionKinds.Status, ix.Conditions[0].OnKind);
    }

    // ── Grafsteen alleen bij duurzaam-gegronde verwerping (#226-review #1) ────

    [Fact]
    public async Task Service_LoneNegativeVerdict_NoTombstone_DoesNotPermanentlyBlock()
    {
        // Een kaal negatief LLM-verdict (geen lexicale/consensus-steun) mag de
        // sleutel niet permanent sluiten (rode draad: LLM-alleen draagt nooit een
        // blijvende destructieve actie). Geen grafsteen → een latere, volledig
        // gesteunde run promoveert alsnog.
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var rejected = await svc.PromoteAsync(
            Req(agentType: EntityType.Keyword, patientType: EntityType.Keyword,
                agent: "mechanic:Deflect", patient: "mechanic:Assault",
                lexical: false, consensus: 0, verdict: false), runId);
        Assert.Equal(InteractionGateOutcome.Rejected, rejected.Outcome);
        Assert.False(await db.RejectionTombstones.AnyAsync());   // GEEN grafsteen

        var reopened = await svc.PromoteAsync(
            Req(agentType: EntityType.Keyword, patientType: EntityType.Keyword,
                agent: "mechanic:Deflect", patient: "mechanic:Assault",
                lexical: true, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Promoted, reopened.Outcome);
    }

    [Fact]
    public async Task Service_SchemaRejection_NoTombstone_AllowsLaterPromotion()
    {
        // Een schema/structuur-fout (hier: verkeerd geresolvede agent-EntityType →
        // rol-range faalt) is transiënt en mag geen permanente grafsteen achterlaten.
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var rejected = await svc.PromoteAsync(
            Req(agentType: EntityType.Mechanic, patientType: EntityType.Card,
                agent: "mechanic:Deflect", patient: "card:b",
                lexical: true, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Rejected, rejected.Outcome);
        Assert.Contains("schema", rejected.StatusReason);
        Assert.False(await db.RejectionTombstones.AnyAsync());   // GEEN grafsteen

        // Entity-resolution gecorrigeerd (agent is toch een Card) → dezelfde sleutel
        // promoveert nu, niet geblokkeerd door een grafsteen.
        var reopened = await svc.PromoteAsync(
            Req(agentType: EntityType.Card, patientType: EntityType.Card,
                agent: "mechanic:Deflect", patient: "card:b",
                lexical: true, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Promoted, reopened.Outcome);
    }

    // ── Eerlijke Assertion-labels per tier (#226-review #5) ───────────────────

    [Fact]
    public async Task Service_HypothesizedTier_AssertionIsNotLabelledSupported()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(lexical: false, consensus: 0, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.ModelHypothesizedUnruled, r.Outcome);

        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal("HYPOTHESIZED", assertion.Verdict);
        Assert.Equal("llm", assertion.Verifier);
        Assert.NotEqual("SUPPORTED", assertion.Verdict);   // geen overdreven verificatie
    }

    [Fact]
    public async Task Service_CandidateTier_AssertionIsLabelledCandidate()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(
            agentType: EntityType.Keyword, patientType: EntityType.Keyword,
            agent: "mechanic:Deflect", patient: "mechanic:Assault",
            lexical: false, consensus: 1, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Candidate, r.Outcome);

        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal("CANDIDATE", assertion.Verdict);
        Assert.Equal("llm", assertion.Verifier);
    }

    [Fact]
    public async Task Service_PromotedViaConsensus_AssertionVerifierReflectsConsensus()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        var r = await svc.PromoteAsync(Req(lexical: false, consensus: 2, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Promoted, r.Outcome);

        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal("SUPPORTED", assertion.Verdict);
        Assert.Equal("llm+consensus", assertion.Verifier);   // geen lexicale steun geclaimd
    }

    // ── Demotie-tak van RejectAsync (#226-review #6) ──────────────────────────

    [Fact]
    public async Task Service_RejectAfterPromotion_DemotesExistingNode_ClearsPromotedAt()
    {
        await using var db = NewDb();
        var runId = await SeedRunAsync(db);
        var svc = new InteractionPromotionService(db);

        // 1. Promoveer.
        var promoted = await svc.PromoteAsync(Req(lexical: true, verdict: true), runId);
        Assert.Equal(InteractionGateOutcome.Promoted, promoted.Outcome);
        var before = await db.Interactions.SingleAsync();
        Assert.NotNull(before.PromotedAt);
        var interactionId = before.Id;

        // 2. Latere run met deterministische steun maar negatief verdict → verwerpen.
        //    De bestaande knoop wordt gedemote (status → rejected, PromotedAt gewist)
        //    en er verschijnt een grafsteen + beslissings-memo.
        var rejected = await svc.PromoteAsync(Req(lexical: true, verdict: false), runId);
        Assert.Equal(InteractionGateOutcome.Rejected, rejected.Outcome);
        Assert.Equal(interactionId, rejected.InteractionId);

        var after = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Rejected, after.Status);
        Assert.Null(after.PromotedAt);                        // geen misleidende timestamp
        Assert.True(await db.RejectionTombstones.AnyAsync(t => !t.Lifted));

        var decisions = await db.InteractionDecisions
            .Where(d => d.InteractionId == interactionId).ToListAsync();
        Assert.Contains(decisions, d => d.Outcome == InteractionStatus.Rejected);
    }

    // ── InMemory-context (pgvector als tekst, zoals de andere service-tests) ──
    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; de service draait er wél in (Postgres) —
            // voor de test volstaat negeren (zelfde patroon als de andere service-tests).
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }
}
