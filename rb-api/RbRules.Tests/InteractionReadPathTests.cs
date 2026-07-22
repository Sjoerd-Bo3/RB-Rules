using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Het LEESPAD van de kaart-interacties (#258). Het kaartdetail
/// (/api/cards/{id}/interactions) las tot nu toe uitsluitend de OUDE
/// <see cref="CardInteraction"/>-tabel; de gereïficeerde
/// <see cref="Interaction"/>-laag (#226) werd alleen door de brein-verkenner en
/// de graph-projectie gelezen. Deze tests bewaken de migratiebrug die beide
/// samenvoegt.
///
/// De belangrijkste is <see cref="Leespad_VerlietstGeenEnkeleLegacyBuur"/>: die
/// faalt zodra iemand de legacy-tak weghaalt terwijl de gereïficeerde laag nog
/// niet genoeg dekking heeft. Dat is precies de fout die met groene tests zou
/// passeren — gemeten op productie zou hard omschakelen het kaartdetail van 94
/// kaarten naar 0 zichtbare interacties brengen (103 legacy-rijen versus 8
/// gereïficeerde, waarvan nul gepromoveerde kaart↔kaart-paren).</summary>
public class InteractionReadPathTests
{
    [Fact]
    public async Task Leespad_VerlietstGeenEnkeleLegacyBuur()
    {
        // REGRESSIETEST voor de blackout: alleen legacy-data, geen enkele
        // gereïficeerde rij — exact de productiesituatie van vandaag.
        using var db = NewDb();
        db.Cards.AddRange(Card("ogn-001-298", "Viktor"), Card("ogn-002-298", "Jinx"));
        db.CardInteractions.Add(Legacy("ogn-001-298", "ogn-002-298", "synergy", "werken samen"));
        await db.SaveChangesAsync();

        var neighbors = await Service(db).NeighborsAsync("ogn-001-298", take: 40);

        var neighbor = Assert.Single(neighbors);
        Assert.Equal("ogn-002-298", neighbor.OtherId);
        Assert.Equal("Jinx", neighbor.OtherName);
        Assert.Equal("synergy", neighbor.Kind);
        Assert.Equal("werken samen", neighbor.Explanation);
    }

    [Fact]
    public async Task Leespad_ToontOokDeGereificeerdeInteracties()
    {
        // De eigenlijke migratie: een gepromoveerde kaart↔kaart-interactie uit
        // de nieuwe laag hoort in het kaartdetail te verschijnen, zonder dat er
        // ook maar één legacy-rij aan te pas komt.
        using var db = NewDb();
        db.Cards.AddRange(Card("ogn-001-298", "Viktor"), Card("ogn-003-298", "Ekko"));
        db.Interactions.Add(Reified(
            "card:ogn-001-298", "card:ogn-003-298", InteractionKinds.Counters,
            InteractionStatus.Promoted));
        await db.SaveChangesAsync();

        var neighbors = await Service(db).NeighborsAsync("ogn-001-298", take: 40);

        var neighbor = Assert.Single(neighbors);
        Assert.Equal("ogn-003-298", neighbor.OtherId);
        Assert.Equal("Ekko", neighbor.OtherName);
        // Kleingeschreven, zodat het chip-label naast de oude kinds
        // (combo/synergy/counter/nonbo) leesbaar blijft.
        Assert.Equal("counters", neighbor.Kind);
        // De ROL blijft zichtbaar — "A countert B" is iets anders dan andersom,
        // en dat onderscheid had de oude, richtingsloze tabel niet.
        Assert.Contains("Deze kaart counters Ekko", neighbor.Explanation);
    }

    [Fact]
    public async Task Leespad_VoegtBeideLagenSamen_ZonderDeBuurTweeKeerTeTonen()
    {
        using var db = NewDb();
        db.Cards.AddRange(
            Card("ogn-001-298", "Viktor"), Card("ogn-002-298", "Jinx"), Card("ogn-003-298", "Ekko"));
        // Dezelfde buur in BEIDE lagen (Jinx) + één die alleen legacy is (Ekko).
        db.CardInteractions.AddRange(
            Legacy("ogn-001-298", "ogn-002-298", "synergy", "oude uitleg"),
            Legacy("ogn-001-298", "ogn-003-298", "nonbo", "botsen"));
        db.Interactions.Add(Reified(
            "card:ogn-001-298", "card:ogn-002-298", InteractionKinds.Modifies,
            InteractionStatus.Promoted));
        await db.SaveChangesAsync();

        var neighbors = await Service(db).NeighborsAsync("ogn-001-298", take: 40);

        Assert.Equal(2, neighbors.Count);
        // De gereïficeerde rij wint van de legacy-rij voor dezelfde buur: hij
        // draagt rol en condities, de oude alleen vrije proza.
        var jinx = Assert.Single(neighbors, n => n.OtherId == "ogn-002-298");
        Assert.Equal("modifies", jinx.Kind);
        // En de buur die alleen de oude laag kent verdwijnt niet.
        var ekko = Assert.Single(neighbors, n => n.OtherId == "ogn-003-298");
        Assert.Equal("nonbo", ekko.Kind);
    }

    [Fact]
    public async Task Leespad_ToontGeenOngetoetsteModelhypothesen()
    {
        // De promotiepoort (#226) bestaat om precies dit tegen te houden:
        // 'candidate' en 'model_hypothesized_unruled' zijn nog niet getoetst en
        // mogen niet als "geverifieerde interactie" bij de bezoeker landen.
        using var db = NewDb();
        db.Cards.AddRange(
            Card("ogn-001-298", "Viktor"), Card("ogn-002-298", "Jinx"), Card("ogn-003-298", "Ekko"));
        db.Interactions.AddRange(
            Reified("card:ogn-001-298", "card:ogn-002-298", InteractionKinds.Counters,
                InteractionStatus.Candidate),
            Reified("card:ogn-001-298", "card:ogn-003-298", InteractionKinds.Counters,
                InteractionStatus.ModelHypothesizedUnruled));
        await db.SaveChangesAsync();

        Assert.Empty(await Service(db).NeighborsAsync("ogn-001-298", take: 40));
    }

    [Fact]
    public async Task Leespad_SlaatKaartKeywordInteractiesOver()
    {
        // Een card↔keyword-interactie ("Deflect MODIFIES deze kaart") is geen
        // buur maar een eigenschap: het kaartdetail linkt buren door naar
        // /cards/{id}, en "mechanic:Deflect" is geen kaartpagina.
        using var db = NewDb();
        db.Cards.Add(Card("ogn-001-298", "Viktor"));
        db.Interactions.Add(Reified(
            "mechanic:Deflect", "card:ogn-001-298", InteractionKinds.Modifies,
            InteractionStatus.Promoted));
        await db.SaveChangesAsync();

        Assert.Empty(await Service(db).NeighborsAsync("ogn-001-298", take: 40));
    }

    [Fact]
    public async Task Leespad_VerlietstGeenKaartBuurAchterEenBergKaartKeywordRijen()
    {
        // REGRESSIETEST (#287-review): het "is dit kaart↔kaart?"-filter moet in de
        // QUERY staan, niet stroomafwaarts in de projectie. Staat het erbuiten, dan
        // eten card↔mechanic-rijen het Take-budget op en verdwijnt elke echte
        // kaart-buur voorbij de afkap STIL.
        //
        // Dat is geen theoretisch geval: BreinInteractionMiningService biedt bewust
        // de keywords van partnerkaarten aan, dus card↔mechanic is een BEDOELDE
        // uitvoervorm (zie ook Leespad_SlaatKaartKeywordInteractiesOver hieronder).
        // Vandaag staan er 8 rijen op productie en gebeurt er niets — de bug slaat
        // pas aan zodra de mining opschaalt, precies wanneer de brug zich moet
        // terugbetalen. Dezelfde stille blackout die deze PR wil voorkomen.
        //
        // take: 40 is de waarde die CardEndpoints gebruikt; 12 die van
        // GraphQueryService.
        // De ruis-rijen krijgen bewust een kind dat VÓÓR dat van de echte buur
        // sorteert (COUNTERS < REQUIRES op de ThenBy(Kind) van de query), zodat de
        // kaart-buur écht achter de afkap valt. Met een gunstiger sortering zou de
        // test toevallig slagen zonder dat het filter er staat — dan bewijst hij niets.
        using var db = NewDb();
        db.Cards.AddRange(Card("ogn-001-298", "Viktor"), Card("ogn-002-298", "Jinx"));
        for (var i = 0; i < 40; i++)
            db.Interactions.Add(Reified(
                "card:ogn-001-298", $"mechanic:Keyword{i:00}", InteractionKinds.Counters,
                InteractionStatus.Promoted));
        db.Interactions.Add(Reified(
            "card:ogn-001-298", "card:ogn-002-298", InteractionKinds.Requires,
            InteractionStatus.Promoted));
        await db.SaveChangesAsync();

        foreach (var take in new[] { 40, 12 })
        {
            var neighbors = await Service(db).NeighborsAsync("ogn-001-298", take);

            var neighbor = Assert.Single(neighbors);
            Assert.Equal("ogn-002-298", neighbor.OtherId);
        }
    }

    [Fact]
    public async Task Leespad_IsVariantgroepBewust_InBeideLagen()
    {
        // #57: een alt-art-pagina toont de interacties van zijn canonieke
        // kaart, en buren worden naar hun canonieke id gecanonicaliseerd. Dat
        // gold al voor de oude laag en moet ook voor de nieuwe gelden.
        using var db = NewDb();
        db.Cards.AddRange(
            Card("ogn-001-298", "Viktor"),
            Card("ogn-001a-298", "Viktor (Alternate Art)", variantOf: "ogn-001-298"),
            Card("ogn-002-298", "Jinx"),
            Card("ogn-002a-298", "Jinx (Alternate Art)", variantOf: "ogn-002-298"));
        // De interactie hangt aan de VARIANT-ids van beide kanten.
        db.Interactions.Add(Reified(
            "card:ogn-001a-298", "card:ogn-002a-298", InteractionKinds.Grants,
            InteractionStatus.Promoted));
        await db.SaveChangesAsync();

        var neighbors = await Service(db).NeighborsAsync("ogn-001-298", take: 40);

        var neighbor = Assert.Single(neighbors);
        Assert.Equal("ogn-002-298", neighbor.OtherId); // gecanonicaliseerd
        Assert.Equal("Jinx", neighbor.OtherName);      // basisnaam
    }

    [Fact]
    public async Task Leespad_ToontDeCondities_DieDeOudeTabelNietKon()
    {
        // De hele reden voor de reïficatie: condities leven als losse,
        // individueel weerlegbare knopen in plaats van platgeslagen in proza.
        // Als het leespad ze laat vallen, is de migratie inhoudelijk zinloos.
        using var db = NewDb();
        db.Cards.AddRange(Card("ogn-001-298", "Viktor"), Card("ogn-002-298", "Jinx"));
        var interaction = Reified(
            "card:ogn-001-298", "card:ogn-002-298", InteractionKinds.Counters,
            InteractionStatus.Promoted);
        interaction.Conditions =
        [
            new InteractionCondition
            {
                InteractionId = 0, OnKind = InteractionConditionKinds.Window,
                Value = "Showdown",
            },
            new InteractionCondition
            {
                InteractionId = 0, OnKind = InteractionConditionKinds.Status,
                Value = "Exhausted", SubjectRole = InteractionRoles.Patient,
            },
        ];
        interaction.GovernedByRef = "section:core-rules-pdf/7.4";
        db.Interactions.Add(interaction);
        await db.SaveChangesAsync();

        var neighbor = Assert.Single(await Service(db).NeighborsAsync("ogn-001-298", take: 40));

        Assert.Contains("WINDOW Showdown", neighbor.Explanation);
        Assert.Contains("STATUS Exhausted (patient)", neighbor.Explanation);
        Assert.Contains("section:core-rules-pdf/7.4", neighbor.Explanation);
    }

    [Fact]
    public void Beschrijving_DraaitDeRolOmAlsDeKaartDeOndergaandeKantIs()
    {
        // Pure eenheid: dezelfde rij levert een andere zin op vanaf de andere
        // kaart. Zonder dit onderscheid leest "A countert B" op B's pagina als
        // "B countert A" — de fout die de oude, richtingsloze tabel maakte.
        var row = Reified("card:a", "card:b", InteractionKinds.Counters, InteractionStatus.Promoted);

        Assert.Equal("Deze kaart counters Jinx.",
            ReifiedInteractionDisplay.Describe(row, "Jinx", thisIsAgent: true));
        Assert.Equal("Viktor counters deze kaart.",
            ReifiedInteractionDisplay.Describe(row, "Viktor", thisIsAgent: false));
    }

    [Fact]
    public void Beschrijving_VerifiedIsDeSterksteTier_NietNogNietGepromoveerd()
    {
        // #332-orde: verified > promoted. De oude UI-tekst ("Geverifieerd, nog
        // niet gepromoveerd.") beweerde het omgekeerde — alsof verified nog op
        // de promotiepoort wachtte.
        var verified = Reified(
            "card:a", "card:b", InteractionKinds.Counters, InteractionStatus.Verified);
        var text = ReifiedInteractionDisplay.Describe(verified, "Jinx", thisIsAgent: true);
        Assert.Contains("Geverifieerd.", text);
        Assert.DoesNotContain("nog niet gepromoveerd", text);

        // Promoted is de norm en blijft stil.
        var promoted = Reified(
            "card:a", "card:b", InteractionKinds.Counters, InteractionStatus.Promoted);
        Assert.DoesNotContain("Geverifieerd",
            ReifiedInteractionDisplay.Describe(promoted, "Jinx", thisIsAgent: true));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static InteractionService Service(RbRulesDbContext db) =>
        // NeighborsAsync raakt alleen de database — rb-ai, Ollama en Neo4j
        // horen bij MineAsync/ResolveAsync en blijven hier buiten beeld.
        new(db, null!, null!, null!);

    private static Card Card(string id, string name, string? variantOf = null) => new()
    {
        RiftboundId = id, Name = name, VariantOf = variantOf,
        SetId = id.Split('-')[0].ToUpperInvariant(),
    };

    private static CardInteraction Legacy(string a, string b, string kind, string explanation)
    {
        var (idA, idB) = CardText.OrderedPair(a, b);
        return new() { CardAId = idA, CardBId = idB, Kind = kind, Explanation = explanation };
    }

    private static Interaction Reified(
        string agentRef, string patientRef, string kind, string status) => new()
    {
        AgentRef = agentRef, PatientRef = patientRef, Kind = kind, Status = status,
        CreatedByRunId = "run-test",
    };

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
