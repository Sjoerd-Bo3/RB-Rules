using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Fase 1 (#225) — canonieke entiteiten &amp; entity-resolution. Dekt de
/// pure bouwstenen (normalisatie, magnitude-behoud, trigram, classifier, gate,
/// gouden-set) én de service (alias-resolve, magnitude-familie, geen-auto-merge-
/// onder-drempel, tombstone-merge + unconsolidate, drift-snapshot, additieve
/// backfill).</summary>
public class EntityResolutionTests
{
    // ── Pure: normalisatie & magnitude ───────────────────────────────────────

    [Theory]
    [InlineData("Showdown Window", "showdown window")]
    [InlineData("showdown-window", "showdown window")]
    [InlineData("  Death_Knell ", "death knell")]
    [InlineData("Deflect", "deflect")]
    public void Normalize_CollapsesCaseAndSeparators(string input, string expected) =>
        Assert.Equal(expected, AliasNormalizer.Normalize(input));

    [Fact]
    public void Magnitude_SplitsTrailingInteger()
    {
        Assert.Equal(new Magnitude.Parsed("Assault", 2), Magnitude.Parse("Assault 2"));
        Assert.Equal(new Magnitude.Parsed("Tank", 3), Magnitude.Parse("Tank 3"));
        Assert.Equal(new Magnitude.Parsed("Deflect", null), Magnitude.Parse("Deflect"));
    }

    [Fact]
    public void Magnitude_PreservesDistinctValues_NotStripped()
    {
        // Kritiek Risico 2a: 'Assault 2' ≠ 'Assault 3' — zelfde familie-basis, maar
        // de magnitude blijft onderscheidend behouden.
        var a2 = Magnitude.Parse("Assault 2");
        var a3 = Magnitude.Parse("Assault 3");
        Assert.Equal(a2.BaseLabel, a3.BaseLabel);      // gedeelde familie
        Assert.NotEqual(a2.Value, a3.Value);           // maar onderscheiden
    }

    // ── Pure: trigram & classifier ───────────────────────────────────────────

    [Fact]
    public void Trigram_IdenticalIsOne_DisjointIsZero()
    {
        Assert.Equal(1.0, Trigrams.Similarity("deflect", "deflect"), 3);
        Assert.Equal(0.0, Trigrams.Similarity("", "deflect"), 3);
    }

    [Fact]
    public void Trigram_VariantOutranksUnrelated()
    {
        var variant = Trigrams.Similarity("deflect", "deflecting");
        var unrelated = Trigrams.Similarity("assault", "assail");
        Assert.True(variant > unrelated);
        Assert.True(variant >= EntityResolutionThresholds.Default.TrigramStrong);
        Assert.True(unrelated < EntityResolutionThresholds.Default.TrigramStrong);
    }

    [Fact]
    public void Classifier_ThreeSignals_IsAutoMergeCandidate()
    {
        var signals = new EntityMatchSignals("deflect", "deflecting",
            Trigrams.Similarity("deflect", "deflecting"), 0.92);
        var d = EntityResolutionClassifier.Classify(signals, EntityResolutionThresholds.Default);
        Assert.Equal(EntityMergeVerdict.AutoMergeCandidate, d.Verdict);
        Assert.Equal(3, d.SignalCount);
    }

    [Fact]
    public void Classifier_TwoSignals_IsReview_NeverMergeOnEmbeddingAlone()
    {
        // Blocking + trigram, geen embedding → 2/3 → review (nooit auto).
        var signals = new EntityMatchSignals("deflect", "deflecting",
            Trigrams.Similarity("deflect", "deflecting"), null);
        var d = EntityResolutionClassifier.Classify(signals, EntityResolutionThresholds.Default);
        Assert.Equal(EntityMergeVerdict.Review, d.Verdict);

        // Alleen embedding sterk, verder niets (niet geblokkeerd, lage trigram) →
        // 1/3 → geen match. Embedding alleen draagt nooit een merge.
        var embOnly = new EntityMatchSignals("deflect", "assault", 0.05, 0.99);
        Assert.Equal(EntityMergeVerdict.NoMatch,
            EntityResolutionClassifier.Classify(embOnly, EntityResolutionThresholds.Default).Verdict);
    }

    // ── Pure: gouden set & gate ──────────────────────────────────────────────

    [Fact]
    public void GoldSet_DefaultThresholds_MeetPrecisionGate()
    {
        var eval = EntityResolutionGoldSet.EvaluateDefault();
        Assert.True(eval.Precision >= EntityResolutionGate.DefaultPrecisionThreshold,
            $"gouden-set-precisie {eval.Precision:0.00} moet ≥ {EntityResolutionGate.DefaultPrecisionThreshold}");
        Assert.True(EntityResolutionGate.IsOpen(eval));
    }

    [Fact]
    public void Gate_BlocksWhenPrecisionBelowThreshold()
    {
        // Synthetische lage precisie (2 TP, 3 FP → 0.40) → gate dicht.
        var poor = new EntityResolutionEvalResult(
            TotalPairs: 10, TruePositives: 2, FalsePositives: 3, TrueNegatives: 5, FalseNegatives: 0);
        Assert.False(EntityResolutionGate.IsOpen(poor));
    }

    [Fact]
    public void Gate_BlocksOnEmptyMeasurement()
    {
        var empty = new EntityResolutionEvalResult(0, 0, 0, 0, 0);
        Assert.False(EntityResolutionGate.IsOpen(empty));
    }

    [Fact]
    public void Gate_ShortLabelsAlwaysReview_EvenWhenOpen()
    {
        var t = EntityResolutionThresholds.Default;
        Assert.False(EntityResolutionGate.MayAutoMerge(gateOpen: true, "Cal", "Calm", t));
        Assert.True(EntityResolutionGate.MayAutoMerge(gateOpen: true, "Deflect", "Deflecting", t));
        // Open gate is een voorwaarde: dicht → nooit.
        Assert.False(EntityResolutionGate.MayAutoMerge(gateOpen: false, "Deflect", "Deflecting", t));
    }

    // ── Service: alias-resolve & magnitude-familie ───────────────────────────

    [Fact]
    public async Task Resolve_MatchesViaAltLabel_CaseAndSeparatorInsensitive()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = "Showdown Window",
            AltLabels = ["Deflecting"], CreatedByRunId = "r",
        });
        await db.SaveChangesAsync();

        var byCanonical = await svc.ResolveAsync("showdown-window", CanonicalEntityKinds.Keyword);
        Assert.True(byCanonical.Matched);
        var byAlias = await svc.ResolveAsync("DEFLECTING", CanonicalEntityKinds.Keyword);
        Assert.True(byAlias.Matched);
        Assert.Equal("Showdown Window", byAlias.Entity!.CanonicalLabel);
    }

    [Fact]
    public async Task ResolveOrRegister_IsIdempotent_StopsSynonymProliferation()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        var first = await svc.ResolveOrRegisterAsync("Deflect", CanonicalEntityKinds.Keyword, "r");
        var again = await svc.ResolveOrRegisterAsync("  deflect ", CanonicalEntityKinds.Keyword, "r");
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, await db.CanonicalEntities.CountAsync());
    }

    [Fact]
    public async Task Resolve_AssaultMagnitudes_ShareFamily_KeepDistinctValue()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        // Registreer via de familie-basis (magnitude wordt afgesplitst).
        var fam = await svc.ResolveOrRegisterAsync("Assault 2", CanonicalEntityKinds.Keyword, "r");
        Assert.Equal("Assault", fam.CanonicalLabel);

        var two = await svc.ResolveAsync("Assault 2", CanonicalEntityKinds.Keyword);
        var three = await svc.ResolveAsync("Assault 3", CanonicalEntityKinds.Keyword);
        Assert.Equal(fam.Id, two.Entity!.Id);
        Assert.Equal(fam.Id, three.Entity!.Id);      // zelfde familie-entiteit
        Assert.Equal(2, two.Magnitude);
        Assert.Equal(3, three.Magnitude);            // magnitude behouden, niet gestript
        Assert.Equal(1, await db.CanonicalEntities.CountAsync()); // GEEN duplicaat per waarde
    }

    // ── Service: scan — geen agressieve auto-merge ───────────────────────────

    [Fact]
    public async Task Scan_TwoSignals_QueuesForReview_DoesNotMerge()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        // Geen embeddings → hoogstens 2/3 signalen → review, nooit auto-merge.
        db.CanonicalEntities.AddRange(
            new CanonicalEntity { Kind = "keyword", CanonicalLabel = "Deflect", CreatedByRunId = "r" },
            new CanonicalEntity { Kind = "keyword", CanonicalLabel = "Deflecting", CreatedByRunId = "r" });
        await db.SaveChangesAsync();

        var result = await svc.ScanForMergeCandidatesAsync("keyword");
        Assert.Equal(1, result.Proposed);
        Assert.Equal(0, result.AutoMerged);
        Assert.Equal(1, result.Queued);
        Assert.Equal(0, await db.CanonicalEntities.CountAsync(e => e.Status == CanonicalEntityStatus.Merged));
        var cand = await db.MergeCandidates.SingleAsync();
        Assert.Equal(MergeCandidateStatus.Open, cand.Status);
    }

    [Fact]
    public async Task Scan_ThreeSignals_GateOpen_AutoMerges()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        var vec = new Vector(new float[] { 1f, 0f, 0f });
        db.CanonicalEntities.AddRange(
            new CanonicalEntity { Kind = "keyword", CanonicalLabel = "Deflect",
                CreatedByRunId = "r", Embedding = vec, EmbeddingModel = "bge-m3", EmbeddingDim = 3 },
            new CanonicalEntity { Kind = "keyword", CanonicalLabel = "Deflecting",
                CreatedByRunId = "r", Embedding = vec, EmbeddingModel = "bge-m3", EmbeddingDim = 3 });
        await db.SaveChangesAsync();

        var result = await svc.ScanForMergeCandidatesAsync("keyword");
        Assert.True(result.GateOpen);
        Assert.Equal(1, result.AutoMerged);

        var merged = await db.CanonicalEntities.SingleAsync(e => e.Status == CanonicalEntityStatus.Merged);
        var survivor = await db.CanonicalEntities.SingleAsync(e => e.Status != CanonicalEntityStatus.Merged);
        Assert.Equal(survivor.Id, merged.MergedIntoId);
        Assert.Contains(merged.CanonicalLabel, survivor.AltLabels); // alias geabsorbeerd
        Assert.Equal(1, await db.MergeDecisions.CountAsync(d => !d.Reverted));
    }

    [Fact]
    public async Task Scan_ThreeSimilarEntities_GateOpen_NoCascadeOntoTombstones()
    {
        // Regressie (#225-review): een blok van 3 onderling-gelijkende varianten met
        // identieke embeddings scoort op elk paar 3/3 signalen. Zonder de tombstone-
        // guard verwerkt de lus (1,2),(1,3),(2,3): na (1,2)+(1,3) zijn 2 én 3 al
        // tombstones, waarna (2,3) 3 OPNIEUW zou mergen (naar tombstone 2) — kapotte
        // keten + dubbele MergeDecision + ambigu herstelpad. De guard slaat (2,3) over.
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        var vec = new Vector(new float[] { 1f, 0f, 0f });
        CanonicalEntity Make(long id, string label) => new()
        {
            Id = id, Kind = "keyword", CanonicalLabel = label, CreatedByRunId = "r",
            Embedding = vec, EmbeddingModel = "bge-m3", EmbeddingDim = 3,
        };
        db.CanonicalEntities.AddRange(Make(1, "Deflect"), Make(2, "Deflecting"), Make(3, "Deflected"));
        await db.SaveChangesAsync();

        var result = await svc.ScanForMergeCandidatesAsync("keyword");
        Assert.True(result.GateOpen);

        // Precies één levende overlever (laagste id) en twee tombstones die er beide
        // rechtstreeks naar wijzen — geen tombstone→tombstone-keten.
        var survivors = await db.CanonicalEntities
            .Where(e => e.Status != CanonicalEntityStatus.Merged).ToListAsync();
        var tombstones = await db.CanonicalEntities
            .Where(e => e.Status == CanonicalEntityStatus.Merged).ToListAsync();
        var survivor = Assert.Single(survivors);
        Assert.Equal(1L, survivor.Id);
        Assert.Equal(2, tombstones.Count);
        Assert.All(tombstones, t => Assert.Equal(survivor.Id, t.MergedIntoId));

        // Elke tombstone heeft exact één niet-teruggedraaide MergeDecision (geen dubbele
        // beslissing voor dezelfde bron) die naar de levende overlever wijst.
        foreach (var t in tombstones)
        {
            var decisions = await db.MergeDecisions
                .Where(d => d.SourceEntityId == t.Id && !d.Reverted).ToListAsync();
            var decision = Assert.Single(decisions);
            Assert.Equal(survivor.Id, decision.TargetEntityId);
        }

        // Herstelpad blijft schoon: elke tombstone is eenduidig terug te draaien.
        foreach (var t in tombstones)
            Assert.True(await svc.UnconsolidateAsync(t.Id));
    }

    // ── Service: tombstone-merge + unconsolidate (herstelpad) ────────────────

    [Fact]
    public async Task Merge_ThenUnconsolidate_RestoresCleanly()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        db.CanonicalEntities.AddRange(
            new CanonicalEntity { Id = 1, Kind = "mechanic", CanonicalLabel = "Showdown",
                AltLabels = ["Show-down"], CreatedByRunId = "r" },
            new CanonicalEntity { Id = 2, Kind = "mechanic", CanonicalLabel = "Showdown Phase",
                CreatedByRunId = "r" });
        await db.SaveChangesAsync();

        var decision = await svc.MergeAsync(targetId: 1, sourceId: 2, "beheerder: zelfde mechaniek");
        Assert.NotNull(decision);
        var target = await db.CanonicalEntities.FindAsync(1L);
        var source = await db.CanonicalEntities.FindAsync(2L);
        Assert.Equal(CanonicalEntityStatus.Merged, source!.Status);
        Assert.Equal(1L, source.MergedIntoId);
        Assert.Contains("Showdown Phase", target!.AltLabels);
        Assert.Contains("Showdown Phase", decision!.MovedAltLabels);

        var ok = await svc.UnconsolidateAsync(2);
        Assert.True(ok);
        target = await db.CanonicalEntities.FindAsync(1L);
        source = await db.CanonicalEntities.FindAsync(2L);
        Assert.Equal(CanonicalEntityStatus.Candidate, source!.Status);
        Assert.Null(source.MergedIntoId);
        Assert.DoesNotContain("Showdown Phase", target!.AltLabels); // exact teruggetrokken
        Assert.Contains("Show-down", target.AltLabels);             // eigen alias intact
        var reverted = await db.MergeDecisions.SingleAsync();
        Assert.True(reverted.Reverted);                             // audit-spoor blijft
    }

    // ── Service: drift-snapshot ──────────────────────────────────────────────

    [Fact]
    public async Task DriftSnapshot_CountsLiveTombstonesSingletonsAndDebt()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        db.CanonicalEntities.AddRange(
            new CanonicalEntity { Id = 1, Kind = "mechanic", CanonicalLabel = "Showdown",
                AltLabels = ["Show-down"], CreatedByRunId = "r" },       // niet-singleton (heeft alias)
            new CanonicalEntity { Id = 2, Kind = "mechanic", CanonicalLabel = "Conquer",
                CreatedByRunId = "r" },                                   // singleton
            new CanonicalEntity { Id = 3, Kind = "keyword", CanonicalLabel = "Deflect",
                Status = CanonicalEntityStatus.Merged, MergedIntoId = 4, CreatedByRunId = "r" }, // tombstone
            new CanonicalEntity { Id = 4, Kind = "keyword", CanonicalLabel = "Deflecting",
                CreatedByRunId = "r" });                                  // singleton
        db.MergeCandidates.Add(new MergeCandidate { EntityAId = 1, EntityBId = 2,
            Verdict = "review", Reason = "x", RunId = "r", Status = MergeCandidateStatus.Open });
        await db.SaveChangesAsync();

        var snap = await svc.DriftSnapshotAsync();
        var mech = snap.ByKind.Single(k => k.Kind == "mechanic");
        var kw = snap.ByKind.Single(k => k.Kind == "keyword");
        Assert.Equal(2, mech.Live);
        Assert.Equal(1, mech.Singletons);       // alleen Conquer
        Assert.Equal(1, kw.Live);               // Deflecting (Deflect is tombstone)
        Assert.Equal(1, kw.Tombstones);
        Assert.Equal(1, snap.DuplicationDebt);  // één open kandidaat
    }

    // ── Service: additieve, niet-destructieve backfill ───────────────────────

    [Fact]
    public async Task Backfill_IsAdditive_Idempotent_LeavesCardMechanicsUntouched()
    {
        using var db = NewDb();
        var svc = new EntityResolutionService(db);
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001-001", Name = "Test", SetId = "ogn",
            Mechanics = ["Deflect", "deflect", "Conquer"], // casing-dupe + tweede mechaniek
        });
        await db.SaveChangesAsync();

        var created = await svc.RegisterExistingMechanicsAsync(CanonicalEntityKinds.Mechanic);
        Assert.Equal(2, created);   // Deflect (casing-dupe samengevouwen) + Conquer
        var second = await svc.RegisterExistingMechanicsAsync(CanonicalEntityKinds.Mechanic);
        Assert.Equal(0, second);    // idempotent

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Deflect", "deflect", "Conquer"], card.Mechanics!); // bronveld ongemoeid
    }

    // ── InMemory-context (pgvector als tekst, zoals de andere service-tests) ──
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
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }
}
