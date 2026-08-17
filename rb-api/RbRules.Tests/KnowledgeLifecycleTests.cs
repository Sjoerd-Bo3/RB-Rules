using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Kennis-levenscyclus (fase 6, #230, §6): het geconsolideerde
/// toestand-vocabulaire + transities, de tier-bewuste staleness-evaluator, de
/// errata-mid-set-flow (SUPERSEDES + afhankelijken-invalidatie) en de kostengegate
/// model-upgrade-schaduw-mine (BESLISSING #232). Puur, geen IO.</summary>
public class KnowledgeLifecycleTests
{
    // ── Toestand-transities (tombstoning i.p.v. hard-delete) ──────────────────
    [Fact]
    public void Transitie_ActiefNaarSuperseded_Mag()
        => Assert.True(LifecycleState.CanTransition(LifecycleState.Active, LifecycleState.Superseded));

    [Fact]
    public void Transitie_SupersededHeropenen_AlleenViaRestore()
    {
        // Nooit stil terug naar Active — dat vereist de expliciete Restored-stap.
        Assert.False(LifecycleState.CanTransition(LifecycleState.Superseded, LifecycleState.Active));
        Assert.True(LifecycleState.CanTransition(LifecycleState.Superseded, LifecycleState.Restored));
        Assert.True(LifecycleState.CanTransition(LifecycleState.Restored, LifecycleState.Active));
    }

    [Fact]
    public void Transitie_GetombsteendMag_NooitNaarOnbekend()
    {
        Assert.True(LifecycleState.CanTransition(LifecycleState.Tombstoned, LifecycleState.Restored));
        Assert.False(LifecycleState.CanTransition(LifecycleState.Tombstoned, "vernietigd"));
        Assert.False(LifecycleState.CanTransition("onzin", LifecycleState.Active));
    }

    // ── Staleness-evaluator (tier-bewust, λ per tier) ─────────────────────────
    private static StalenessEvaluator.Input Base(string tier, int ageDays) => new(
        Tier: tier,
        AssertedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Now: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(ageDays),
        MinedModel: "claude-opus-4-8", CurrentModel: "claude-opus-4-8",
        MinedEmbeddingRev: "bge-m3", CurrentEmbeddingRev: "bge-m3",
        CorroborationNow: 0.9, CorroborationBaseline: 0.9,
        HasNewErrataOnSection: false, NegativeAskSignals: 0);

    [Fact]
    public void Staleness_OfficieelVervaltNietOpLeeftijd()
    {
        // Officieel: λ≈0 → alleen via SUPERSEDES/errata, nooit puur op leeftijd.
        var v = StalenessEvaluator.Evaluate(Base("official", ageDays: 5000));
        Assert.False(v.IsStale);
        Assert.Null(StalenessEvaluator.AgeThresholdDays("official"));
    }

    [Fact]
    public void Staleness_MetaVervaltSnel()
    {
        var v = StalenessEvaluator.Evaluate(Base("meta", ageDays: 45));
        Assert.True(v.IsStale);
        Assert.Contains(RecheckTrigger.AgeThreshold, v.Triggers);
    }

    [Fact]
    public void Staleness_ModelBumpTriggert()
    {
        var input = Base("community", ageDays: 1) with { CurrentModel = "claude-opus-5" };
        var v = StalenessEvaluator.Evaluate(input);
        Assert.Contains(RecheckTrigger.ModelUpgrade, v.Triggers);
    }

    [Fact]
    public void Staleness_ErrataEnCorroboratiedalingTriggeren()
    {
        var input = Base("verified_ruling", ageDays: 1) with
        {
            HasNewErrataOnSection = true,
            CorroborationNow = 0.5,
            CorroborationBaseline = 0.9,
        };
        var v = StalenessEvaluator.Evaluate(input);
        Assert.Contains(RecheckTrigger.NewErrataOnSection, v.Triggers);
        Assert.Contains(RecheckTrigger.CorroborationDrop, v.Triggers);
    }

    // ── Errata-mid-set-flow: SUPERSEDES + afhankelijken-invalidatie ───────────
    [Fact]
    public void Errata_DepreceertRuling_EnInvalideerAfhankelijken()
    {
        var deps = new[]
        {
            new ErrataLifecycle.Dependent("ruling:42", "ruling"),          // de vervangen ruling zelf
            new ErrataLifecycle.Dependent("assertion:01AB", "assertion"),  // afhankelijk feit
            new ErrataLifecycle.Dependent("eval_case:7", "forbidden_claim"), // eval-case op oude bewoording
            new ErrataLifecycle.Dependent("assertion:01AB", "assertion"),  // dubbel → gededupt
        };
        var plan = ErrataLifecycle.Plan_("erratum:12", targetRulingRef: "ruling:42", deps);

        Assert.True(plan.DeprecatesRuling);
        Assert.Equal("ruling:42", plan.TargetRulingRef);
        // De ruling zelf zit NIET in de invalidaties (krijgt een eigen SUPERSEDES-transitie).
        Assert.DoesNotContain(plan.Invalidations, i => i.SubjectRef == "ruling:42");
        // De eval-case (forbidden_claim) vervalt mee.
        Assert.Contains(plan.Invalidations, i => i.SubjectRef == "eval_case:7" && i.FactKind == "forbidden_claim");
        // Dedup: assertion:01AB precies één keer.
        Assert.Single(plan.Invalidations, i => i.SubjectRef == "assertion:01AB");
    }

    [Fact]
    public void Errata_KaartTekstZonderRuling_AlleenAfhankelijken()
    {
        var deps = new[] { new ErrataLifecycle.Dependent("assertion:01AB", "assertion") };
        var plan = ErrataLifecycle.Plan_("erratum:13", targetRulingRef: null, deps);
        Assert.False(plan.DeprecatesRuling);
        Assert.Single(plan.Invalidations);
    }

    // ── Kostengegate model-upgrade-schaduw-mine (BESLISSING #232) ─────────────
    private static ModelUpgradeInvalidation.FactProvenance F(
        string id, string model, bool human = false, bool corr = false)
        => new($"assertion:{id}", "relation", model, human, corr);

    [Fact]
    public void ModelUpgrade_SelecteertAlleenPuurLlmOngesteund()
    {
        var facts = new[]
        {
            F("a", "old"),                       // puur-LLM-ongesteund → selecteren
            F("b", "old", human: true),          // menselijk geverifieerd → behouden
            F("c", "old", corr: true),           // onafhankelijk gecorroboreerd → behouden
            F("d", "new"),                        // ander model → niet geraakt
        };
        var plan = ModelUpgradeInvalidation.Plan_(facts, oldModel: "old",
            new ModelUpgradeInvalidation.Budget(MaxFacts: 100, MaxTokens: 1_000_000, EstTokensPerFact: 1000));

        Assert.Single(plan.Selected);
        Assert.Equal("assertion:a", plan.Selected[0].SubjectRef);
        Assert.Equal(3, plan.Skipped);          // b, c, d bewust behouden
        Assert.False(plan.HasBacklog);
    }

    [Fact]
    public void ModelUpgrade_RespecteertBudgetplafond_RestNaarBacklog()
    {
        var facts = Enumerable.Range(0, 10).Select(i => F($"f{i}", "old")).ToArray();
        // Feit-plafond 4 vs. token-plafond 3 (3000/1000) → laagste (3) begrenst.
        var plan = ModelUpgradeInvalidation.Plan_(facts, oldModel: "old",
            new ModelUpgradeInvalidation.Budget(MaxFacts: 4, MaxTokens: 3_000, EstTokensPerFact: 1000));

        Assert.Equal(3, plan.Selected.Count);
        Assert.Equal(7, plan.Deferred.Count);
        Assert.Equal(10, plan.TotalCandidates);
        Assert.Equal(3000, plan.EstimatedTokens);
        Assert.True(plan.HasBacklog);
        // Deterministische, stabiele volgorde (invoervolgorde) → cycli werken de backlog af.
        Assert.Equal("assertion:f0", plan.Selected[0].SubjectRef);
        Assert.Equal("assertion:f3", plan.Deferred[0].SubjectRef);
    }

    [Fact]
    public void ModelUpgrade_GeenKandidaten_LeegPlan()
    {
        var facts = new[] { F("a", "old", human: true), F("b", "new") };
        var plan = ModelUpgradeInvalidation.Plan_(facts, "old", ModelUpgradeInvalidation.Budget.Default);
        Assert.Empty(plan.Selected);
        Assert.Empty(plan.Deferred);
        Assert.Equal(2, plan.Skipped);
    }
}
