using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Bron-naam + gesaniteerde link + opmerking op de correcties-
/// reviewqueue (#184): AdminOverviewService.CorrectionsAsync resolvet de
/// bron-naam voor clarify-mining-Corrections via hun Provenance
/// ("clarify-mining:{sourceId}", de enige ontstaanswijze met een
/// resolveerbare Source-rij) en rekent SourceRefSafe uit via UrlGuard.Check —
/// dezelfde sanitize-vóór-{@html}-conventie als de claims-bewijsvoering.</summary>
public class AdminOverviewCorrectionsTests
{
    [Fact]
    public async Task CorrectionsAsync_ClarifyMiningProvenance_ResolvedSourceName()
    {
        using var db = NewDb();
        db.Sources.Add(Official(
            "playriftbound-com-unleashed-rules-faq", "Unleashed Rules FAQ"));
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "Legion betekent...",
            Provenance = "clarify-mining:playriftbound-com-unleashed-rules-faq",
            SourceRef = "https://playriftbound.com/en-us/news/faq",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var items = await new AdminOverviewService(db).CorrectionsAsync();

        var item = Assert.Single(items);
        Assert.Equal("Unleashed Rules FAQ", item.SourceName);
        Assert.True(item.SourceRefSafe);
    }

    [Fact]
    public async Task CorrectionsAsync_NonClarifyMiningProvenance_NoSourceNameResolved()
    {
        // Review-notitie-promotie (#124) en chat-rulings hebben geen
        // resolveerbare Source-rij achter hun Provenance — de UI valt terug
        // op de kale SourceRef, geen kapotte/foute naam.
        using var db = NewDb();
        db.Corrections.Add(new Correction
        {
            Scope = "claim", Ref = "claim:1", Text = "Zo zit het wél.",
            Provenance = "review-notitie", SourceRef = null,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new AdminOverviewService(db).CorrectionsAsync());

        Assert.Null(item.SourceName);
        Assert.False(item.SourceRefSafe); // geen SourceRef ⇒ niets om te linken
    }

    [Fact]
    public async Task CorrectionsAsync_OnveiligeSourceRef_NietAlsVeiligeLinkGemarkeerd()
    {
        // Een vrije citatie (geen URL) of een geweigerd schema (UrlGuard) mag
        // de UI nooit als klikbare link tonen (sanitize vóór {@html}).
        using var db = NewDb();
        db.Corrections.Add(new Correction
        {
            Scope = "answer", Ref = "up", Text = "Bevestigd in Discord.",
            SourceRef = "Discord #rulings, 2026-05-01", Status = "verified",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "answer", Ref = "up", Text = "Interne link.",
            SourceRef = "javascript:alert(1)", Status = "verified",
        });
        await db.SaveChangesAsync();

        var items = await new AdminOverviewService(db).CorrectionsAsync();

        Assert.All(items, i => Assert.False(i.SourceRefSafe));
    }

    [Fact]
    public async Task CorrectionsAsync_ReviewNoteEnStatusReason_KomenMee()
    {
        using var db = NewDb();
        db.Corrections.Add(new Correction
        {
            Scope = "concept", Ref = "Reflection tokens", Text = "...",
            Status = "unverified", StatusReason = "onderwerp niet herkend",
            ReviewNote = "dit is eigenlijk mechanic:Legion",
        });
        await db.SaveChangesAsync();

        var item = Assert.Single(await new AdminOverviewService(db).CorrectionsAsync());

        Assert.Equal("onderwerp niet herkend", item.StatusReason);
        Assert.Equal("dit is eigenlijk mechanic:Legion", item.ReviewNote);
    }

    private static Source Official(string id, string name) => new()
    {
        Id = id, Name = name, Url = $"https://example.com/{id}", Type = "official",
        TrustTier = 1, Rank = 100, Parser = "html", Cadence = "weekly",
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de andere AdminOverview-tests).</summary>
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
