using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Domein-kleurcodering per wijziging (#214): read-time afgeleid uit
/// de geraakte kaart(en) via de gestructureerde ban-/errata-laag. Eén
/// onderscheiden domein kleurt de streep; nul of ambigu → neutraal (null).</summary>
public class ChangeDomainsTests
{
    [Fact]
    public async Task Ban_NoemtEénVerbodenKaart_GeeftHaarDomein()
    {
        using var db = NewDb();
        Card(db, "ogn-001", "Rampage", "Fury");
        Ban(db, "Rampage", "ogn-001");
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(1, "ban", "Rampage wordt verboden in constructed.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Equal("Fury", result[1]);
    }

    [Fact]
    public async Task Errata_NoemtGeërrataKaart_GeeftHaarDomein()
    {
        using var db = NewDb();
        Card(db, "ogn-002", "Foresee", "Mind");
        db.Errata.Add(new Erratum
        {
            CardName = "Foresee", CardRiftboundId = "ogn-002",
            NewText = "…", SourceUrl = "https://example.test/errata",
        });
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(2, "errata", "Foresee krijgt een tekstcorrectie.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Equal("Mind", result[2]);
    }

    [Fact]
    public async Task Ban_NoemtKaartenUitTweeDomeinen_GeeftNull()
    {
        using var db = NewDb();
        Card(db, "ogn-001", "Rampage", "Fury");
        Card(db, "ogn-003", "Discord", "Chaos");
        Ban(db, "Rampage", "ogn-001");
        Ban(db, "Discord", "ogn-003");
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(3, "ban", "Rampage en Discord worden beide verboden.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Null(result[3]);
    }

    [Fact]
    public async Task Ban_KaartMetTweeDomeinen_GeeftNull()
    {
        using var db = NewDb();
        Card(db, "ogn-004", "Twinstrike", "Fury", "Body");
        Ban(db, "Twinstrike", "ogn-004");
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(4, "ban", "Twinstrike wordt verboden.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Null(result[4]);
    }

    [Fact]
    public async Task CoreRule_WordtNooitGeresolved_OokAlNoemtHijEenKaart()
    {
        using var db = NewDb();
        Card(db, "ogn-001", "Rampage", "Fury");
        Ban(db, "Rampage", "ogn-001");
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(5, "core-rule", "Voorbeeld met Rampage in §201.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Null(result[5]);
    }

    [Fact]
    public async Task Ban_KaartnaamNietInTekst_GeeftNull()
    {
        using var db = NewDb();
        Card(db, "ogn-001", "Rampage", "Fury");
        Ban(db, "Rampage", "ogn-001");
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(6, "ban", "Twee kaarten verboden in constructed.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Null(result[6]);
    }

    [Fact]
    public async Task Ban_KaartNietInStructuredLaag_TeltNietMee()
    {
        // De kandidatenpoel is alleen de daadwerkelijk verboden kaarten; een
        // change die een niet-verboden kaart noemt krijgt geen domein.
        using var db = NewDb();
        Card(db, "ogn-005", "Yasuo", "Body");
        // Yasuo staat NIET in BanEntries.
        await db.SaveChangesAsync();
        var change = new ChangeTextRow(7, "ban", "Yasuo besproken maar niet verboden.", null, null);

        var result = await ChangeDomains.ResolveAsync(db, [change]);

        Assert.Null(result[7]);
    }

    [Fact]
    public async Task LegeChangeset_GeeftLegeMap()
    {
        using var db = NewDb();
        var result = await ChangeDomains.ResolveAsync(db, []);
        Assert.Empty(result);
    }

    private static void Card(RbRulesDbContext db, string id, string name, params string[] domains) =>
        db.Cards.Add(new Card { RiftboundId = id, Name = name, Domains = domains });

    private static void Ban(RbRulesDbContext db, string name, string cardId) =>
        db.BanEntries.Add(new BanEntry
        {
            Name = name, CardRiftboundId = cardId, Kind = "card",
            SourceUrl = "https://example.test/bans",
        });

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
