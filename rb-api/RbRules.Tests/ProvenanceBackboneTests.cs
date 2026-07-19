using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Fase 0a (#233) — provenance-ruggengraat: ULID-sleutel,
/// AssertionProvenanceGuard, embedding-provenance, Ring-A-gate, de
/// DbContext-schrijfpoort en de EF-vertaalbaarheid van de audit-queries.</summary>
public class ProvenanceBackboneTests
{
    // ── Ulid ────────────────────────────────────────────────────────────────
    [Fact]
    public void Ulid_HasFixedLengthAndCrockfordAlphabet()
    {
        var id = Ulid.NewUlid();
        Assert.Equal(26, id.Length);
        Assert.DoesNotContain(id, c => "ILOU".Contains(c)); // uitgesloten letters
        Assert.All(id, c => Assert.Contains(c, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public void Ulid_IsDeterministicForSameTimeAndRandomness()
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var rnd = new byte[10];
        Assert.Equal(Ulid.NewUlid(t, rnd), Ulid.NewUlid(t, rnd));
    }

    [Fact]
    public void Ulid_SortsChronologically()
    {
        var rnd = new byte[10];
        var earlier = Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(1_000), rnd);
        var later = Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(2_000), rnd);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void Ulid_RejectsWrongRandomnessLength()
    {
        Assert.Throws<ArgumentException>(() =>
            Ulid.NewUlid(DateTimeOffset.UtcNow, new byte[9]));
    }

    // ── AssertionProvenanceGuard ─────────────────────────────────────────────
    private static Assertion ValidAssertion() => new()
    {
        Id = Ulid.NewUlid(),
        Subject = BrainRef.Relation(42).Format(),
        FactKind = FactKinds.Relation,
        MiningRunId = Ulid.NewUlid(),
        DerivedFromRef = BrainRef.Source("core-rules-pdf").Format(),
    };

    [Fact]
    public void Guard_AcceptsFullyProvenancedAssertion()
    {
        var r = AssertionProvenanceGuard.Validate(ValidAssertion());
        Assert.True(r.IsValid);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Guard_RejectsMissingGeneratedBy()
    {
        var a = ValidAssertion();
        a.MiningRunId = "";
        var r = AssertionProvenanceGuard.Validate(a);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == AssertionProvenanceGuard.Code.MissingGeneratedBy);
    }

    [Fact]
    public void Guard_RejectsMissingDerivedFrom()
    {
        var a = ValidAssertion();
        a.DerivedFromRef = "  ";
        var r = AssertionProvenanceGuard.Validate(a);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == AssertionProvenanceGuard.Code.MissingDerivedFrom);
    }

    [Fact]
    public void Guard_RejectsUnparseableDerivedFromRef()
    {
        var a = ValidAssertion();
        a.DerivedFromRef = "not-a-brainref";
        var r = AssertionProvenanceGuard.Validate(a);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == AssertionProvenanceGuard.Code.InvalidDerivedFromRef);
    }

    [Fact]
    public void Guard_RejectsMissingSubject()
    {
        var a = ValidAssertion();
        a.Subject = "   ";
        var r = AssertionProvenanceGuard.Validate(a);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == AssertionProvenanceGuard.Code.MissingSubject);
    }

    [Fact]
    public void Guard_RejectsMissingFactKind()
    {
        var a = ValidAssertion();
        a.FactKind = "";
        var r = AssertionProvenanceGuard.Validate(a);
        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == AssertionProvenanceGuard.Code.MissingFactKind);
    }

    // ── EmbeddingProvenance ──────────────────────────────────────────────────
    [Fact]
    public void EmbeddingContentHash_IsDeterministicAndTextSensitive()
    {
        Assert.Equal(EmbeddingProvenance.ContentHash("Deflect"), EmbeddingProvenance.ContentHash("Deflect"));
        Assert.NotEqual(EmbeddingProvenance.ContentHash("Deflect"), EmbeddingProvenance.ContentHash("Assault"));
        Assert.Equal(64, EmbeddingProvenance.ContentHash("x").Length); // SHA-256 hex
    }

    [Theory]
    [InlineData("bge-m3", 1024, "abc", true)]
    [InlineData("bge-m3", 512, "abc", false)]   // verkeerde dim
    [InlineData("other", 1024, "abc", false)]   // verkeerd model
    [InlineData("bge-m3", 1024, null, false)]   // geen hash
    [InlineData(null, 1024, "abc", false)]      // geen model
    public void EmbeddingProvenance_IsComplete(string? model, int dim, string? hash, bool expected)
    {
        Assert.Equal(expected, EmbeddingProvenance.IsComplete(model, dim, hash));
    }

    // ── ProvenanceAudit (Ring-A-gate) ────────────────────────────────────────
    [Fact]
    public void RingA_PassesOnlyWhenNoNewFactsMissProvenance()
    {
        Assert.True(new ProvenanceAudit.Report(0, 0, 12).Passes);   // legacy blokkeert niet
        Assert.False(new ProvenanceAudit.Report(1, 0, 0).Passes);   // nieuw feit zonder Assertion
        Assert.False(new ProvenanceAudit.Report(0, 3, 0).Passes);   // embedding zonder herkomst
    }

    [Fact]
    public void RingA_SummaryReportsBacklogWhenPassing()
    {
        Assert.Contains("5 legacy", new ProvenanceAudit.Report(0, 0, 5).Summary);
        Assert.Contains("Provenance-gat", new ProvenanceAudit.Report(2, 0, 0).Summary);
    }

    // ── DbContext-schrijfpoort (de .NET-helft van het dubbele write-guard) ────
    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: vectors als tekst opslaan
    /// (zelfde workaround als de AdminOverview-tests).</summary>
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

    [Fact]
    public async Task SaveChanges_RejectsAssertionWithoutProvenance()
    {
        await using var db = NewDb();
        var bad = ValidAssertion();
        bad.DerivedFromRef = "";   // schendt DERIVED_FROM
        db.Assertions.Add(bad);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("provenance-poort", ex.Message);
    }

    [Fact]
    public async Task SaveChanges_AcceptsFullyProvenancedAssertion()
    {
        await using var db = NewDb();
        db.Assertions.Add(ValidAssertion());
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Assertions.CountAsync());
    }

    /// <summary>De schrijfpoort draait óók op EntityState.Modified: provenance
    /// die later wordt gestript (de klassieke manier waarop herkomst wegvalt bij
    /// een update) moet net zo hard falen als een provenance-loze insert.</summary>
    [Fact]
    public async Task SaveChanges_RejectsAssertionModifiedToStripGeneratedBy()
    {
        await using var db = NewDb();
        var a = ValidAssertion();
        db.Assertions.Add(a);
        await db.SaveChangesAsync();               // eerst valide opgeslagen

        a.MiningRunId = "";                        // strip WAS_GENERATED_BY na de feiten
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("provenance-poort", ex.Message);
    }

    // ── ProvenanceAuditService.AuditAsync (Ring-A-teller, end-to-end op de DB) ──
    private static Relation RelationRow(long id, DateTimeOffset detectedAt) => new()
    {
        Id = id,
        FromRef = "mechanic:Deflect",
        ToRef = "section:core-rules-pdf/7.4",
        Kind = "counters",
        Explanation = "x",
        Provenance = "concept:combat",
        DetectedAt = detectedAt,
    };

    private static CardInteraction InteractionRow(long id, DateTimeOffset detectedAt) => new()
    {
        Id = id,
        CardAId = "ogn-011-298",
        CardBId = "ogn-011-299",
        Kind = "combo",
        Explanation = "x",
        DetectedAt = detectedAt,
    };

    private static Assertion AssertionFor(string subject, string factKind)
    {
        var a = ValidAssertion();
        a.Subject = subject;
        a.FactKind = factKind;
        return a;
    }

    /// <summary>De cutoff splitst nieuw (moet 0 zijn = de gate) van legacy; een
    /// relatie én een interactie zonder Assertion aan elke kant bewijzen dat de
    /// vergelijkingsrichting, de nieuw/legacy-toewijzing én de interactions-term
    /// alle drie kloppen (een geflipte `<`, een omgewisselde toewijzing of een
    /// weggevallen interactions-optelling laat dit falen).</summary>
    [Fact]
    public async Task AuditAsync_CountsNewFactsMissingProvenanceAndSplitsLegacy()
    {
        await using var db = NewDb();
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000_000_000);
        var newTime = cutoff.AddDays(1);
        var legacyTime = cutoff.AddDays(-1);

        // Nieuwe feiten zónder Assertion → laten de gate falen.
        db.Relations.Add(RelationRow(100, newTime));
        db.CardInteractions.Add(InteractionRow(200, newTime));
        // Nieuwe relatie MÉT bijpassende Assertion → telt niet mee.
        db.Relations.Add(RelationRow(101, newTime));
        db.Assertions.Add(AssertionFor(
            ProvenanceAuditService.RelationSubjectPrefix + 101, FactKinds.Relation));
        // Legacy feiten zónder Assertion → backlog, blokkeren de gate niet.
        db.Relations.Add(RelationRow(102, legacyTime));
        db.CardInteractions.Add(InteractionRow(201, legacyTime));
        await db.SaveChangesAsync();

        var report = await new ProvenanceAuditService(db).AuditAsync(cutoff);

        Assert.Equal(2, report.FactsMissingAssertion);     // 1 relation-new + 1 interaction-new
        Assert.Equal(2, report.LegacyBacklog);             // 1 relation-legacy + 1 interaction-legacy
        Assert.Equal(0, report.EmbeddingsMissingProvenance);
        Assert.False(report.Passes);
    }

    /// <summary>Zowel de relation- als de interaction-subject-prefix moeten kloppen
    /// om een nieuw feit als "gedekt" te zien — een verkeerde prefix laat de gate
    /// ten onrechte falen.</summary>
    [Fact]
    public async Task AuditAsync_PassesWhenEveryNewFactHasAssertion()
    {
        await using var db = NewDb();
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000_000_000);
        var newTime = cutoff.AddDays(1);

        db.Relations.Add(RelationRow(100, newTime));
        db.Assertions.Add(AssertionFor(
            ProvenanceAuditService.RelationSubjectPrefix + 100, FactKinds.Relation));
        db.CardInteractions.Add(InteractionRow(200, newTime));
        db.Assertions.Add(AssertionFor(
            ProvenanceAuditService.InteractionSubjectPrefix + 200, FactKinds.CardInteraction));
        await db.SaveChangesAsync();

        var report = await new ProvenanceAuditService(db).AuditAsync(cutoff);

        Assert.Equal(0, report.FactsMissingAssertion);
        Assert.Equal(0, report.LegacyBacklog);
        Assert.True(report.Passes);
    }

    /// <summary>Fase-2 gereïficeerde interacties tellen óók mee in de Ring-A-gate
    /// (#226-review #4): een levend (niet-verworpen) nieuw <see cref="Interaction"/>
    /// -feit zonder Assertion laat de gate falen; een verworpen interactie is geen
    /// levend feit en telt niet; een legacy interactie is backlog.</summary>
    [Fact]
    public async Task AuditAsync_CountsReifiedInteractionsMissingProvenance_ExcludesRejected()
    {
        await using var db = NewDb();
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000_000_000);
        var newTime = cutoff.AddDays(1);
        var legacyTime = cutoff.AddDays(-1);

        // Nieuw, levend feit zónder Assertion → laat de gate falen.
        db.Interactions.Add(new Interaction
        {
            AgentRef = "card:a", PatientRef = "card:b", Kind = InteractionKinds.Counters,
            Status = InteractionStatus.Promoted, CreatedByRunId = "run", DetectedAt = newTime,
        });
        // Nieuw, MÉT bijpassende Assertion → telt niet mee.
        var withProv = new Interaction
        {
            Id = 555, AgentRef = "card:c", PatientRef = "card:d", Kind = InteractionKinds.Modifies,
            Status = InteractionStatus.Candidate, CreatedByRunId = "run", DetectedAt = newTime,
        };
        db.Interactions.Add(withProv);
        db.Assertions.Add(AssertionFor(
            ProvenanceAuditService.ReifiedInteractionSubjectPrefix + 555, FactKinds.Interaction));
        // Nieuw maar verworpen (geen levend feit) → telt niet mee.
        db.Interactions.Add(new Interaction
        {
            AgentRef = "card:e", PatientRef = "card:f", Kind = InteractionKinds.Grants,
            Status = InteractionStatus.Rejected, CreatedByRunId = "run", DetectedAt = newTime,
        });
        // Legacy, levend, zónder Assertion → backlog, blokkeert niet.
        db.Interactions.Add(new Interaction
        {
            AgentRef = "card:g", PatientRef = "card:h", Kind = InteractionKinds.Requires,
            Status = InteractionStatus.Promoted, CreatedByRunId = "run", DetectedAt = legacyTime,
        });
        await db.SaveChangesAsync();

        var report = await new ProvenanceAuditService(db).AuditAsync(cutoff);

        Assert.Equal(1, report.FactsMissingAssertion);   // alleen het nieuwe levende feit
        Assert.Equal(1, report.LegacyBacklog);           // de legacy interactie
        Assert.False(report.Passes);
    }

    /// <summary>De embedding-tak (productie-projectie <c>EmbeddingRow</c> + filter):
    /// een rij mét content-hash maar zónder geldig model is een provenance-fout in
    /// de nieuwe pipeline; een rij zonder hash is legacy-backlog.</summary>
    [Fact]
    public async Task AuditAsync_FlagsEmbeddingsWithoutValidModelAndSplitsLegacy()
    {
        await using var db = NewDb();
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000_000_000);
        var vec = new Pgvector.Vector(new float[] { 0.1f });

        db.Cards.Add(new Card { RiftboundId = "c-wrong-model", Name = "x", Embedding = vec,
            EmbeddingContentHash = "abc", EmbeddingModel = "old-model" });   // fout
        db.Cards.Add(new Card { RiftboundId = "c-null-model", Name = "x", Embedding = vec,
            EmbeddingContentHash = "abc", EmbeddingModel = null });          // fout
        db.Cards.Add(new Card { RiftboundId = "c-ok", Name = "x", Embedding = vec,
            EmbeddingContentHash = "abc", EmbeddingModel = EmbeddingConfig.Model }); // compleet
        db.Cards.Add(new Card { RiftboundId = "c-legacy", Name = "x", Embedding = vec,
            EmbeddingContentHash = null, EmbeddingModel = null });           // legacy
        db.Cards.Add(new Card { RiftboundId = "c-none", Name = "x", Embedding = null }); // genegeerd
        await db.SaveChangesAsync();

        var report = await new ProvenanceAuditService(db).AuditAsync(cutoff);

        Assert.Equal(2, report.EmbeddingsMissingProvenance);   // wrong-model + null-model
        Assert.Equal(1, report.LegacyBacklog);                 // c-legacy
        Assert.False(report.Passes);
    }

    // ── EF-vertaalbaarheid van de audit-queries (conventie: geen Contains(char),
    //    bewezen vertaalbaar via ToQueryString, zonder database) ───────────────
    private static RbRulesDbContext NpgsqlCtx() => new(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseNpgsql("Host=localhost;Database=x;Username=x", o => o.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void AuditRelationSubquery_TranslatesToSql()
    {
        using var db = NpgsqlCtx();
        var cutoff = DateTimeOffset.UtcNow;
        var q = db.Relations.AsNoTracking()
            .Where(r => r.DetectedAt >= cutoff)
            .Where(r => !db.Assertions.Any(a =>
                a.FactKind == FactKinds.Relation
                && a.Subject == ProvenanceAuditService.RelationSubjectPrefix + r.Id));
        var sql = q.ToQueryString();
        Assert.Contains("assertion", sql);
    }

    [Fact]
    public void AuditEmbeddingQuery_TranslatesToSql()
    {
        using var db = NpgsqlCtx();
        // De échte productie-query (projectie EmbeddingRow + MissingNewProvenance-
        // filter), niet een handkopie — zo vangt de test een Npgsql-vertaalbreuk in
        // de audit zelf (bv. een helper-call die in de Where sluipt). Alle vijf
        // embedding-tabellen moeten vertalen.
        var queries = new ProvenanceAuditService(db).MissingNewEmbeddingQueries().ToList();
        Assert.Equal(5, queries.Count);
        foreach (var q in queries)
        {
            var sql = q.ToQueryString();
            Assert.Contains("embedding_content_hash", sql);
        }
    }

    [Fact]
    public void EmbeddingContentHashColumns_ExistOnAllEmbeddingTables()
    {
        using var db = NpgsqlCtx();
        foreach (var clr in new[] {
            typeof(Card), typeof(RuleChunk), typeof(Correction),
            typeof(Claim), typeof(KnowledgeDoc) })
        {
            var et = db.Model.FindEntityType(clr);
            Assert.NotNull(et);
            Assert.NotNull(et!.FindProperty(nameof(Card.EmbeddingContentHash)));
        }
    }
}
