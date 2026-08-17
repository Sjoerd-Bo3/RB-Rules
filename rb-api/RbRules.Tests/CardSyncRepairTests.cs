using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Reparatie van de riftcodex-bronwissel (#144): dubbelen op
/// (set, collector-nummer, variant-suffix) samenvoegen waarbij de canonieke
/// vorm wint en álle verwijzingen mee omhangen, ster-id's zonder tegenhanger
/// hernoemen, en streepjes-namen mét bewijs herstellen. Idempotent: de
/// tweede run wijzigt niets. Database is EF InMemory (servicetest-patroon);
/// transacties negeert die provider — de echte transactiegrens draait
/// alleen tegen Postgres.</summary>
public class CardSyncRepairTests
{
    [Fact]
    public async Task Repair_MergesStarDuplicateAndRepointsAllReferences()
    {
        using var db = NewDb();
        // Productie-situatie: de Riot-rij is de canonieke vorm, de
        // riftcodex-rij (ster-id, streepjes-naam) is de dubbel — met alle
        // soorten verwijzingen aan de dubbel hangend.
        var winner = Card("sfd-239-star-221", "Soraka, Wanderer");
        var dupe = Card("sfd-239*-221", "Soraka - Wanderer (Signature)");
        var other = Card("ogn-050-298", "Jinx, Loose Cannon");
        var variant = Card("sfd-239-221", "Soraka, Wanderer", variantOf: "sfd-239*-221");
        db.Cards.AddRange(winner, dupe, other, variant);
        db.BanEntries.Add(new BanEntry
        {
            Name = "Soraka, Wanderer", CardRiftboundId = "sfd-239*-221",
            Kind = "card", SourceUrl = "https://example.com/bans",
        });
        db.Errata.Add(new Erratum
        {
            CardName = "Soraka, Wanderer", CardRiftboundId = "sfd-239*-221",
            NewText = "Nieuwe tekst.", SourceUrl = "https://example.com/errata",
        });
        db.CardInteractions.Add(Interaction("ogn-050-298", "sfd-239*-221"));
        db.SimilarityExplanations.Add(new SimilarityExplanation
        {
            CardAId = "ogn-050-298", CardBId = "sfd-239*-221", Text = "lijkt op",
        });
        db.Corrections.Add(new Correction
        {
            Scope = "card", Ref = "sfd-239*-221", Text = "Ruling over Soraka.",
        });
        db.Relations.Add(new Relation
        {
            FromRef = "card:sfd-239*-221", ToRef = "mechanic:Deflect",
            Kind = "synergizes", Explanation = "uitleg", Provenance = "test",
        });
        db.Claims.Add(new Claim
        {
            TopicType = "card", TopicRef = "Soraka - Wanderer (Signature)",
            Statement = "Claim over de signature-printing.",
        });
        await db.SaveChangesAsync();

        var result = await Service(db).RepairSourceFormsAsync();

        Assert.Equal(1, result.MergedDuplicates);
        Assert.Null(await db.Cards.FindAsync("sfd-239*-221"));
        Assert.NotNull(await db.Cards.FindAsync("sfd-239-star-221"));

        Assert.Equal("sfd-239-star-221", (await db.BanEntries.SingleAsync()).CardRiftboundId);
        Assert.Equal("sfd-239-star-221", (await db.Errata.SingleAsync()).CardRiftboundId);
        Assert.Equal("sfd-239-star-221", (await db.Corrections.SingleAsync()).Ref);
        Assert.Equal("card:sfd-239-star-221", (await db.Relations.SingleAsync()).FromRef);
        var interaction = await db.CardInteractions.SingleAsync();
        Assert.Equal(("ogn-050-298", "sfd-239-star-221"),
            (interaction.CardAId, interaction.CardBId));
        var explanation = await db.SimilarityExplanations.SingleAsync();
        Assert.Equal(("ogn-050-298", "sfd-239-star-221"),
            (explanation.CardAId, explanation.CardBId));
        // Canonical-flip-les (#57): variant-verwijzingen hangen mee om.
        Assert.Equal("sfd-239-star-221", (await db.Cards.FindAsync("sfd-239-221"))!.VariantOf);
        // Claims verwijzen op naam: de verdwenen riftcodex-naam wordt de winnaar.
        Assert.Equal("Soraka, Wanderer", (await db.Claims.SingleAsync()).TopicRef);

        var log = Assert.Single(await db.RunLogs.ToListAsync());
        Assert.Equal(("cards", "bronvorm-reparatie", "ok"), (log.Kind, log.Ref, log.Status));
        Assert.Contains("1 dubbelen samengevoegd", log.Detail);
    }

    [Fact]
    public async Task Repair_CollidingInteractionPairFallsAwayInsteadOfViolatingUniqueness()
    {
        using var db = NewDb();
        db.Cards.AddRange(
            Card("sfd-239-star-221", "Soraka, Wanderer"),
            Card("sfd-239*-221", "Soraka - Wanderer (Signature)"),
            Card("ogn-050-298", "Jinx, Loose Cannon"));
        // Hetzelfde paar bestaat al onder de canonieke id én onder de dubbel;
        // en de dubbel heeft een interactie met de winnaar zelf (zelf-paar).
        db.CardInteractions.AddRange(
            Interaction("ogn-050-298", "sfd-239-star-221"),
            Interaction("ogn-050-298", "sfd-239*-221"),
            Interaction("sfd-239*-221", "sfd-239-star-221"));
        await db.SaveChangesAsync();

        await Service(db).RepairSourceFormsAsync();

        var remaining = Assert.Single(await db.CardInteractions.ToListAsync());
        Assert.Equal(("ogn-050-298", "sfd-239-star-221"),
            (remaining.CardAId, remaining.CardBId));
    }

    [Fact]
    public async Task Repair_RenamesStarIdWithoutCounterpartSoTheAdapterKeepsFindingIt()
    {
        using var db = NewDb();
        // Alleen de riftcodex-vorm bestaat: zonder hernoemen zou de
        // genormaliseerde adapter bij de volgende sync een nieuwe
        // "-star"-rij aanmaken — dan is de dubbel terug.
        db.Cards.AddRange(
            Card("sfd-233*-221", "Yone - Blademaster (Signature)", mechanics: ["Deflect"]),
            Card("sfd-233-221", "Yone, Blademaster"));
        await db.SaveChangesAsync();

        var result = await Service(db).RepairSourceFormsAsync();

        Assert.Equal(0, result.MergedDuplicates);
        Assert.Equal(1, result.NormalizedIds);
        Assert.Equal(1, result.NormalizedNames);
        Assert.Null(await db.Cards.FindAsync("sfd-233*-221"));
        var moved = await db.Cards.FindAsync("sfd-233-star-221");
        Assert.NotNull(moved);
        // Naambewijs uit de basisprinting: de streepjes-naam wordt de komma-vorm.
        Assert.Equal("Yone, Blademaster (Signature)", moved!.Name);
        // Alle velden verhuizen mee (het id is de sleutel — kopie + delete).
        Assert.Equal(["Deflect"], moved.Mechanics!);
    }

    [Fact]
    public async Task Repair_DashNameWithoutEvidenceStays()
    {
        using var db = NewDb();
        // "Dark Child - Starter" is een écht Riot-naampatroon (OGS-starters):
        // zonder komma-bewijs geen conversie — nooit gokken.
        db.Cards.Add(Card("ogs-017-024", "Dark Child - Starter"));
        await db.SaveChangesAsync();

        var result = await Service(db).RepairSourceFormsAsync();

        Assert.Equal(0, result.NormalizedNames);
        Assert.Equal("Dark Child - Starter", (await db.Cards.SingleAsync()).Name);
        Assert.Empty(await db.RunLogs.ToListAsync());
    }

    [Fact]
    public async Task Repair_NameFixInvalidatesTheEmbedding()
    {
        using var db = NewDb();
        var dash = Card("sfd-057-221", "Irelia - Fervent");
        dash.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        dash.EmbeddingModel = EmbeddingConfig.Model;
        db.Cards.AddRange(dash, Card("sfd-057a-221", "Irelia, Fervent (Alternate Art)"));
        await db.SaveChangesAsync();

        var result = await Service(db).RepairSourceFormsAsync();

        Assert.Equal(1, result.NormalizedNames);
        var repaired = await db.Cards.FindAsync("sfd-057-221");
        Assert.Equal("Irelia, Fervent", repaired!.Name);
        // De naam zit in de embeddingtekst: de embed-pijplijn moet de kaart
        // opnieuw oppakken.
        Assert.Null(repaired.Embedding);
        Assert.Null(repaired.EmbeddingModel);
    }

    [Fact]
    public async Task Repair_SecondRunChangesNothing()
    {
        using var db = NewDb();
        db.Cards.AddRange(
            Card("sfd-239-star-221", "Soraka, Wanderer"),
            Card("sfd-239*-221", "Soraka - Wanderer (Signature)"),
            Card("sfd-233*-221", "Yone - Blademaster (Signature)"),
            Card("sfd-233-221", "Yone, Blademaster"),
            Card("ogs-017-024", "Dark Child - Starter"));
        await db.SaveChangesAsync();

        var service = Service(db);
        var first = await service.RepairSourceFormsAsync();
        Assert.Equal((1, 1, 1),
            (first.MergedDuplicates, first.NormalizedIds, first.NormalizedNames));

        var second = await service.RepairSourceFormsAsync();
        Assert.Equal((0, 0, 0),
            (second.MergedDuplicates, second.NormalizedIds, second.NormalizedNames));
        // Grootboek: alleen de run die iets deed schrijft een regel.
        Assert.Single(await db.RunLogs.ToListAsync());
    }

    private static Card Card(
        string id, string name, string? variantOf = null, string[]? mechanics = null) => new()
    {
        RiftboundId = id, Name = name, VariantOf = variantOf, Mechanics = mechanics,
        SetId = id.Split('-')[0].ToUpperInvariant(),
    };

    private static CardInteraction Interaction(string a, string b)
    {
        var (idA, idB) = CardText.OrderedPair(a, b);
        return new() { CardAId = idA, CardBId = idB, Kind = "combo", Explanation = "uitleg" };
    }

    private static CardSyncService Service(RbRulesDbContext db) =>
        new(db, new HttpClient()); // de reparatie doet geen netwerk-I/O

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kent geen transacties; de reparatie draait er wel in
            // (Postgres) — voor de test volstaat negeren.
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
