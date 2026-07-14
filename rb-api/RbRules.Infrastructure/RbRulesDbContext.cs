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
    public DbSet<Relation> Relations => Set<Relation>();
    public DbSet<RelationKind> RelationKinds => Set<RelationKind>();
    public DbSet<SourceProposal> SourceProposals => Set<SourceProposal>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();

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
            // Quota-teller (#42): "vragen van gebruiker X vandaag".
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<AskTrace>(e =>
        {
            e.ToTable("ask_trace");
            e.HasIndex(x => x.CreatedAt);
            // Eigen ask-geschiedenis (#157): laatste N op user_id resp. ip_hash.
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.IpHash, x.CreatedAt });
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
            e.HasIndex(x => x.Status);
        });

        // Dynamische relaties (#116): voorstellen + kind-vocabulaire.
        b.Entity<Relation>(e =>
        {
            e.ToTable("relation");
            // Eén voorstel per gerichte (van, naar, kind)-combinatie — de
            // service dedupet genormaliseerd, de index borgt het hard.
            e.HasIndex(x => new { x.FromRef, x.ToRef, x.Kind }).IsUnique();
            e.HasIndex(x => x.Status);
        });

        b.Entity<RelationKind>(e =>
        {
            e.ToTable("relation_kind");
            e.HasIndex(x => x.Kind).IsUnique();
            e.HasIndex(x => x.Status);
        });

        // Piltover Archive-decks (#15): PaId is de idempotentie-sleutel van
        // de ingest; kaartregels verdwijnen met hun deck (cascade).
        b.Entity<Deck>(e =>
        {
            e.ToTable("deck");
            e.HasIndex(x => x.PaId).IsUnique();
        });

        b.Entity<DeckCard>(e =>
        {
            e.ToTable("deck_card");
            e.HasOne(x => x.Deck).WithMany().HasForeignKey(x => x.DeckId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DeckId);
            // Meta-vragen straks ("populair in N% van recente decks", #15
            // fase 2) zoeken op kaart.
            e.HasIndex(x => x.CanonicalRiftboundId);
        });

        b.Entity<SourceProposal>(e =>
        {
            e.ToTable("source_proposal");
            // Eén voorstel per URL — dedupe over runs heen (de service
            // vergelijkt genormaliseerd; de index borgt het hard).
            e.HasIndex(x => x.Url).IsUnique();
            e.HasIndex(x => x.Status);
        });

        // Accounts (#42). Bewust "app_user" en niet het "user" uit het issue:
        // user is een gereserveerd woord in Postgres en zou elke handmatige
        // query tot quoten dwingen.
        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("user_session");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LoginToken>(e =>
        {
            e.ToTable("login_token");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.Email);
        });

        // Passkeys (#109): credential-opslag + lopende WebAuthn-ceremonies.
        b.Entity<PasskeyCredential>(e =>
        {
            e.ToTable("passkey_credential");
            // De credential-id komt van de authenticator en is wereldwijd
            // uniek; login zoekt hierop (bytea-vergelijking, zie DbModelTests).
            e.HasIndex(x => x.CredentialId).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PasskeyChallenge>(e =>
        {
            e.ToTable("passkey_challenge");
            e.HasIndex(x => x.TokenHash).IsUnique();
        });
    }
}
