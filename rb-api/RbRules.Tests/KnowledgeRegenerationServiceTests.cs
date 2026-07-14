using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Contract-tests voor het wipe-mechanisme van de LLM-afgeleide
/// kennislaag (#187): EXACT de regenereerbare laag (claim, correction,
/// knowledge_doc kind=primer, relation) gaat weg, de mining-markers resetten
/// zodat her-mining vanzelf oppakt, en de bron-/mensenwerk-tabellen (source,
/// document zelf, rule_chunk, card, errata, ban_entry, deck, deck_card) én
/// het beheerder-gereviewde relation_kind-vocabulaire blijven letterlijk
/// ongewijzigd. De database is EF InMemory (Vector-kolommen als tekst
/// opgeslagen, zelfde patroon als ClaimMiningServiceTests) — dit bewijst de
/// scope van <see cref="KnowledgeRegenerationService.WipeAsync"/>, geen
/// pgvector-gedrag.</summary>
public class KnowledgeRegenerationServiceTests
{
    [Fact]
    public async Task WipeAsync_VerwijdertExactDeAfgeleideLaag()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var svc = new KnowledgeRegenerationService(db);
        var r = await svc.WipeAsync();

        Assert.Equal(1, r.Claims);
        Assert.Equal(2, r.Corrections); // verified én unverified — ALLE correcties, ook mensenwerk
        Assert.Equal(1, r.PrimerDocs);
        Assert.Equal(1, r.Relations);
        Assert.Equal(1, r.DocumentsReset);

        Assert.Equal(0, await db.Claims.CountAsync());
        Assert.Equal(0, await db.Corrections.CountAsync());
        Assert.Equal(0, await db.Relations.CountAsync());
        Assert.Equal(0, await db.KnowledgeDocs.CountAsync(k => k.Kind == "primer"));

