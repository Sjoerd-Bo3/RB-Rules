using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Errata-resolutie op temporele precedentie (#168): welke errata-
/// tekst NU geldt voor een kaart = hoogste TrustTier van de bron, dan
/// nieuwste EffectiveFrom — met DetectedAt als tie-break zolang EffectiveFrom
/// nog onbekend is (bijvoorbeeld vlak na de migratie).</summary>
public class CardDetailServiceErrataTests
{
    private const string CardId = "ogn-011-298";

    [Fact]
    public async Task GetAsync_SingleErratum_IsUsedAsIs()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Sources.Add(OfficialSource("s1", "https://example.com/errata-1"));
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Enige tekst.", SourceUrl = "https://example.com/errata-1",
        });
        await db.SaveChangesAsync();

        var detail = await Service(db).GetAsync(CardId);

        Assert.Equal("Enige tekst.", detail!.ErrataText);
    }

    [Fact]
    public async Task GetAsync_TwoOfficialSources_NewestEffectiveFromWins()
    {
        // Twee even gezaghebbende (trust 1) bronnen spreken elkaar tegen —
        // de nieuwste EffectiveFrom moet winnen, ook al is hij later gedetecteerd.
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Sources.Add(OfficialSource("oud", "https://example.com/errata-oud"));
        db.Sources.Add(OfficialSource("nieuw", "https://example.com/errata-nieuw"));
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Oude tekst.", SourceUrl = "https://example.com/errata-oud",
            EffectiveFrom = new DateOnly(2025, 1, 1),
            DetectedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), // later gedetecteerd
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Nieuwe tekst.", SourceUrl = "https://example.com/errata-nieuw",
            EffectiveFrom = new DateOnly(2026, 1, 1),
            DetectedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), // eerder gedetecteerd
        });
        await db.SaveChangesAsync();

        var detail = await Service(db).GetAsync(CardId);

        Assert.Equal("Nieuwe tekst.", detail!.ErrataText);
    }

    [Fact]
    public async Task GetAsync_OfficialBeatsCommunity_RegardlessOfDate()
    {
        // Gezag blijft primair: een verse community-bron wint nooit van een
        // oudere officiële bron (docs/KNOWLEDGE.md: officieel wint altijd).
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Sources.Add(OfficialSource("officieel", "https://example.com/errata-off"));
        db.Sources.Add(new Source
        {
            Id = "community", Name = "Community Mirror", Url = "https://example.com/errata-com",
            Type = "community", TrustTier = 3, Rank = 10, Parser = "html", Cadence = "weekly",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Officiële, oude tekst.", SourceUrl = "https://example.com/errata-off",
            EffectiveFrom = new DateOnly(2020, 1, 1),
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Community, verse tekst.", SourceUrl = "https://example.com/errata-com",
            EffectiveFrom = new DateOnly(2026, 6, 1),
        });
        await db.SaveChangesAsync();

        var detail = await Service(db).GetAsync(CardId);

        Assert.Equal("Officiële, oude tekst.", detail!.ErrataText);
    }

    [Fact]
    public async Task RulesAsync_ErrataList_IsOrderedByPrecedence_WinnerFirst()
    {
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Sources.Add(OfficialSource("oud", "https://example.com/errata-oud"));
        db.Sources.Add(OfficialSource("nieuw", "https://example.com/errata-nieuw"));
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Oude tekst.", SourceUrl = "https://example.com/errata-oud",
            EffectiveFrom = new DateOnly(2025, 1, 1),
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Nieuwe tekst.", SourceUrl = "https://example.com/errata-nieuw",
            EffectiveFrom = new DateOnly(2026, 1, 1),
        });
        await db.SaveChangesAsync();

        var rules = await Service(db).RulesAsync(CardId);

        Assert.Equal(2, rules!.Errata.Count);
        Assert.Equal("Nieuwe tekst.", rules.Errata[0].NewText);
        Assert.Equal(new DateOnly(2026, 1, 1), rules.Errata[0].EffectiveFrom);
        Assert.Equal("Oude tekst.", rules.Errata[1].NewText);
    }

    [Fact]
    public async Task GetAsync_MissingSourceRow_StillResolvesWithoutCrashing()
    {
        // Bron verwijderd na extractie (zeldzaam) — het erratum blijft
        // zichtbaar en verliest de TrustTier-vergelijking, geen crash.
        using var db = NewDb();
        db.Cards.Add(Card());
        db.Sources.Add(OfficialSource("nog-aanwezig", "https://example.com/errata-aanwezig"));
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Bron ontbreekt.", SourceUrl = "https://example.com/errata-verdwenen",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Test Kaart", CardRiftboundId = CardId,
            NewText = "Bron aanwezig.", SourceUrl = "https://example.com/errata-aanwezig",
        });
        await db.SaveChangesAsync();

        var detail = await Service(db).GetAsync(CardId);

        Assert.Equal("Bron aanwezig.", detail!.ErrataText);
    }

    [Fact]
    public async Task GetAsync_EqualTierNullDate_DeterministicByRankThenId_RegardlessOfInputOrder()
    {
        // Review-fix (#168): volledige gelijkstand op tier + (lege) EffectiveFrom.
        // De winnaar mag NIET van invoervolgorde of DetectedAt afhangen —
        // Source.Rank (bron-voorkeur) beslist, dan Erratum.Id. Twee DB's met
        // omgekeerde invoervolgorde én omgekeerde DetectedAt moeten dezelfde
        // "nu geldig"-tekst opleveren.
        async Task<string?> WinnerFor(bool reversed)
        {
            using var db = NewDb();
            db.Cards.Add(Card());
            db.Sources.Add(OfficialSource("laag", "https://example.com/errata-laag", rank: 80));
            db.Sources.Add(OfficialSource("hoog", "https://example.com/errata-hoog", rank: 100));
            var laag = new Erratum
            {
                CardName = "Test Kaart", CardRiftboundId = CardId,
                NewText = "Lage rank.", SourceUrl = "https://example.com/errata-laag",
                DetectedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            };
            var hoog = new Erratum
            {
                CardName = "Test Kaart", CardRiftboundId = CardId,
                NewText = "Hoge rank.", SourceUrl = "https://example.com/errata-hoog",
                DetectedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), // ouder — mag niet meetellen
            };
            // Invoervolgorde omdraaien wisselt ook de auto-increment Id-volgorde.
            if (reversed) { db.Errata.Add(laag); db.Errata.Add(hoog); }
            else { db.Errata.Add(hoog); db.Errata.Add(laag); }
            await db.SaveChangesAsync();
            return (await Service(db).GetAsync(CardId))!.ErrataText;
        }

        var a = await WinnerFor(reversed: false);
        var b = await WinnerFor(reversed: true);
        Assert.Equal("Hoge rank.", a); // hogere Source.Rank wint, ongeacht datum/detectie
        Assert.Equal(a, b);            // reproduceerbaar, onafhankelijk van invoervolgorde
    }

    // --- testinfra ---------------------------------------------------------

    private static Card Card() => new() { RiftboundId = CardId, Name = "Test Kaart", SetId = "OGN" };

    private static Source OfficialSource(string id, string url, int rank = 100) => new()
    {
        Id = id, Name = id, Url = url, Type = "official",
        TrustTier = 1, Rank = rank, Parser = "html", Cadence = "weekly",
    };

    private static CardDetailService Service(RbRulesDbContext db) => new(db, new CardResolver(db));

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde hulpconstructie als CardSyncRepairTests).</summary>
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
