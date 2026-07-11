using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>EF Core-context. Tabel-/kolomnamen zijn snake_case en identiek aan
/// het PoP-schema, zodat bestaande data 1-op-1 migreert. Vector-kolommen zijn
/// GETYPT op EmbeddingConfig.Dimensions (audit-fix: geen dimensieloze vectors).</summary>
public class RbRulesDbContext(DbContextOptions<RbRulesDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<CardSet> CardSets => Set<CardSet>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<RuleChunk> RuleChunks => Set<RuleChunk>();
    public DbSet<RunLog> RunLogs => Set<RunLog>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<BanEntry> BanEntries => Set<BanEntry>();
    public DbSet<Erratum> Errata => Set<Erratum>();
    public DbSet<CardInteraction> CardInteractions => Set<CardInteraction>();
    public DbSet<SimilarityExplanation> SimilarityExplanations => Set<SimilarityExplanation>();
    public DbSet<AskMetric> AskMetrics => Set<AskMetric>();
    public DbSet<AskTrace> AskTraces => Set<AskTrace>();
    public DbSet<KnowledgeDoc> KnowledgeDocs => Set<KnowledgeDoc>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimSource> ClaimSources => Set<ClaimSource>();
    public DbSet<MechanicKeyword> MechanicKeywords => Set<MechanicKeyword>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        var vectorType = $"vector({EmbeddingConfig.Dimensions})";

        b.Entity<Source>(e =>
        {
            e.ToTable("source");
            e.HasKey(x => x.Id);
        });

        b.Entity<Document>(e =>
        {
            e.ToTable("document");
            e.HasIndex(x => new { x.SourceId, x.RetrievedAt });
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
        });

        b.Entity<Change>(e =>
        {
            e.ToTable("change");
            e.HasIndex(x => x.DetectedAt);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
        });

        b.Entity<Conflict>(e =>
        {
            e.ToTable("conflict");
            // Audit-fix: bron-verwijdering laat geen wees-conflicten meer achter.
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceAId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceBId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.WinnerSourceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Correction>(e =>
        {
            e.ToTable("correction");
            e.Property(x => x.Ref).HasColumnName("ref");
            e.Property(x => x.Embedding).HasColumnType(vectorType);
            e.HasIndex(x => x.Status);
        });

        b.Entity<CardSet>(e =>
        {
            e.ToTable("card_set");
            e.HasKey(x => x.SetId);
        });

        b.Entity<Card>(e =>
        {
            e.ToTable("card");
            e.HasKey(x => x.RiftboundId);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.SetId);
            e.Property(x => x.Embedding).HasColumnType(vectorType);
            // ANN-index voor semantisch kaartzoeken (S1).
            e.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
        });

        b.Entity<RuleChunk>(e =>
        {
            e.ToTable("rule_chunk");
            e.HasIndex(x => x.SourceId);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Embedding).HasColumnType(vectorType);
            e.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
        });

        b.Entity<RunLog>(e =>
        {
            e.ToTable("run_log");
            e.Property(x => x.Ref).HasColumnName("ref");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscription");
            e.HasKey(x => x.Endpoint);
        });

        b.Entity<BanEntry>(e =>
        {
            e.ToTable("ban_entry");
            e.HasIndex(x => x.CardRiftboundId);
        });

        b.Entity<Erratum>(e =>
        {
            e.ToTable("erratum");
            e.HasIndex(x => x.CardRiftboundId);
        });

        b.Entity<CardInteraction>(e =>
        {
            e.ToTable("card_interaction");
            e.HasIndex(x => x.CardAId);
            e.HasIndex(x => x.CardBId);
            e.HasIndex(x => new { x.CardAId, x.CardBId }).IsUnique();
        });

        b.Entity<SimilarityExplanation>(e =>
        {
            e.ToTable("similarity_explanation");
            e.HasIndex(x => new { x.CardAId, x.CardBId }).IsUnique();
        });

        b.Entity<AskMetric>(e =>
        {
            e.ToTable("ask_metric");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AskTrace>(e =>
        {
            e.ToTable("ask_trace");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<KnowledgeDoc>(e =>
        {
            e.ToTable("knowledge_doc");
            e.HasIndex(x => new { x.Kind, x.Topic }).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Embedding).HasColumnType(vectorType);
        });

        b.Entity<Claim>(e =>
        {
            e.ToTable("claim");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.TopicType, x.TopicRef });
            e.Property(x => x.Embedding).HasColumnType(vectorType);
            // ANN-index: dedupe/clustering én straks het retrieval-kanaal (#51).
            e.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
        });

        b.Entity<ClaimSource>(e =>
        {
            e.ToTable("claim_source");
            e.HasOne(x => x.Claim).WithMany().HasForeignKey(x => x.ClaimId)
                .OnDelete(DeleteBehavior.Cascade);
            // Bron-verwijdering laat geen wees-bewijs achter (Conflict-patroon);
            // de corroboratie-telling herstelt bij de volgende claims-run.
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            // Eén bron telt één keer per claim (corroboratie-integriteit).
            e.HasIndex(x => new { x.ClaimId, x.SourceId }).IsUnique();
        });

        b.Entity<MechanicKeyword>(e =>
        {
            e.ToTable("mechanic_keyword");
            e.HasIndex(x => x.Term).IsUnique();
        });
    }
}