        // Cascade (FK OnDelete(Cascade) op ClaimSource.ClaimId): het bewijsspoor
        // van de verwijderde claim verdwijnt mee — geen wees-rijen.
        Assert.Equal(0, await db.ClaimSources.CountAsync());
    }

    [Fact]
    public async Task WipeAsync_RaaktBronOfFeitelijkeTabellenNooitAan()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        var sourcesBefore = await db.Sources.CountAsync();
        var documentsBefore = await db.Documents.CountAsync();
        var ruleChunksBefore = await db.RuleChunks.CountAsync();
        var cardsBefore = await db.Cards.CountAsync();
        var errataBefore = await db.Errata.CountAsync();
        var bansBefore = await db.BanEntries.CountAsync();
        var decksBefore = await db.Decks.CountAsync();
        var deckCardsBefore = await db.DeckCards.CountAsync();
        // Geen wipe-doel maar wel aangrenzend risico: het beheerder-
        // gereviewde kind-vocabulaire is taxonomie, geen mining-output.
        var relationKindsBefore = await db.RelationKinds.CountAsync();

        await new KnowledgeRegenerationService(db).WipeAsync();

        Assert.Equal(sourcesBefore, await db.Sources.CountAsync());
        Assert.Equal(documentsBefore, await db.Documents.CountAsync());
        Assert.Equal(ruleChunksBefore, await db.RuleChunks.CountAsync());
        Assert.Equal(cardsBefore, await db.Cards.CountAsync());
        Assert.Equal(errataBefore, await db.Errata.CountAsync());
        Assert.Equal(bansBefore, await db.BanEntries.CountAsync());
        Assert.Equal(decksBefore, await db.Decks.CountAsync());
        Assert.Equal(deckCardsBefore, await db.DeckCards.CountAsync());
        Assert.Equal(relationKindsBefore, await db.RelationKinds.CountAsync());
    }

    [Fact]
    public async Task WipeAsync_LaatAndereKnowledgeDocKindsOngemoeid()
    {
        // Scope is EXACT kind=primer — mocht er ooit een ander kind bestaan
        // (het domeincomment noemt "later: claim-samenvatting…"), dan mag de
        // WHERE-filter dat niet meepakken.
        using var db = NewDb();
        db.KnowledgeDocs.Add(new KnowledgeDoc
        {
            Kind = "other", Topic = "t", Title = "Titel", Body = "tekst",
        });
        await db.SaveChangesAsync();

        await new KnowledgeRegenerationService(db).WipeAsync();

        Assert.Equal(1, await db.KnowledgeDocs.CountAsync(k => k.Kind == "other"));
    }

    [Fact]
    public async Task WipeAsync_ResetDeMiningMarkers_ZodatHerMiningOppakt()
    {
        using var db = NewDb();
        var doc = new Document
        {
            SourceId = SeedSource(db), Content = "tekst", ContentHash = "h",
            ClaimsMinedAt = DateTimeOffset.UtcNow, ClarifiedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await new KnowledgeRegenerationService(db).WipeAsync();

        var reloaded = await db.Documents.SingleAsync();
        Assert.Null(reloaded.ClaimsMinedAt);
        Assert.Null(reloaded.ClarifiedAt);
    }

    [Fact]
    public async Task WipeAsync_IsIdempotent_TweedeRunVindtNietsMeer()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);
        var svc = new KnowledgeRegenerationService(db);

        await svc.WipeAsync();
        var again = await svc.WipeAsync();

        Assert.Equal(0, again.Claims);
        Assert.Equal(0, again.Corrections);
        Assert.Equal(0, again.PrimerDocs);
        Assert.Equal(0, again.Relations);
        Assert.Equal(0, again.DocumentsReset);
    }

    [Fact]
    public async Task WipeAsync_LogtAantallenNaarRunLog()
    {
        using var db = NewDb();
        await SeedEverythingAsync(db);

        await new KnowledgeRegenerationService(db).WipeAsync();

        var log = await db.RunLogs.SingleAsync(
            l => l.Kind == KnowledgeRegenerationService.LedgerKind);
        Assert.Equal("ok", log.Status);
        Assert.Contains("1 claims", log.Detail);
        Assert.Contains("2 correcties", log.Detail);
        Assert.Contains("1 primer-docs", log.Detail);
        Assert.Contains("1 relaties", log.Detail);
    }

    // --- testinfra (patroon ClaimMiningServiceTests) ----------------------

    private static string SeedSource(RbRulesDbContext db)
    {
        const string id = "core-rules-pdf";
        if (!db.Sources.Local.Any(s => s.Id == id) && !db.Sources.Any(s => s.Id == id))
        {
            db.Sources.Add(new Source
            {
                Id = id, Name = "Core Rules", Url = "https://example.test/rules",
                Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "daily",
            });
        }
        return id;
    }

    private static async Task SeedEverythingAsync(RbRulesDbContext db)
    {
        var sourceId = SeedSource(db);

        var doc = new Document
        {
            SourceId = sourceId, Content = "regeltekst", ContentHash = "h1",
            ClaimsMinedAt = DateTimeOffset.UtcNow, ClarifiedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(); // Document.Id vast voor RuleChunk.DocumentId hieronder

        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = doc.Id, SourceId = sourceId, SectionCode = "7.4",
            ChunkIndex = 0, Text = "Deflect reduces combat damage.",
        });

        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-011-298", Name = "Shen", Domains = [], Tags = [],
        });

        db.Errata.Add(new Erratum
        {
            CardName = "Shen", NewText = "nieuwe tekst", SourceUrl = "https://example.test/errata",
        });

        db.BanEntries.Add(new BanEntry
        {
            Name = "Shen", Kind = "card", SourceUrl = "https://example.test/bans",
        });

        var deck = new Deck { PaId = "deck-1", SourceUrl = "https://example.test/decks/view/deck-1" };
        db.Decks.Add(deck);
        await db.SaveChangesAsync(); // Deck.Id vast voor DeckCard.DeckId hieronder

        db.DeckCards.Add(new DeckCard
        {
            DeckId = deck.Id, Section = "maindeck", CardCode = "OGN-011a", Quantity = 1,
        });

        // Afgeleide/regenereerbare laag — dit hoort na de wipe weg te zijn.
        var claim = new Claim
        {
            TopicType = "concept", TopicRef = "mulligan",
            Statement = "You may swap your starting hand once.",
            TrustScore = 0.5, OfficialStatus = "unclear",
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync(); // Claim.Id vast voor ClaimSource.ClaimId hieronder
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = sourceId, Url = "https://example.test/gids",
        });

        // ALLE correcties, ook een door-mens-geverifieerde (chat-ruling) —
        // de definitieve wipe-scope (issue-comment) gooit ook die weg.
        db.Corrections.Add(new Correction
        {
            Scope = "concept", Ref = "mulligan", Text = "You may mulligan once.",
            Status = "verified", Provenance = "chat-ruling:admin",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "rule_section", Ref = "7.4", Text = "unverified item",
            Status = "unverified", Provenance = "clarify-mining:core-rules-pdf",
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

        // Beheerder-gereviewd vocabulaire — geen mining-output, blijft staan.
        db.RelationKinds.Add(new RelationKind { Kind = "ontgrendelt", Status = "accepted" });

        await db.SaveChangesAsync();
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; WipeAsync draait er wel in
            // (Postgres) — negeren volstaat (BanErrataSyncServiceTests-patroon).
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
