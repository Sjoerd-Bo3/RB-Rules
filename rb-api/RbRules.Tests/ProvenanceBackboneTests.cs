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
        var q = db.Cards.AsNoTracking()
            .Where(c => c.Embedding != null)
            .Where(c => c.EmbeddingContentHash != null
                && (c.EmbeddingModel == null || c.EmbeddingModel != EmbeddingConfig.Model));
        var sql = q.ToQueryString();
        Assert.Contains("embedding_content_hash", sql);
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
