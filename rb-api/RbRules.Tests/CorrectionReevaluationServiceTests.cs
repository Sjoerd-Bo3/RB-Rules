using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Her-evaluatie van één Correction op een beheerder-opmerking (#184):
/// draait de hybride poort (#177/#185) opnieuw voor dít ene item. Roept de
/// bestaande domeinlogica aan (ClarificationGrounding/ClaimTopicMapper/
/// ClarificationInformativeness) zonder ze te wijzigen — deze tests dekken de
/// nieuwe orkestratie, niet die onderliggende poort zelf (die staat al onder
/// test in ClarificationMiningServiceTests).</summary>
public class CorrectionReevaluationServiceTests
{
    private const string SourceId = "playriftbound-com-unleashed-rules-faq";
    private const string GroundedQuote = "Recall means you finalize an item on the chain";

    [Fact]
    public async Task ReevaluateAsync_AnkerCorrectieInOpmerking_PromoveertPendingNaarVerified()
    {
        // De LLM ankerde het onderwerp fout (typo "Recal", resolvet niet) —
        // de beheerder corrigeert met een ankerregel in de opmerking.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        db.MechanicKeywords.Add(new MechanicKeyword { Term = "Recall", Status = "accepted" });
        var correction = new Correction
        {
            Scope = "concept", Ref = "Recal",
            Text = $"Recall betekent finalizen op de chain.\n\nCitaat uit de bron: “{GroundedQuote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "onderwerp 'Recal' (concept) niet herkend",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "mechanic:Recall");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("mechanic", correction.Scope);
        Assert.Equal("Recall", correction.Ref);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.StatusReason);
        Assert.NotNull(correction.VerifiedAt);
        Assert.Equal("mechanic:Recall", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_OpmerkingZonderGeldigAnker_BlijftPending_MetOpmerkingBewaard()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "concept", Ref = "Onbekende term", Text = "Iets over een onbekende term.",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "onderwerp 'Onbekende term' (concept) niet herkend",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "dit klopt volgens mij toch echt wel");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status);
        Assert.Contains("niet herkend", correction.StatusReason);
        Assert.Equal("dit klopt volgens mij toch echt wel", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AfgewezenItem_BewaartOpmerking_HerEvalueertNiet()
    {
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "...",
            Provenance = $"clarify-mining:{SourceId}", Status = "rejected",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "toch even nakijken");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Contains("afgewezen", r.Reason);
        Assert.Equal("rejected", correction.Status); // geen stiekeme her-evaluatie op een tombstone
        Assert.Equal("toch even nakijken", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_GeenClarifyMiningOorsprong_MaarOnverified_BewaartAlleenOpmerking()
    {
        // Een niet-clarify-mining Correction is in de praktijk altijd meteen
        // verified (chat-ruling/review-notitie-promotie) — dit dekt de
        // defensieve tak voor een hypothetisch onverified geval zonder
        // brontekst: geen poort om tegen te draaien, alleen de opmerking bewaart.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "claim", Ref = "claim:1", Text = "Zo zit het wél.",
            Provenance = "review-notitie", Status = "unverified",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "extra toelichting");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status); // ongemoeid, geen poort van toepassing
        Assert.Equal("extra toelichting", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AlGeverifieerdMaarNietClarifyMining_DegradeertNooit()
    {
        // Chat-rulings/review-notitie-promoties zijn al verified — her-
        // evaluatie mag dat nooit terugdraaien, ongeacht de oorsprong.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "claim", Ref = "claim:1", Text = "Zo zit het wél.",
            Provenance = "review-notitie", Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "extra toelichting");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status); // ongemoeid
        Assert.Equal("extra toelichting", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AlGeverifieerdItem_DegradeertNooit_BewaartAlleenOpmerking()
    {
        // Zelfde no-demote-invariant als ClarificationMiningService.StoreAsync:
        // her-evaluatie op een al geverifieerde ruling mag hem nooit terugzetten
        // naar pending, ook niet als de brontekst inmiddels is verdwenen.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "Legion betekent finalizen.",
            Provenance = $"clarify-mining:{SourceId}", // geen Document gezaaid: zou "niet grounded" zijn
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, "nog even bevestigd");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
        Assert.Equal("nog even bevestigd", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_OnbekendId_GeeftNotFound()
    {
        using var db = NewDb();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(999, "opmerking");

        Assert.Equal(ReevaluateOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task ReevaluateAsync_ZonderOpmerking_HerToetstMetBestaandeOnderwerp()
    {
        // Geen nieuwe opmerking meegeven (leeg formulier "opnieuw evalueren")
        // draait de poort gewoon opnieuw met het bestaande Scope/Ref.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"Legion betekent finalizen.\n\nCitaat uit de bron: “{GroundedQuote.Replace("Recall", "Legion")}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db);

        var r = await svc.ReevaluateAsync(correction.Id, null);

        // "Legion" zit al in het seed-mechaniek-vocabulaire (MechanicMiner) ⇒
        // anchored; grounded via het citaat in de brontekst ⇒ verified.
        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.ReviewNote); // geen opmerking meegegeven ⇒ niets bewaard
    }

    private static async Task SeedFaqDocumentAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ", Url = "https://playriftbound.com/en-us/news/faq",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = SourceId,
            Content = $"Uitleg over Recall en Legion. {GroundedQuote}. "
                      + $"{GroundedQuote.Replace("Recall", "Legion")}.",
            ContentHash = "hash",
        });
        await db.SaveChangesAsync();
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als ClarificationMiningServiceTests).</summary>
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
