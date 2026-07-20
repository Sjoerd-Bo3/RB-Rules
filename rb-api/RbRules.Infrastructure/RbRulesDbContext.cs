using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.GraphRag;
using RbRules.Domain.Reasoning;

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
    /// <summary>Beheerde instellingen (#254): overrides op de env-bootstrap-defaults.</summary>
    public DbSet<Setting> Settings => Set<Setting>();
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
    public DbSet<SourceFeed> SourceFeeds => Set<SourceFeed>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckCard> DeckCards => Set<DeckCard>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();
    public DbSet<BenchmarkQuestion> BenchmarkQuestions => Set<BenchmarkQuestion>();
    public DbSet<BenchmarkRun> BenchmarkRuns => Set<BenchmarkRun>();
    public DbSet<BenchmarkResult> BenchmarkResults => Set<BenchmarkResult>();
    // Provenance-ruggengraat (fase 0a, #233).
    public DbSet<MiningRun> MiningRuns => Set<MiningRun>();
    public DbSet<Assertion> Assertions => Set<Assertion>();
    // Canonieke entiteiten & entity-resolution (fase 1, #225).
    public DbSet<CanonicalEntity> CanonicalEntities => Set<CanonicalEntity>();
    public DbSet<MergeDecision> MergeDecisions => Set<MergeDecision>();
    public DbSet<MergeCandidate> MergeCandidates => Set<MergeCandidate>();
    // Reïficatie & gekwalificeerde relaties (fase 2, #226).
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<InteractionCondition> InteractionConditions => Set<InteractionCondition>();
    public DbSet<RejectionTombstone> RejectionTombstones => Set<RejectionTombstone>();
    public DbSet<InteractionDecision> InteractionDecisions => Set<InteractionDecision>();
    public DbSet<InteractionAudit> InteractionAudits => Set<InteractionAudit>();
    // Getypeerde mechanic-predicaten (fase 5, #229): het structurele signaal voor
    // de abductieve hypothese-motor.
    public DbSet<MechanicPredicateAssertion> MechanicPredicates => Set<MechanicPredicateAssertion>();
    // Redeneer-laag (fase 3, #227): door de reasoner gedetecteerde tegenspraken.
    public DbSet<ReasoningConflict> ReasoningConflicts => Set<ReasoningConflict>();

    // GraphRAG-retrieval (fase 4, #228): het immutable auditspoor per /ask-antwoord.
    public DbSet<AnswerTrace> AnswerTraces => Set<AnswerTrace>();
    public DbSet<AnswerTraceSupport> AnswerTraceSupports => Set<AnswerTraceSupport>();

    // Governance & levenscyclus (fase 6, #230): geversioneerde ontologie, staging-
    // voorstellen en het geconsolideerde levenscyclus-gebeurtenis-log.
    public DbSet<Domain.Ontology.OntologyVersionRecord> OntologyVersions => Set<Domain.Ontology.OntologyVersionRecord>();
    public DbSet<Domain.Ontology.SchemaProposal> SchemaProposals => Set<Domain.Ontology.SchemaProposal>();
    public DbSet<LifecycleEvent> LifecycleEvents => Set<LifecycleEvent>();

    // Eval-industrialisatie (fase 7, #231): de per-klasse-baseline waartegen de
    // baseline-diff-gate diff't, en de rollup-samenvatting per harness-gate-run.
    public DbSet<EvalBaselineRecord> EvalBaselines => Set<EvalBaselineRecord>();
    public DbSet<EvalRunRecord> EvalRuns => Set<EvalRunRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        var vectorType = $"vector({EmbeddingConfig.Dimensions})";

        b.Entity<Source>(e =>
        {
            e.ToTable("source");
            e.HasKey(x => x.Id);
            // Herkomst (#167): feed-verwijdering laat de bron met FeedId =
            // null staan — een via de feed ontdekte bron blijft gewoon een
            // prima bron, alleen de herkomst-aanduiding vervalt.
            e.HasOne<SourceFeed>().WithMany().HasForeignKey(x => x.FeedId)
                .OnDelete(DeleteBehavior.SetNull);
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
            // Changeconsolidatie (#206): zelf-verwijzende, nullable FK naar de
            // primaire change. SetNull (Source.FeedId-patroon): verdwijnt de
            // primaire rij ooit (bv. handmatige feed-curatie), dan wordt de
            // secundaire gewoon weer een op zichzelf staand item in plaats
            // van een wees-verwijzing.
            e.HasOne<Change>().WithMany().HasForeignKey(x => x.ConsolidatedWithId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.Property(x => x.InteractionsMinedByRunId).HasMaxLength(Domain.Ulid.Length);
            // De focus-selectie van de interactie-mining vraagt precies naar de
            // nog-niet-verwerkte kaarten (#249-review).
            e.HasIndex(x => x.InteractionsMinedAt);
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

        // Beheerde instellingen (#254): sleutel/waarde, sleutel is de PK. Klein en
        // zelden gemuteerd — geen extra index nodig; de service leest de hele tabel
        // in één keer in de cache.
        b.Entity<Setting>(e =>
        {
            e.ToTable("setting");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(256);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
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
            // Relatie-triage (#199 v1): de triage-run filtert op "nog geen
            // aanbeveling" en de reviewqueue sorteert erop.
            e.HasIndex(x => x.Recommendation);
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

        // Bron-feeds (#167): index-pagina's die periodiek op nieuwe
        // artikel-URL's worden afgespeurd (FeedCrawlService).
        b.Entity<SourceFeed>(e =>
        {
            e.ToTable("source_feed");
            e.HasKey(x => x.Id);
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

        // Benchmark (#158): vaste vragenset + runs, strikt gescheiden van
        // ask_trace/ask_metric — zie AskService.AskOptions.Benchmark.
        b.Entity<BenchmarkQuestion>(e =>
        {
            e.ToTable("benchmark_question");
            e.HasIndex(x => x.ExternalKey).IsUnique();
        });

        b.Entity<BenchmarkRun>(e =>
        {
            e.ToTable("benchmark_run");
            e.HasIndex(x => x.StartedAt);
            // Model-sweep (#174): het sweep-overzicht groepeert per SweepId —
            // sparse index (de meeste rijen blijven null buiten een sweep).
            e.HasIndex(x => x.SweepId);
        });

        b.Entity<BenchmarkResult>(e =>
        {
            e.ToTable("benchmark_result");
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            // Vragen zijn vaste seed-data — een vraag verwijderen mag geen
            // historische resultaten meeslepen (audit-trail blijft leesbaar).
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.QuestionId);
        });

        // Provenance-ruggengraat (fase 0a, #233): PROV-O-Activity + gereïficeerd
        // feit-met-herkomst. Postgres is de bron van waarheid; de Neo4j-projectie
        // is idempotent herbouwbaar (GraphSyncService).
        b.Entity<MiningRun>(e =>
        {
            e.ToTable("mining_run");
            e.HasKey(x => x.Id);
            // "Welke feiten kwamen uit deze run" + tijd-sortering (ULID is al
            // sorteerbaar, maar StartedAt is de operationele as).
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.Kind);
        });

        b.Entity<Assertion>(e =>
        {
            e.ToTable("assertion");
            e.HasKey(x => x.Id);
            // WAS_GENERATED_BY: verwijderen van een run mag de afgeleide feiten
            // niet stil weesmaken — Restrict dwingt bewuste opschoning af
            // (provenance is geen wegwerp-administratie).
            e.HasOne(x => x.MiningRun).WithMany().HasForeignKey(x => x.MiningRunId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DerivedFromDocumentId)
                .OnDelete(DeleteBehavior.SetNull);
            // "Heeft dit feit al provenance?" — de Ring-A-audit zoekt op subject.
            e.HasIndex(x => new { x.FactKind, x.Subject });
            e.HasIndex(x => x.MiningRunId);
        });

        // Canonieke entiteiten & entity-resolution (fase 1, #225). Postgres is de
        // bron van waarheid; de Neo4j-projectie is idempotent herbouwbaar (MERGE op
        // de canonieke id). pg_trgm staat als extensie klaar voor het lexicale
        // schaal-pad (§3.2) — de fase-1-scorer draait in-memory en gate-consistent.
        b.HasPostgresExtension("pg_trgm");
        b.Entity<CanonicalEntity>(e =>
        {
            e.ToTable("canonical_entity");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Kind);
            e.HasIndex(x => x.Status);
            // Eén canonieke rij per (kind, label): de harde borg tegen duplicatie
            // (#1) náást de service-resolutie. Case-collisie is de service's taak
            // (genormaliseerd resolven vóór insert); dit vangt exacte duplicaten.
            e.HasIndex(x => new { x.Kind, x.CanonicalLabel }).IsUnique();
            e.Property(x => x.InteractionsMinedByRunId).HasMaxLength(Domain.Ulid.Length);
            // De subject-selectie van de mechanic-niveau-interactie-mining vraagt
            // precies naar de nog-niet-verwerkte entiteiten (#286).
            e.HasIndex(x => x.InteractionsMinedAt);
            e.Property(x => x.Embedding).HasColumnType(vectorType);
            // Tombstone-verwijzing naar de overlevende entiteit (self-FK). Restrict:
            // een doel met tombstones eromheen mag niet stil verdwijnen — dat zou
            // het herstelpad (unconsolidate) breken.
            e.HasOne<CanonicalEntity>().WithMany().HasForeignKey(x => x.MergedIntoId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MergeDecision>(e =>
        {
            e.ToTable("merge_decision");
            e.HasKey(x => x.Id);
            // "Welke merge(s) raakten deze entiteit" + het herstelpad-zoekpad.
            e.HasIndex(x => x.SourceEntityId);
            e.HasIndex(x => x.TargetEntityId);
            // Merge-beslissing verwijst naar entiteiten die als tombstone blijven
            // bestaan; nooit cascade-deleten (audit-spoor is heilig).
            e.HasOne<CanonicalEntity>().WithMany().HasForeignKey(x => x.SourceEntityId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CanonicalEntity>().WithMany().HasForeignKey(x => x.TargetEntityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MergeCandidate>(e =>
        {
            e.ToTable("merge_candidate");
            e.HasKey(x => x.Id);
            // Ongeordend uniek paar (service ordent A<B) — nooit twee spiegelrijen.
            e.HasIndex(x => new { x.EntityAId, x.EntityBId }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne<CanonicalEntity>().WithMany().HasForeignKey(x => x.EntityAId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CanonicalEntity>().WithMany().HasForeignKey(x => x.EntityBId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Reïficatie & gekwalificeerde relaties (fase 2, #226): de gereïficeerde
        // interactie is de canonieke opslagvorm (SoT in Postgres). De Neo4j-
        // projectie (:Interaction/:Condition + de RELATES_TO-cache) is idempotent
        // herbouwbaar uit deze rijen — de cache is nooit de bron.
        b.Entity<Interaction>(e =>
        {
            e.ToTable("interaction");
            e.HasKey(x => x.Id);
            // Eén gerichte (agent, patient, kind)-interactie: de service dedupet
            // genormaliseerd, de index borgt het hard tegen dubbele reïficaties.
            e.HasIndex(x => new { x.AgentRef, x.PatientRef, x.Kind }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.AgentRef);
            e.HasIndex(x => x.PatientRef);
        });

        b.Entity<InteractionCondition>(e =>
        {
            e.ToTable("interaction_condition");
            e.HasKey(x => x.Id);
            // Condities verdwijnen met hun interactie (ze bestaan er niet los van).
            e.HasOne(x => x.Interaction).WithMany(x => x.Conditions)
                .HasForeignKey(x => x.InteractionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.InteractionId);
        });

        b.Entity<RejectionTombstone>(e =>
        {
            e.ToTable("rejection_tombstone");
            e.HasKey(x => x.Id);
            // De poort raadpleegt de dedupe-sleutel vóór ze een kandidaat overweegt.
            e.HasIndex(x => x.DedupeKey);
            e.HasIndex(x => x.Lifted);
        });

        b.Entity<InteractionDecision>(e =>
        {
            e.ToTable("interaction_decision");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InteractionId);
        });

        // Steekproef-audit (#255): het oordeel van een sterker model over een
        // gepromoveerde interactie, als aparte meting met eigen provenance. Bewust
        // GEEN FK-cascade naar interaction: het oordeel is een audit-spoor en hoort
        // een eventuele opruiming van het feit te overleven.
        b.Entity<InteractionAudit>(e =>
        {
            e.ToTable("interaction_audit");
            e.HasKey(x => x.Id);
            // Het watermark-pad: "heeft deze interactie al een oordeel op deze
            // promptversie?" — de query van de 1-op-N-selectie.
            e.HasIndex(x => new { x.InteractionId, x.PromptVersion });
            e.HasIndex(x => x.AuditedAt);
            e.Property(x => x.RunId).HasMaxLength(Domain.Ulid.Length);
        });

        // Getypeerde mechanic-predicaten (fase 5, #229, §5): het structurele signaal
        // (triggers_on/prevents/grants/requires_target) waarop de abductieve hypothese-
        // motor redeneert. Postgres = SoT; gemined+gereviewd, elk predicaat draagt eigen
        // provenance en review-status (afzonderlijk weerlegbaar).
        b.Entity<MechanicPredicateAssertion>(e =>
        {
            e.ToTable("mechanic_predicate");
            e.HasKey(x => x.Id);
            // Eén (subject, predicaat, object) — de service dedupet genormaliseerd,
            // de unieke index borgt het hard tegen dubbele predicaten.
            e.HasIndex(x => new { x.SubjectEntityId, x.Predicate, x.ObjectToken }).IsUnique();
            e.HasIndex(x => x.Status);
            // De hypothese-motor leest de predicaten van een entiteit op.
            e.HasIndex(x => x.SubjectEntityId);
            // Predicaat verdwijnt met zijn canonieke entiteit (het bestaat er niet los
            // van); een merge behoudt de entiteit als tombstone, dus geen wees-predicaten.
            e.HasOne(x => x.SubjectEntity).WithMany().HasForeignKey(x => x.SubjectEntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Redeneer-laag (fase 3, #227, §5): door de reasoner gedetecteerde
        // tegenspraken. Postgres = SoT ook hier — de detectie draait tegen de
        // Neo4j-projectie, maar het resultaat leeft (herbouwbaar) in Postgres.
        // Bewust een eigen tabel naast bron-niveau "conflict" (die draagt FK's
        // naar source): een redeneer-tegenspraak verwijst naar graf-knopen via
        // BrainRefs, niet naar bron-rijen.
        b.Entity<ReasoningConflict>(e =>
        {
            e.ToTable("reasoning_conflict");
            e.HasKey(x => x.Id);
            // Idempotentie over runs heen: dezelfde tegenspraak opnieuw detecteren
            // maakt geen tweede rij (de service dedupet, de index borgt het hard).
            e.HasIndex(x => x.DedupeKey).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Channel);
            e.HasIndex(x => x.Kind);
        });

        // GraphRAG-AnswerTrace (fase 4, #228, §6/#236): immutable auditspoor. ULID-
        // PK (tijd-sorteerbaar); de dragende feiten hangen er als child-rijen aan,
        // cascade-verwijderd met de trace (het spoor is atomair, niet los te knippen).
        b.Entity<AnswerTrace>(e =>
        {
            e.ToTable("answer_trace");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Domain.Ulid.Length);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.PrimaryChannel);
            e.HasMany(x => x.Supports).WithOne(x => x.AnswerTrace!)
                .HasForeignKey(x => x.AnswerTraceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AnswerTraceSupport>(e =>
        {
            e.ToTable("answer_trace_support");
            e.HasKey(x => x.Id);
            e.Property(x => x.AnswerTraceId).HasMaxLength(Domain.Ulid.Length);
            e.HasIndex(x => x.AnswerTraceId);
            // "Verantwoord dit antwoord"-query (§6): welke antwoorden leunden op een
            // feit dat inmiddels deprecated/getombstoned is.
            e.HasIndex(x => x.SubjectRef);
        });

        // Governance & levenscyclus (fase 6, #230, §6). Postgres = SoT; de ontologie
        // is een geversioneerd, herbouwbaar artefact. De has-pending-ontology-poort
        // zelf is PUUR (code vs. checked-in baseline) — deze tabellen dragen de
        // runtime-historie en de reviewqueue, niet de CI-gate.
        b.Entity<Domain.Ontology.OntologyVersionRecord>(e =>
        {
            e.ToTable("ontology_version");
            e.HasKey(x => x.Id);
            // Eén rij per vastgelegde versie — de service sorteert semver in-memory
            // (string-sort ≠ semver-sort), de index borgt uniciteit hard.
            e.HasIndex(x => x.Version).IsUnique();
            e.HasIndex(x => x.AppliedAt);
        });

        b.Entity<Domain.Ontology.SchemaProposal>(e =>
        {
            e.ToTable("schema_proposal");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
            // Eén open voorstel per (soort, naam): de service dedupet, de index borgt
            // het hard tegen dubbele staging-voorstellen voor hetzelfde type.
            e.HasIndex(x => new { x.Kind, x.ProposedName }).IsUnique();
        });

        b.Entity<LifecycleEvent>(e =>
        {
            e.ToTable("lifecycle_event");
            e.HasKey(x => x.Id);
            // "Wat is er met dit feit gebeurd" (het geconsolideerde tombstone-spoor).
            e.HasIndex(x => new { x.SubjectRef, x.CreatedAt });
            e.HasIndex(x => x.ToState);
            e.HasIndex(x => x.Reverted);
        });

        // Eval-industrialisatie (fase 7, #231, spec §7). De baseline-diff-gate zelf is
        // PUUR (code vs. vastgelegde baseline); deze tabellen dragen de runtime-
        // baseline en de run-historie, niet de CI-gate-logica.
        b.Entity<EvalBaselineRecord>(e =>
        {
            e.ToTable("eval_baseline");
            e.HasKey(x => x.Id);
            e.Property(x => x.Ring).HasMaxLength(8);
            e.Property(x => x.QueryType).HasMaxLength(32);
            e.Property(x => x.Metric).HasMaxLength(48);
            // Eén actieve baseline per (ring × question_class × metric) — de gate
            // diff't tegen precies één cel; de index borgt dat hard.
            e.HasIndex(x => new { x.Ring, x.QueryType, x.Metric }).IsUnique();
        });

        b.Entity<EvalRunRecord>(e =>
        {
            e.ToTable("eval_run");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Domain.Ulid.Length);
            e.Property(x => x.Ring).HasMaxLength(8);
            // "Sluipende degradatie over runs" — sorteren op tijd, filteren op uitslag.
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Ring, x.Passed });
        });
    }

    /// <summary>Schrijfpoort (fase 0a, #233) — de .NET-helft van het dubbele
    /// write-guard: een <see cref="Assertion"/> die de provenance-shape niet
    /// haalt (geen WAS_GENERATED_BY of DERIVED_FROM) wordt hard geweigerd, ook
    /// als een caller de <see cref="AssertionProvenanceGuard"/> zou vergeten.
    /// Faalmodus #4 wordt zo een invariant, geen discipline.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAssertions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAssertions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAssertions()
    {
        foreach (var entry in ChangeTracker.Entries<Assertion>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var result = AssertionProvenanceGuard.Validate(entry.Entity);
            if (!result.IsValid)
                throw new InvalidOperationException(
                    $"Assertion '{entry.Entity.Id}' weigert de provenance-poort: {result.Reason}");
        }
    }
}
