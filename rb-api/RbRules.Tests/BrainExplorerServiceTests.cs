using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Domain.Reasoning;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De Brein-verkenner (#236): read-only projecties over de brein-tabellen.
/// Elke test bevestigt (a) een nette lege staat zonder data en (b) een correcte
/// projectie met data. Additief — geen bestaand model/gedrag wordt geraakt.</summary>
public class BrainExplorerServiceTests
{
    [Fact]
    public async Task Overview_ZonderData_AllesNul()
    {
        using var db = NewDb();
        var svc = new BrainExplorerService(db);

        var o = await svc.OverviewAsync();

        Assert.Equal(0, o.Assertions);
        Assert.Equal(0, o.CanonicalEntities);
        Assert.Equal(0, o.Interactions);
        Assert.Equal(0, o.Conflicts);
        Assert.Equal(0, o.MiningRuns);
        Assert.Equal(0, o.EvalBaselines);
        Assert.Equal(0, o.AnswerTraces);
    }

    [Fact]
    public async Task Overview_MetData_TeltPerTabelMetSubtellingen()
    {
        using var db = NewDb();
        db.MiningRuns.Add(Run("r1"));
        db.CanonicalEntities.Add(Entity("Deflect", CanonicalEntityStatus.Canonical, "r1"));
        db.CanonicalEntities.Add(Entity("Deflecting", CanonicalEntityStatus.Merged, "r1", mergedInto: 0));
        db.Interactions.Add(Interaction("card:a", "card:b", InteractionStatus.Promoted, "r1"));
        db.Interactions.Add(Interaction("card:c", "card:d", InteractionStatus.Candidate, "r1"));
        db.ReasoningConflicts.Add(Conflict("claim:1", ReasoningConflictStatus.Open));
        db.EvalBaselines.Add(Baseline());
        await db.SaveChangesAsync();

        var o = await new BrainExplorerService(db).OverviewAsync();

        Assert.Equal(2, o.CanonicalEntities);
        Assert.Equal(1, o.CanonicalEntitiesMerged);
        Assert.Equal(2, o.Interactions);
        Assert.Equal(1, o.InteractionsPromoted);
        Assert.Equal(1, o.Conflicts);
        Assert.Equal(1, o.ConflictsOpen);
        Assert.Equal(1, o.MiningRuns);
        Assert.Equal(1, o.EvalBaselines);
    }

    [Fact]
    public async Task Entities_TombstoneKrijgtDoellabelEnFiltert()
    {
        using var db = NewDb();
        db.MiningRuns.Add(Run("r1"));
        var live = Entity("Deflect", CanonicalEntityStatus.Canonical, "r1");
        live.AltLabels = ["Deflecting"];
        db.CanonicalEntities.Add(live);
        await db.SaveChangesAsync();

        var tomb = Entity("Deflekt", CanonicalEntityStatus.Merged, "r1", mergedInto: live.Id);
        db.CanonicalEntities.Add(tomb);
        await db.SaveChangesAsync();

        var svc = new BrainExplorerService(db);

        var all = await svc.EntitiesAsync(null, null, 1);
        Assert.Equal(2, all.Total);

        var tombRow = all.Items.Single(i => i.Status == CanonicalEntityStatus.Merged);
        Assert.Equal(live.Id, tombRow.MergedIntoId);
        Assert.Equal("Deflect", tombRow.MergedIntoLabel);

        var liveRow = all.Items.Single(i => i.Status == CanonicalEntityStatus.Canonical);
        Assert.Contains("Deflecting", liveRow.AltLabels);
        Assert.Null(liveRow.MergedIntoLabel);

        var merged = await svc.EntitiesAsync(null, CanonicalEntityStatus.Merged, 1);
        Assert.Equal(1, merged.Total);
        Assert.Equal("mechanic", Assert.Single(merged.Items).Kind);
    }

    [Fact]
    public async Task Interactions_ConditiesTierEnProvenanceAnker()
    {
        using var db = NewDb();
        db.MiningRuns.Add(Run("r1"));
        var it = Interaction("mechanic:Deflect", "card:ogn-011-298", InteractionStatus.Verified, "r1");
        it.StatusReason = "consensus 2/2";
        it.Conditions.Add(new InteractionCondition
        {
            InteractionId = 0, // EF-fixup zet de FK uit de Conditions-navigatie bij SaveChanges
            OnKind = InteractionConditionKinds.Window, SubjectRole = null, Value = "Showdown",
        });
        db.Interactions.Add(it);
        await db.SaveChangesAsync();

        // Een Assertion met subject interaction:{id} is het provenance-anker.
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(), Subject = it.Ref.Format(), FactKind = "card_interaction",
            MiningRunId = "r1", DerivedFromRef = "section:core-rules-pdf/7.4",
        });
        await db.SaveChangesAsync();

        var page = await new BrainExplorerService(db).InteractionsAsync(null, 1);

        var row = Assert.Single(page.Items);
        Assert.Equal(InteractionStatus.Verified, row.Status);
        Assert.Equal("consensus 2/2", row.StatusReason);
        Assert.Equal("Showdown", Assert.Single(row.Conditions).Value);
        Assert.Equal(it.Ref.Format(), row.SubjectRef);
        Assert.StartsWith("assertion:", row.AssertionRef);

        // Statusfilter op een niet-bestaande tier → lege staat, geen fout.
        var promoted = await new BrainExplorerService(db).InteractionsAsync(InteractionStatus.Promoted, 1);
        Assert.Empty(promoted.Items);
    }

    /// <summary>Regressie (#236): de provenance-anker-lookup in InteractionsAsync mag
    /// niet server-side greatest-n-per-group doen — dat vertaalt Npgsql niet en de
    /// InMemory-test hierboven maskeert het. Bewijs de échte productiequery via
    /// ToQueryString op een Npgsql-context (geen database nodig).</summary>
    [Fact]
    public void AssertionAnchorQuery_TranslatesToSql()
    {
        using var db = new RbRulesDbContext(
            new DbContextOptionsBuilder<RbRulesDbContext>()
                .UseNpgsql("Host=localhost;Database=x;Username=x", o => o.UseVector())
                .UseSnakeCaseNamingConvention()
                .Options);
        var subjects = new List<string> { "interaction:1", "interaction:2" };
        var sql = BrainExplorerService.AssertionAnchorQuery(db, subjects).ToQueryString();
        Assert.Contains("assertion", sql);
    }

    [Fact]
    public async Task ProvenanceChain_SubjectRef_GeeftKetenMetRun()
    {
        using var db = NewDb();
        db.MiningRuns.Add(Run("r1", model: "claude-opus-4-8"));
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(), Subject = "interaction:42", FactKind = "card_interaction",
            MiningRunId = "r1", DerivedFromRef = "section:core-rules-pdf/7.4",
            Model = "claude-opus-4-8", Verifier = "consensus", Verdict = "positive",
        });
        await db.SaveChangesAsync();

        var chain = await new BrainExplorerService(db).ProvenanceChainAsync("interaction:42");

        Assert.NotNull(chain);
        Assert.Equal("interaction:42", chain!.Subject);
        var a = Assert.Single(chain.Assertions);
        Assert.Equal("section:core-rules-pdf/7.4", a.DerivedFromRef);
        Assert.Equal("consensus", a.Verifier);
        Assert.NotNull(a.Run);
        Assert.Equal("claude-opus-4-8", a.Run!.LlmModel);
    }

    [Fact]
    public async Task ProvenanceChain_OngeldigeRef_Null_GeldigeLegeSubject_LegeKeten()
    {
        using var db = NewDb();
        var svc = new BrainExplorerService(db);

        Assert.Null(await svc.ProvenanceChainAsync("geen-ref"));

        var chain = await svc.ProvenanceChainAsync("interaction:999");
        Assert.NotNull(chain);
        Assert.Empty(chain!.Assertions);
    }

    [Fact]
    public async Task Conflicts_FilterEnKanaalMeegenomen()
    {
        using var db = NewDb();
        db.ReasoningConflicts.Add(Conflict("claim:1", ReasoningConflictStatus.Open,
            kind: ReasoningConflictKind.ClaimContradictsOfficial));
        db.ReasoningConflicts.Add(Conflict("card:x", ReasoningConflictStatus.Resolved,
            kind: ReasoningConflictKind.DisjointnessViolation));
        await db.SaveChangesAsync();

        var svc = new BrainExplorerService(db);

        var open = await svc.ConflictsAsync(ReasoningConflictStatus.Open, 1);
        var row = Assert.Single(open.Items);
        Assert.Equal(ConflictRouter.ChannelString(ConflictChannel.Misconception), row.Channel);

        var all = await svc.ConflictsAsync(null, 1);
        Assert.Equal(2, all.Total);
    }

    [Fact]
    public async Task AnswerTrace_DetailMetSupports_EnLijst()
    {
        using var db = NewDb();
        var trace = new AnswerTrace
        {
            Id = Ulid.NewUlid(), Question = "Werkt Deflect in een showdown?",
            QuestionType = "Interaction", RetrievalMode = "Drift", PrimaryChannel = "official",
            Beta = 0.7, GateMemo = "officiële dekking",
        };
        trace.Supports.Add(new AnswerTraceSupport
        {
            AnswerTraceId = trace.Id, CitationN = 2, SubjectRef = "card:b",
            Tier = "Community", TrustWeightAtQuery = 0.30,
        });
        trace.Supports.Add(new AnswerTraceSupport
        {
            AnswerTraceId = trace.Id, CitationN = 1, SubjectRef = "section:core-rules-pdf/7.4",
            Tier = "Official", TrustWeightAtQuery = 1.0,
        });
        db.AnswerTraces.Add(trace);
        await db.SaveChangesAsync();

        var svc = new BrainExplorerService(db);

        var detail = await svc.AnswerTraceAsync(trace.Id);
        Assert.NotNull(detail);
        Assert.Equal("official", detail!.PrimaryChannel);
        Assert.Equal(2, detail.Supports.Count);
        Assert.Equal(1, detail.Supports[0].CitationN); // op CitationN gesorteerd

        var list = await svc.AnswerTracesAsync();
        Assert.Equal(2, Assert.Single(list).SupportCount);

        Assert.Null(await svc.AnswerTraceAsync("onbekend"));
    }

    [Fact]
    public async Task Observability_MiningPrecisieDriftEnTiers_GraphLeeg()
    {
        using var db = NewDb();
        var run = Run("r1", model: "claude-opus-4-8");
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Kind = "interaction";
        run.Candidates = 10; run.Verified = 4; run.Rejected = 6;
        db.MiningRuns.Add(run);
        db.CanonicalEntities.Add(Entity("Deflect", CanonicalEntityStatus.Canonical, "r1"));
        db.Interactions.Add(Interaction("card:a", "card:b", InteractionStatus.Promoted, "r1"));
        db.ReasoningConflicts.Add(Conflict("claim:1", ReasoningConflictStatus.Open,
            kind: ReasoningConflictKind.ClaimContradictsOfficial));
        await db.SaveChangesAsync();

        var obs = await new BrainExplorerService(db).ObservabilityAsync();

        var mp = Assert.Single(obs.Report.MiningPrecision);
        Assert.Equal("interaction", mp.Kind);
        Assert.Equal(0.4, mp.Precision, 3);
        Assert.NotNull(obs.Report.CanonicalDrift);
        Assert.Empty(obs.Report.GraphDrift);        // Neo4j-deel: nette lege staat
        Assert.Null(obs.Report.CommunityHealth);    // GDS-deel: nette lege staat
        Assert.Equal("promoted", Assert.Single(obs.InteractionTiers).Key);
        Assert.Equal("misconception", Assert.Single(obs.ConflictChannels).Key);
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private static MiningRun Run(string id, string? model = null) => new()
    {
        Id = id, Kind = "mechanic", LlmModel = model,
    };

    private static CanonicalEntity Entity(string label, string status, string runId, long? mergedInto = null) => new()
    {
        Kind = "mechanic", CanonicalLabel = label, Status = status,
        CreatedByRunId = runId, MergedIntoId = mergedInto == 0 ? null : mergedInto,
    };

    private static Interaction Interaction(string agent, string patient, string status, string runId) => new()
    {
        AgentRef = agent, PatientRef = patient, Kind = InteractionKinds.Counters,
        Status = status, CreatedByRunId = runId,
    };

    private static ReasoningConflict Conflict(
        string subject, string status, string? kind = null)
    {
        var k = kind ?? ReasoningConflictKind.ClaimContradictsOfficial;
        return new ReasoningConflict
        {
            PatternId = "p1", Kind = k, Channel = ConflictRouter.ChannelString(ConflictRouter.Route(k)),
            SubjectRef = subject, DedupeKey = ReasoningConflictDedupe.Key("p1", subject, null),
            RunId = "r1", Status = status,
        };
    }

    private static EvalBaselineRecord Baseline() => new()
    {
        Ring = "A", QueryType = "Interaction", Metric = "subgraph_recall",
        Mean = 0.8, StdDev = 0.05, SampleCount = 10,
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op als tekst
    /// (zelfde patroon als de AdminOverview-tests).</summary>
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
