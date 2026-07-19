using Microsoft.EntityFrameworkCore;
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
}
