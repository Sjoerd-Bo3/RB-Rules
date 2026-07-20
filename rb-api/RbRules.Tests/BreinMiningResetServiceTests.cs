using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Contract-tests voor de gerichte brein-mining-reset (#263). Bewijzen:
/// (a) na de reset is de interactie-laag leeg en zijn de kaarten NIET meer
/// gewatermerkt — getoetst met exact de query die
/// <see cref="BreinInteractionMiningService"/> gebruikt om verwerkte kaarten over
/// te slaan; (b) claims, primer, correcties, relaties, kaarten, regels en bans zijn
/// aantoonbaar ongemoeid; (c) de scope-keuze doet wat hij belooft (entiteiten en
/// predicaten blijven staan in de smalle scope, gaan mee in de brede); (d) de
/// MiningRun-historie blijft als provenance-baseline staan. De database is EF
/// InMemory (Vector-kolommen als tekst, patroon KnowledgeRegenerationServiceTests)
/// — dit bewijst de scope, geen pgvector-gedrag.</summary>
public class BreinMiningResetServiceTests
{
    private const string MinedCardId = "ogn-011-298";
    private const string UnminedCardId = "ogn-012-298";

    // ── (a) de interactie-laag + het watermark ────────────────────────────

    [Fact]
    public async Task Reset_LeegtDeInteractieLaag()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var r = await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        Assert.Equal(1, r.Interactions);
        Assert.Equal(1, r.Conditions);
        Assert.Equal(1, r.Decisions);
        Assert.Equal(1, r.Assertions);

        Assert.Equal(0, await db.Interactions.CountAsync());
        Assert.Equal(0, await db.InteractionConditions.CountAsync());
        Assert.Equal(0, await db.InteractionDecisions.CountAsync());
        Assert.Equal(0, await db.Assertions.CountAsync(a => a.FactKind == FactKinds.Interaction));
    }

    [Fact]
    public async Task Reset_HaaltHetWatermarkVanDeKaartenWeg()
    {
        // Dé kern van #263: het watermark is "deze kaart leverde ooit een
        // interactie-Assertion op". Zolang die rij bestaat, slaat de miner de kaart
        // over en is een verbeterde extractie niet te meten. Getoetst met exact de
        // selectie uit BreinInteractionMiningService.
        using var db = NewDb();
        await SeedEverythingAsync(db);

        Assert.Contains(MinedCardId, await MinedCardIdsAsync(db));

        await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        Assert.Empty(await MinedCardIdsAsync(db));
        // De hele pool staat weer klaar — inclusief de kaart die al gemined was.
        var pool = await db.Cards.AsNoTracking().Select(c => c.RiftboundId).ToListAsync();
        Assert.Contains(MinedCardId, pool);
        Assert.Contains(UnminedCardId, pool);
    }

    [Fact]
    public async Task Reset_LichtPoortGrafstenen_MaarLaatAdminOordelenStaan()
    {
        // De grafstenen van de OUDE poort blokkeren precies de dedupe-sleutels die
        // de nieuwe extractie opnieuw moet mogen aandragen; lichten (niet
        // verwijderen) houdt het audit-spoor heel (#236). Een admin-oordeel is
        // mensenwerk en blijft blokkeren.
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var r = await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        Assert.Equal(1, r.TombstonesLifted);
        Assert.Equal(2, await db.RejectionTombstones.CountAsync()); // niets hard-deleted
        Assert.True((await db.RejectionTombstones.SingleAsync(t => t.Actor == "gate")).Lifted);
        Assert.False((await db.RejectionTombstones.SingleAsync(t => t.Actor == "admin")).Lifted);
    }

    // ── (b) alles daarbuiten blijft ongemoeid ─────────────────────────────

    [Fact]
    public async Task Reset_RaaktClaimsPrimerCorrectiesEnRelatiesNooitAan()
    {
        // De reden dat deze job naast `regenerateknowledge` bestaat: die wist juist
        // wél claims/primer/correcties/relaties en is daarmee veel te grof (#263).
        using var db = NewDb();
        await SeedEverythingAsync(db);

        await new BreinMiningResetService(db).ResetAsync(BreinResetScope.InteractionsAndEntities);

        Assert.Equal(1, await db.Claims.CountAsync());
        Assert.Equal(1, await db.ClaimSources.CountAsync());
        Assert.Equal(1, await db.Corrections.CountAsync());
        Assert.Equal(1, await db.KnowledgeDocs.CountAsync(k => k.Kind == "primer"));
        Assert.Equal(1, await db.Relations.CountAsync());
    }

    [Fact]
    public async Task Reset_RaaktBronOfFeitelijkeTabellenNooitAan()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        await new BreinMiningResetService(db).ResetAsync(BreinResetScope.InteractionsAndEntities);

        Assert.Equal(1, await db.Sources.CountAsync());
        Assert.Equal(1, await db.Documents.CountAsync());
        Assert.Equal(1, await db.RuleChunks.CountAsync());
        Assert.Equal(2, await db.Cards.CountAsync());
        Assert.Equal(1, await db.Errata.CountAsync());
        Assert.Equal(1, await db.BanEntries.CountAsync());
    }

    [Fact]
    public async Task Reset_LaatDeOudeLexicaleInteractieLaagStaan()
    {
        // CardInteraction (job "interacties", FactKind card_interaction) is een
        // ANDERE laag dan de brein-mining van #226 — die valt buiten deze reset,
        // anders zou een gerichte actie stilzwijgend een tweede pijplijn slopen.
        using var db = NewDb();
        await SeedEverythingAsync(db);

        await new BreinMiningResetService(db).ResetAsync(BreinResetScope.InteractionsAndEntities);

        Assert.Equal(1, await db.CardInteractions.CountAsync());
        Assert.Equal(1, await db.Assertions.CountAsync(a => a.FactKind == FactKinds.CardInteraction));
    }

    // ── (c) de scope-keuze ────────────────────────────────────────────────

    [Fact]
    public async Task SmalleScope_LaatEntiteitenEnPredicatenStaan()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var r = await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        Assert.Equal(0, r.Predicates);
        Assert.Equal(0, r.Entities);
        Assert.Equal(1, await db.MechanicPredicates.CountAsync());
        Assert.Equal(2, await db.CanonicalEntities.CountAsync());
        Assert.Equal(1, await db.MergeCandidates.CountAsync());
        Assert.Equal(1, await db.MergeDecisions.CountAsync());
    }

    [Fact]
    public async Task BredeScope_WistOokEntiteitenPredicatenEnMergeHistorie()
    {
        // #250 moet schoon te testen zijn: de canonieke laag is deterministisch
        // herbouwbaar met de job "breinentiteiten". De merge-historie moet mee —
        // haar FK's staan op Restrict, dus de entiteiten laten zich anders niet
        // verwijderen; dat verlies staat expliciet in de telling en de UI-tekst.
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var r = await new BreinMiningResetService(db).ResetAsync(
            BreinResetScope.InteractionsAndEntities);

        Assert.Equal(1, r.Predicates);
        Assert.Equal(2, r.Entities);
        Assert.Equal(1, r.MergeCandidates);
        Assert.Equal(1, r.MergeDecisions);

        Assert.Equal(0, await db.MechanicPredicates.CountAsync());
        Assert.Equal(0, await db.CanonicalEntities.CountAsync());
        Assert.Equal(0, await db.MergeCandidates.CountAsync());
        Assert.Equal(0, await db.MergeDecisions.CountAsync());
        // De interactie-laag gaat in deze scope óók mee (hij is de basis-scope).
        Assert.Equal(0, await db.Interactions.CountAsync());
    }

    // ── (d) provenance-baseline + grootboek ───────────────────────────────

    [Fact]
    public async Task Reset_BehoudtDeMiningRunHistorieAlsBaseline()
    {
        // Bewuste keuze (#263): de MiningRun is de PROV-O-Activity met model,
        // prompt-versie en tellingen — precies de baseline waartegen de #249-
        // verbetering gemeten wordt. Mee-wissen zou het meetdoel slopen.
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var r = await new BreinMiningResetService(db).ResetAsync(
            BreinResetScope.InteractionsAndEntities);

        Assert.Equal(2, r.MiningRunsKept); // interaction + mechanic
        Assert.Equal(2, await db.MiningRuns.CountAsync());
    }

    [Fact]
    public async Task Reset_LogtAantallenEnScopeNaarRunLog()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        var log = await db.RunLogs.SingleAsync(l => l.Kind == BreinMiningResetService.LedgerKind);
        Assert.Equal("ok", log.Status);
        Assert.Equal("interacties", log.Ref);
        Assert.Contains("1 interacties", log.Detail);
        Assert.Contains("1 interactie-assertions verwijderd", log.Detail);
        Assert.Contains("BEHOUDEN", log.Detail);
    }

    [Fact]
    public async Task Reset_IsIdempotent_TweedeRunVindtNietsMeer()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);
        var svc = new BreinMiningResetService(db);

        await svc.ResetAsync(BreinResetScope.InteractionsAndEntities);
        var again = await svc.ResetAsync(BreinResetScope.InteractionsAndEntities);

        Assert.Equal(0, again.Interactions);
        Assert.Equal(0, again.Conditions);
        Assert.Equal(0, again.Decisions);
        Assert.Equal(0, again.Assertions);
        Assert.Equal(0, again.TombstonesLifted);
        Assert.Equal(0, again.Predicates);
        Assert.Equal(0, again.Entities);
    }

    // ── de reset mag nooit vanzelf meeliften ──────────────────────────────

    [Fact]
    public async Task AllesBijwerken_BevatDeResetNiet()
    {
        // Harde eis (#263): nooit onderdeel van "alles". Gedraaid op een lege
        // container — élke stap faalt dan met "FOUT", maar de STAPLABELS staan
        // gewoon in het detail. Zou iemand de reset als stap toevoegen, dan zou
        // zijn label hier opduiken.
        await using var sp = new ServiceCollection().BuildServiceProvider();

        var outcome = await JobCatalog.Find("all")!.Run(sp, _ => { }, CancellationToken.None);

        Assert.DoesNotContain("brein", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terugzetten", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        // Sanity: de bekende keten staat er wél in (anders bewijst de test niets).
        Assert.Contains("kaarten", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interacties", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- testinfra --------------------------------------------------------

    /// <summary>Exact het watermark-filter uit <see cref="BreinInteractionMiningService"/>:
    /// welke kaarten beschouwt de miner als "al verwerkt"?</summary>
    private static async Task<List<string>> MinedCardIdsAsync(RbRulesDbContext db)
    {
        var refs = await db.Assertions.AsNoTracking()
            .Where(a => a.FactKind == FactKinds.Interaction)
            .Select(a => a.DerivedFromRef)
            .Distinct()
            .ToListAsync();
        return refs
            .Select(r => BrainRef.TryParse(r, out var br) && br.Kind == BrainRefKind.Card ? br.Key : null)
            .Where(k => k is not null).Select(k => k!)
            .Distinct().ToList();
    }

    private static async Task SeedEverythingAsync(RbRulesDbContext db)
    {
        const string sourceId = "core-rules-pdf";
        db.Sources.Add(new Source
        {
            Id = sourceId, Name = "Core Rules", Url = "https://example.test/rules",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "daily",
        });

        var doc = new Document { SourceId = sourceId, Content = "regeltekst", ContentHash = "h1" };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = sourceId, SectionCode = "7.4",
            ChunkIndex = 0, Text = "Deflect reduces combat damage.",
        });
        db.Cards.Add(new Card
        {
            RiftboundId = MinedCardId, Name = "Shen", Domains = [], Tags = [],
            Mechanics = ["Deflect"], TextPlain = "Deflect.",
        });
        db.Cards.Add(new Card
        {
            RiftboundId = UnminedCardId, Name = "Yasuo", Domains = [], Tags = [],
            Mechanics = ["Accelerate"], TextPlain = "Accelerate.",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Shen", NewText = "nieuwe tekst", SourceUrl = "https://example.test/errata",
        });
        db.BanEntries.Add(new BanEntry
        {
            Name = "Shen", Kind = "card", SourceUrl = "https://example.test/bans",
        });

        // Andere afgeleide lagen — nooit doel van deze reset (dat is juist het punt).
        var claim = new Claim
        {
            TopicType = "concept", TopicRef = "mulligan",
            Statement = "You may swap your starting hand once.",
            TrustScore = 0.5, OfficialStatus = "unclear",
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = sourceId, Url = "https://example.test/gids",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "concept", Ref = "mulligan", Text = "You may mulligan once.",
            Status = "verified", Provenance = "chat-ruling:admin",
        });
        db.KnowledgeDocs.Add(new KnowledgeDoc
        {
            Kind = "primer", Topic = "combat", Title = "Combat",
            Body = "Combat is about damage.", Status = "approved",
        });
        db.Relations.Add(new Relation
        {
            FromRef = "mechanic:Deflect", ToRef = "concept:combat", Kind = "counters",
            Explanation = "Deflect reduces combat damage.", Provenance = "concept:combat",
            Trust = 0.75,
        });

        // De OUDE lexicale interactie-laag (job "interacties") — andere pijplijn.
        db.CardInteractions.Add(new CardInteraction
        {
            CardAId = MinedCardId, CardBId = UnminedCardId, Kind = "synergy",
            Explanation = "werkt samen",
        });

        // Provenance-runs: één per fact-kind, allebei te behouden.
        var interactionRun = new MiningRun
        {
            Id = Ulid.NewUlid(), Kind = FactKinds.Interaction,
            LlmModel = "claude-sonnet-4-6",
            PromptVersion = BreinInteractionMiningService.PromptVersion,
        };
        var mechanicRun = new MiningRun
        {
            Id = Ulid.NewUlid(), Kind = FactKinds.Mechanic, LlmModel = "claude-sonnet-4-6",
        };
        db.MiningRuns.AddRange(interactionRun, mechanicRun);
        await db.SaveChangesAsync();

        // Canonieke laag (#225/#250) met een merge-tombstone erin: het herstelpad
        // en de self-FK moeten de reset overleven qua volgorde.
        var deflect = new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = "Deflect",
            Status = CanonicalEntityStatus.Canonical, CreatedByRunId = mechanicRun.Id,
        };
        db.CanonicalEntities.Add(deflect);
        await db.SaveChangesAsync();
        var deflecting = new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = "Deflecting",
            Status = CanonicalEntityStatus.Merged, MergedIntoId = deflect.Id,
            MergedAt = DateTimeOffset.UtcNow, CreatedByRunId = mechanicRun.Id,
        };
        db.CanonicalEntities.Add(deflecting);
        await db.SaveChangesAsync();

        db.MergeDecisions.Add(new MergeDecision
        {
            SourceEntityId = deflecting.Id, TargetEntityId = deflect.Id,
            RunId = mechanicRun.Id, DecidedBy = "admin", Memo = "zelfde keyword",
            MovedAltLabels = ["Deflecting"],
        });
        db.MergeCandidates.Add(new MergeCandidate
        {
            EntityAId = deflect.Id, EntityBId = deflecting.Id,
            Verdict = "review", Reason = "trigram 0.58", RunId = mechanicRun.Id,
        });
        db.MechanicPredicates.Add(new MechanicPredicateAssertion
        {
            SubjectEntityId = deflect.Id, Predicate = MechanicPredicateKinds.Prevents,
            ObjectToken = "damage", CreatedByRunId = mechanicRun.Id,
        });

        // De brein-mining-laag zelf.
        var interaction = new Interaction
        {
            AgentRef = BrainRef.Mechanic("Deflect").Format(),
            PatientRef = BrainRef.Card(UnminedCardId).Format(),
            Kind = InteractionKinds.Counters,
            Status = InteractionStatus.Promoted,
            CreatedByRunId = interactionRun.Id,
        };
        db.Interactions.Add(interaction);
        await db.SaveChangesAsync();

        db.InteractionConditions.Add(new InteractionCondition
        {
            InteractionId = interaction.Id, OnKind = InteractionConditionKinds.Window,
            SubjectRole = InteractionRoles.Agent, Value = "Showdown",
        });
        db.InteractionDecisions.Add(new InteractionDecision
        {
            InteractionId = interaction.Id, Outcome = InteractionStatus.Promoted,
            Memo = "schema-ok; bewijszin gevonden", RunId = interactionRun.Id,
        });

        // Het watermark: DerivedFromRef = de focus-kaart.
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(), Subject = interaction.Ref.Format(),
            FactKind = FactKinds.Interaction, MiningRunId = interactionRun.Id,
            DerivedFromRef = BrainRef.Card(MinedCardId).Format(),
        });
        // Assertion van de ANDERE pijplijn — blijft staan.
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(), Subject = "card_interaction:1",
            FactKind = FactKinds.CardInteraction, MiningRunId = mechanicRun.Id,
            DerivedFromRef = BrainRef.Card(MinedCardId).Format(),
        });

        db.RejectionTombstones.Add(new RejectionTombstone
        {
            DedupeKey = InteractionDedupe.Key("mechanic:Deflect", "mechanic:Accelerate", "COUNTERS"),
            AgentRef = "mechanic:Deflect", PatientRef = "mechanic:Accelerate",
            Kind = InteractionKinds.Counters, Reason = "geen lexicale steun",
            Actor = "gate", RunId = interactionRun.Id,
        });
        db.RejectionTombstones.Add(new RejectionTombstone
        {
            DedupeKey = InteractionDedupe.Key("mechanic:Deflect", "mechanic:Tank", "GRANTS"),
            AgentRef = "mechanic:Deflect", PatientRef = "mechanic:Tank",
            Kind = InteractionKinds.Grants, Reason = "beheerder wees af",
            Actor = "admin", RunId = interactionRun.Id,
        });

        await db.SaveChangesAsync();
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; ResetAsync draait er wel in
            // (Postgres) — negeren volstaat (KnowledgeRegenerationServiceTests-patroon).
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (alleen opslag — vector-queries blijven buiten deze tests).</summary>
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
