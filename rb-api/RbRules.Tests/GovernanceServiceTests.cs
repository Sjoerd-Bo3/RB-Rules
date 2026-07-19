using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De IO-schil rond de governance/levenscyclus-poorten (fase 6, #230):
/// <see cref="OntologyGovernanceService"/> (versie-historie, staging-reviewqueue) en
/// <see cref="KnowledgeLifecycleService"/> (geconsolideerd tombstone-/deprecatie-log).
/// InMemory-DbContext, zoals de ProvenanceBackbone-tests.</summary>
public class GovernanceServiceTests
{
    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; MigrateProposalAsync draait er wel in
            // (Postgres) om de versie-rij + voorstel-status atomair te schrijven.
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
                             .Where(p => p.ClrType == typeof(Pgvector.Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                            .ValueConverter<Pgvector.Vector, string>(
                            v => v.ToString(), s => new Pgvector.Vector(s)));
        }
    }

    // ── Ontologie-versie-historie ─────────────────────────────────────────────
    [Fact]
    public async Task RecordVersion_MoetMonotoonToenemen()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        await svc.RecordVersionAsync(new SemVer(1, 1, 0), OntologyBumpKind.Minor, "eerste bump", "run-1");
        Assert.Equal(new SemVer(1, 1, 0), await svc.GetLatestVersionAsync());

        // Terugval of gelijk moet weigeren.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordVersionAsync(new SemVer(1, 0, 0), OntologyBumpKind.Patch, "terugval", "run-2"));

        // Semver-sort (niet lexicaal): 1.10.0 > 1.9.0.
        await svc.RecordVersionAsync(new SemVer(1, 9, 0), OntologyBumpKind.Minor, "", "run-3");
        await svc.RecordVersionAsync(new SemVer(1, 10, 0), OntologyBumpKind.Minor, "", "run-4");
        Assert.Equal(new SemVer(1, 10, 0), await svc.GetLatestVersionAsync());
    }

    // ── Staging → review → migratie ───────────────────────────────────────────
    [Fact]
    public async Task Voorstel_ZonderBewijs_KanNietGoedgekeurd_MaarBlijftInStaging()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        var p = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "gemined uit set OGN", "run-1", officialCardCount: 1);
        Assert.Equal(SchemaProposalStatus.Proposed, p.Status);

        // Deterministisch bewijs ontbreekt → review-poort dicht → approve weigert.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveProposalAsync(p.Id, "sjoerd", "ziet er goed uit"));

        // Het voorstel is niet weggegooid — het staat nog in staging.
        var reloaded = await db.SchemaProposals.FindAsync(p.Id);
        Assert.Equal(SchemaProposalStatus.Proposed, reloaded!.Status);
    }

    [Fact]
    public async Task Voorstel_MetBewijs_DoorloopReviewEnMigratie()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        var p = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "gemined uit set OGN", "run-1",
            officialCardCount: 5, ruleSectionRef: "section:core-rules-pdf/9.2");

        var approved = await svc.ApproveProposalAsync(p.Id, "sjoerd", "voldoende bewijs");
        Assert.Equal(SchemaProposalStatus.Approved, approved.Status);
        Assert.Equal("sjoerd", approved.ReviewedBy);

        // Migratie legt de versie-rij vast én markeert het voorstel gemigreerd.
        var version = await svc.MigrateProposalAsync(p.Id, new SemVer(1, 1, 0), "run-migrate");
        Assert.Equal("1.1.0", version.Version);
        var migrated = await db.SchemaProposals.FindAsync(p.Id);
        Assert.Equal(SchemaProposalStatus.Migrated, migrated!.Status);
        Assert.Equal("1.1.0", migrated.MigratedInVersion);
        Assert.Equal(new SemVer(1, 1, 0), await svc.GetLatestVersionAsync());
    }

    [Fact]
    public async Task Voorstel_IsIdempotentOpSoortEnNaam()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);
        var a = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS", "m", "run-1");
        var b = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS", "m2", "run-2");
        Assert.Equal(a.Id, b.Id);
        Assert.Single(db.SchemaProposals);
    }

    // Regressie #230 (faal 5): een re-mine met STERKER bewijs versterkt de bestaande
    // staging-rij monotoon — zonder update bleef een zwak-binnengekomen type voorgoed
    // onder de SchemaProposalGate hangen.
    [Fact]
    public async Task Voorstel_ReMineVersterktBewijs_MonotoonEnPromoveerbaar()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        // Set-preview: 1 kaart, geen sectie → poort dicht.
        var first = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "preview: 1 kaart", "run-preview", officialCardCount: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveProposalAsync(first.Id, "sjoerd", "nog te vroeg"));

        // Volledige set: 5 kaarten + verankerende sectie → hetzelfde voorstel, versterkt.
        var second = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "volledige set: 5 kaarten, §9.2", "run-full",
            officialCardCount: 5, ruleSectionRef: "section:core-rules-pdf/9.2");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(db.SchemaProposals);
        Assert.Equal(5, second.OfficialCardCount);
        Assert.True(second.HasRuleSectionEvidence);

        // Een schralere re-mine mag het bewijs niet laten regresseren.
        var third = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "partiële re-scan: 2 kaarten", "run-partial", officialCardCount: 2);
        Assert.Equal(5, third.OfficialCardCount);
        Assert.True(third.HasRuleSectionEvidence);

        // Nu haalt het versterkte voorstel de poort en kan het door review + migratie.
        var approved = await svc.ApproveProposalAsync(second.Id, "sjoerd", "voldoende bewijs");
        Assert.Equal(SchemaProposalStatus.Approved, approved.Status);
    }

    // Regressie #230 (faal 1): een afgewezen type dat later mét bewijs terugkeert,
    // her-kwalificeert i.p.v. voorgoed vergrendeld te blijven op 'rejected'.
    [Fact]
    public async Task Voorstel_AfgewezenDanHerKwalificeert_HeropentEnPromoveerbaar()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        var p = await svc.ProposeAsync(SchemaProposalKind.RelationType, "OVERLOAD",
            "set A: 0 officiële kaarten", "run-a", officialCardCount: 0);
        var rejected = await svc.RejectProposalAsync(p.Id, "sjoerd", "geen dekking in set A");
        Assert.Equal(SchemaProposalStatus.Rejected, rejected.Status);

        // Set B introduceert het type echt: 5 kaarten + sectie → her-kwalificatie.
        var reproposed = await svc.ProposeAsync(SchemaProposalKind.RelationType, "OVERLOAD",
            "set B: 5 kaarten, §11.4", "run-b",
            officialCardCount: 5, ruleSectionRef: "section:core-rules-pdf/11.4");
        Assert.Equal(p.Id, reproposed.Id);
        Assert.Equal(SchemaProposalStatus.Proposed, reproposed.Status);
        Assert.Equal(5, reproposed.OfficialCardCount);

        var approved = await svc.ApproveProposalAsync(reproposed.Id, "sjoerd", "nu wél onderbouwd");
        Assert.Equal(SchemaProposalStatus.Approved, approved.Status);
    }

    // Regressie #230 (faal 1): een reeds goedgekeurd/gehard type mag NIET stil
    // heropenen of van bewijs veranderen bij een latere re-mine.
    [Fact]
    public async Task Voorstel_ReProposeRaaktGoedgekeurdeRijNiet()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        var p = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "5 kaarten, §9.2", "run-1",
            officialCardCount: 5, ruleSectionRef: "section:core-rules-pdf/9.2");
        var approved = await svc.ApproveProposalAsync(p.Id, "sjoerd", "ok");
        Assert.Equal(SchemaProposalStatus.Approved, approved.Status);

        var reproposed = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "re-mine", "run-2", officialCardCount: 99);
        Assert.Equal(SchemaProposalStatus.Approved, reproposed.Status);   // niet heropend
        Assert.Equal(5, reproposed.OfficialCardCount);                    // bewijs onaangetast
    }

    // Regressie #230 (faal 4): afwijzen is symmetrisch met approve/migrate — alleen een
    // 'proposed'-voorstel mag afgewezen worden; een gemigreerd (gehard) type niet.
    [Fact]
    public async Task Afwijzen_WeigertGemigreerdVoorstel()
    {
        await using var db = NewDb();
        var svc = new OntologyGovernanceService(db);

        var p = await svc.ProposeAsync(SchemaProposalKind.RelationType, "REDIRECTS",
            "5 kaarten, §9.2", "run-1",
            officialCardCount: 5, ruleSectionRef: "section:core-rules-pdf/9.2");
        await svc.ApproveProposalAsync(p.Id, "sjoerd", "ok");
        await svc.MigrateProposalAsync(p.Id, new SemVer(1, 1, 0), "run-migrate");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RejectProposalAsync(p.Id, "sjoerd", "bedenk me"));

        var reloaded = await db.SchemaProposals.FindAsync(p.Id);
        Assert.Equal(SchemaProposalStatus.Migrated, reloaded!.Status);   // geen tegenstrijdige tombstone
    }

    // ── Levenscyclus: errata-supersessie + geconsolideerd herstel ─────────────
    [Fact]
    public async Task Errata_ZetRulingSuperseded_EnAfhankelijkenStale_Herstelbaar()
    {
        await using var db = NewDb();
        var svc = new KnowledgeLifecycleService(db);

        var plan = ErrataLifecycle.Plan_("erratum:12", targetRulingRef: "ruling:42",
        [
            new ErrataLifecycle.Dependent("ruling:42", "ruling"),
            new ErrataLifecycle.Dependent("eval_case:7", "forbidden_claim"),
        ]);
        var events = await svc.ApplyErratumSupersessionAsync(plan, "run-errata");

        Assert.Equal(2, events.Count);
        var superseded = await db.LifecycleEvents.SingleAsync(e => e.SubjectRef == "ruling:42");
        Assert.Equal(LifecycleState.Superseded, superseded.ToState);
        Assert.Equal("erratum:12", superseded.SupersededByRef);
        var evalStale = await db.LifecycleEvents.SingleAsync(e => e.SubjectRef == "eval_case:7");
        Assert.Equal(LifecycleState.Stale, evalStale.ToState);

        // Geconsolideerd herstelpad: de superseded ruling terugdraaien.
        var restored = await svc.RestoreAsync("ruling:42", "sjoerd", "run-restore");
        Assert.Equal(LifecycleState.Restored, restored.ToState);
        var reloaded = await db.LifecycleEvents.FindAsync(superseded.Id);
        Assert.True(reloaded!.Reverted);   // oude transitie blijft bestaan, gemarkeerd
    }

    [Fact]
    public async Task Transitie_WeigertOngeldigePad()
    {
        await using var db = NewDb();
        var svc = new KnowledgeLifecycleService(db);
        // Superseded → Active mag niet stil (alleen via Restored).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordTransitionAsync("ruling:1", "ruling",
                LifecycleState.Superseded, LifecycleState.Active, "stil heropenen", "admin", "run-x"));
    }

    // ── Levenscyclus: kostengegate model-upgrade-schaduw-mine ─────────────────
    [Fact]
    public async Task ModelUpgrade_RequeuetAlleenGeselecteerdeFeiten()
    {
        await using var db = NewDb();
        var svc = new KnowledgeLifecycleService(db);

        var facts = new[]
        {
            new ModelUpgradeInvalidation.FactProvenance("assertion:a", "relation", "old", false, false),
            new ModelUpgradeInvalidation.FactProvenance("assertion:b", "relation", "old", true, false),
        };
        var plan = ModelUpgradeInvalidation.Plan_(facts, "old", ModelUpgradeInvalidation.Budget.Default);
        var count = await svc.ApplyModelUpgradeAsync(plan, "old", "run-upgrade");

        Assert.Equal(1, count);
        var ev = await db.LifecycleEvents.SingleAsync();
        Assert.Equal("assertion:a", ev.SubjectRef);
        Assert.Equal(LifecycleState.Stale, ev.ToState);
        Assert.Equal("model_upgrade", ev.Actor);
    }

    // ── Regressie #230 (faal 3): FromState = de ECHTE huidige toestand ─────────
    // Een reeds-stale feit dat door een tweede invalidatiebron (errata) wordt geraakt,
    // krijgt GEEN tweede Active→Stale — dat zou een onmogelijke historie (twee keer
    // vanaf Active zonder tussentijdse terugkeer) in het provenance-spoor schrijven.
    [Fact]
    public async Task Transitie_TweedeInvalidatie_SchrijftGeenValseActiveFromState()
    {
        await using var db = NewDb();
        var svc = new KnowledgeLifecycleService(db);

        // 1) Model-upgrade maakt assertion:X stale (Active→Stale).
        var facts = new[] { new ModelUpgradeInvalidation.FactProvenance("assertion:X", "assertion", "old", false, false) };
        var upgradePlan = ModelUpgradeInvalidation.Plan_(facts, "old", ModelUpgradeInvalidation.Budget.Default);
        Assert.Equal(1, await svc.ApplyModelUpgradeAsync(upgradePlan, "old", "run-upgrade"));

        // 2) Errata raakt hetzelfde onderwerp — maar het is al stale → no-op.
        var errataPlan = ErrataLifecycle.Plan_("erratum:9", targetRulingRef: null,
            [new ErrataLifecycle.Dependent("assertion:X", "assertion")]);
        var events = await svc.ApplyErratumSupersessionAsync(errataPlan, "run-errata");
        Assert.Empty(events);   // geen dubbele her-verificatie-transitie

        // Slechts één transitie, en die start eerlijk vanaf Active.
        var all = await db.LifecycleEvents.Where(e => e.SubjectRef == "assertion:X").ToListAsync();
        Assert.Single(all);
        Assert.Equal(LifecycleState.Active, all[0].FromState);
        Assert.Equal(LifecycleState.Stale, all[0].ToState);
    }

    // Een ECHTE vooruitgang legt de werkelijke vorige toestand vast (Stale→Superseded),
    // niet het hardgecodeerde Active.
    [Fact]
    public async Task Transitie_SupersedeVanStaleRuling_LegtEchteFromStateVast()
    {
        await using var db = NewDb();
        var svc = new KnowledgeLifecycleService(db);

        var verdict = new StalenessEvaluator.Verdict([RecheckTrigger.AgeThreshold]);
        await svc.RequeueStaleAsync("ruling:5", "ruling", verdict, "run-stale");

        var plan = ErrataLifecycle.Plan_("erratum:1", targetRulingRef: "ruling:5", []);
        var events = await svc.ApplyErratumSupersessionAsync(plan, "run-errata");

        var superseded = Assert.Single(events);
        Assert.Equal(LifecycleState.Stale, superseded.FromState);        // echte vorige toestand
        Assert.Equal(LifecycleState.Superseded, superseded.ToState);
    }
}
