using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De werkverdeling deterministisch ↔ LLM in de mining-run (#211).
/// Kern: gebrackete mechanieken zijn gratis en moeten rb-ai-uitval overleven,
/// de LLM mag er alleen ongebrackete kandidaten bij leggen, en een kaart met
/// alleen het deterministische deel telt nog niet als klaar. rb-ai is de échte
/// client op een gestubde handler; de database is EF InMemory.</summary>
public class MechanicMiningServiceTests
{
    // Echte kaarttekst (riftcodex text.plain): keyword zónder blokhaken.
    private const string LaurentBladekeeper =
        "Ganking (I can move from battlefield to battlefield.)";

    [Fact]
    public async Task RunAsync_RbAiUitval_SchrijftDeGebracketeMechaniekenToch()
    {
        // Kern van #211: de keywords staan letterlijk in de kaarttekst, dus een
        // platliggende rb-ai (verwacht pad, ~45% uitval) mag niet betekenen dat
        // de kaart géén mechanieken heeft — dat kostte de graaf zijn
        // HAS_KEYWORD-edges voor niets.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Assault 2] Deal 4. [Deflect]"));
        await db.SaveChangesAsync();
        var svc = new MechanicMiningService(db, Ai(() => null));

        var r = await svc.RunAsync();

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Assault", "Deflect"], card.Mechanics!);
        // ... maar de kaart is níét klaar: de LLM-velden ontbreken nog, dus hij
        // blijft in de wachtrij i.p.v. als half feit voor "gemined" door te gaan.
        Assert.Null(card.Triggers);
        Assert.Equal(0, r.Mined);
        Assert.Equal(1, r.Failed);
        Assert.Equal(1, r.Remaining);
        Assert.Equal(0, r.LlmAdded);
    }

    [Fact]
    public async Task RunAsync_MagnitudeBlijftDeFamilie_OpDeKaart()
    {
        // "Assault 2" en "Assault 3" delen de canonieke entiteit "Assault"
        // (CanonicalEntity.CanonicalLabel) — de magnitude mag nooit als aparte
        // mechaniek op de kaart belanden.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "Give a unit [Assault 2]. Then [Assault 3]."));
        await db.SaveChangesAsync();

        await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Assault"], card.Mechanics!);
    }

    [Fact]
    public async Task RunAsync_LlmMagAlleenAangebodenOngebracketeTermenToevoegen()
    {
        // Laurent Bladekeeper draagt "Ganking" zónder haken — dát is het gat
        // dat de regex niet dicht. De LLM stelt hem terecht voor (telt mee) en
        // stelt daarnaast "Tank" voor, dat nergens in deze kaarttekst staat en
        // dus niet werd aangeboden: die valt bij de validatie weg.
        using var db = NewDb();
        db.MechanicKeywords.Add(new MechanicKeyword { Term = "Ganking", Status = "accepted" });
        db.Cards.Add(Kaart("ogn-001-298", "[Deathknell] " + LaurentBladekeeper));
        await db.SaveChangesAsync();
        var answer = """
            [{"id": "ogn-001-298", "extraMechanics": ["Ganking", "Tank"],
              "triggers": ["when I move"], "effects": ["move a unit"]}]
            """;

        var r = await new MechanicMiningService(db, Ai(() => answer)).RunAsync();

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Deathknell", "Ganking"], card.Mechanics!);
        Assert.Equal(["when I move"], card.Triggers!);
        Assert.Equal(1, r.Mined);
        Assert.Equal(1, r.LlmAdded); // alleen Ganking; Tank is weggevallen
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task RunAsync_LlmKanEenGebracketeMechaniekNietWegnemen()
    {
        // Een leeg LLM-oordeel is geen reden om het deterministische feit te
        // vergeten — de LLM mag hier alleen bij, nooit af.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Tank] [Shield]"));
        await db.SaveChangesAsync();
        var answer =
            """[{"id": "ogn-001-298", "extraMechanics": [], "triggers": [], "effects": []}]""";

        var r = await new MechanicMiningService(db, Ai(() => answer)).RunAsync();

        Assert.Equal(["Tank", "Shield"], (await db.Cards.SingleAsync()).Mechanics!);
        Assert.Equal(0, r.LlmAdded);
    }

    [Fact]
    public async Task RunAsync_VerworpenKeywordKomtNietOpDeKaart()
    {
        // De beheerder zei "geen mechaniek"; die beslissing geldt ook voor de
        // deterministische route.
        using var db = NewDb();
        db.MechanicKeywords.Add(new MechanicKeyword { Term = "Level", Status = "rejected" });
        db.Cards.Add(Kaart("ogn-001-298", "[Level 6] [Tank]"));
        await db.SaveChangesAsync();

        await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(["Tank"], (await db.Cards.SingleAsync()).Mechanics!);
    }

    [Fact]
    public async Task RunAsync_NaUitval_MaaktEenVolgendeRunDeKaartAf()
    {
        // Het deterministische deel uit run 1 blijft staan; run 2 vult alleen
        // de LLM-velden aan en haalt de kaart uit de wachtrij.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Tank] When I conquer, draw 1."));
        await db.SaveChangesAsync();
        string? answer = null;
        var svc = new MechanicMiningService(db, Ai(() => answer));

        var first = await svc.RunAsync();
        Assert.Equal(1, first.Remaining);

        answer = """
            [{"id": "ogn-001-298", "extraMechanics": [], "triggers": ["when I conquer"],
              "effects": ["draw a card"]}]
            """;
        var second = await svc.RunAsync();

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Tank"], card.Mechanics!);
        Assert.Equal(["when I conquer"], card.Triggers!);
        Assert.Equal(1, second.Mined);
        Assert.Equal(0, second.Remaining);
    }

    [Fact]
    public async Task RunAsync_HerhaaldIdInHetAntwoord_LaatDeJobNietCrashen()
    {
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Tank]"));
        await db.SaveChangesAsync();
        var answer = """
            [{"id": "ogn-001-298", "extraMechanics": [], "triggers": ["a"], "effects": []},
             {"id": "ogn-001-298", "extraMechanics": [], "triggers": ["b"], "effects": []}]
            """;

        var r = await new MechanicMiningService(db, Ai(() => answer)).RunAsync();

        Assert.Equal(1, r.Mined);
        Assert.Equal(["a"], (await db.Cards.SingleAsync()).Triggers!);
    }

    // ── Hersynchronisatie van eerder gemínede kaarten (#211) ────────────

    [Fact]
    public async Task RunAsync_HerstelsSplitsteMagnitudeOpEenAlGemineedKaart()
    {
        // Regressie: kaarten van vóór #211 zijn "klaar" (Triggers gevuld) en
        // komen dus nooit meer in de wachtrij — hun mechanieken bleven het
        // vrije-vorm LLM-resultaat, inclusief de magnitude-splitsing die de
        // canonieke entiteit uit elkaar trekt.
        using var db = NewDb();
        var card = Kaart("ogn-001-298", "Give a unit [Assault 2].");
        card.Mechanics = ["Assault 2"];
        card.Triggers = [];
        card.Effects = [];
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var r = await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(1, r.Reconciled);
        Assert.Equal(["Assault"], (await db.Cards.SingleAsync()).Mechanics!);
        Assert.Equal(0, r.Remaining); // de kaart blijft klaar
    }

    [Fact]
    public async Task RunAsync_HersynchronisatieBehoudtEenEerderLlmOordeel()
    {
        // Niet-destructief: "Ganking" staat ongebracket in de tekst en is
        // geaccepteerd vocabulaire — dus precies wat het LLM-oordeel oplevert.
        // Dat blijft staan; het verzonnen "Warmog" valt weg.
        using var db = NewDb();
        db.MechanicKeywords.Add(new MechanicKeyword { Term = "Ganking", Status = "accepted" });
        var card = Kaart("ogn-001-298", "[Tank] " + LaurentBladekeeper);
        card.Mechanics = ["Tank", "Ganking", "Warmog"];
        card.Triggers = [];
        card.Effects = [];
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var r = await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(["Tank", "Ganking"], (await db.Cards.SingleAsync()).Mechanics!);
        Assert.Equal(1, r.Reconciled);
    }

    [Fact]
    public async Task RunAsync_HersynchronisatieIsIdempotent()
    {
        using var db = NewDb();
        var card = Kaart("ogn-001-298", "[Tank] [Assault 2]");
        card.Mechanics = ["Tank", "Assault"];
        card.Triggers = [];
        card.Effects = [];
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var r = await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(0, r.Reconciled); // niets te doen — geen schrijfactie
        Assert.Equal(["Tank", "Assault"], (await db.Cards.SingleAsync()).Mechanics!);
    }

    [Fact]
    public async Task RunAsync_OogstOnbekendeGebracketeTermenAlsKandidaat()
    {
        // Ongewijzigd (#52): een nieuw keyword komt niet via de LLM binnen maar
        // als kandidaat in de reviewqueue, met zijn kaart-telling.
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Ganking] Deal 4."));
        db.Cards.Add(Kaart("ogn-002-298", "[Ganking] Deal 2."));
        await db.SaveChangesAsync();

        var r = await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(1, r.NewCandidates);
        var candidate = await db.MechanicKeywords.SingleAsync();
        Assert.Equal("Ganking", candidate.Term);
        Assert.Equal("candidate", candidate.Status);
        Assert.Equal(2, candidate.Occurrences);
    }

    [Fact]
    public async Task RunAsync_SlaatVariantenEnTekstlozeKaartenOver()
    {
        using var db = NewDb();
        db.Cards.Add(Kaart("ogn-001-298", "[Tank]", variantOf: "ogn-002-298"));
        db.Cards.Add(Kaart("ogn-003-298", ""));
        await db.SaveChangesAsync();

        var r = await new MechanicMiningService(db, Ai(() => null)).RunAsync();

        Assert.Equal(0, r.Remaining);
        Assert.Equal(0, r.Failed);
        Assert.All(await db.Cards.ToListAsync(), c => Assert.Null(c.Mechanics));
    }

    // --- testinfra ---------------------------------------------------------

    private static Card Kaart(string id, string text, string? variantOf = null) => new()
    {
        RiftboundId = id, Name = $"Kaart {id}", TextPlain = text,
        Tags = [], VariantOf = variantOf,
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Echte RbAiClient op een gestubde handler: null ⇒ 500 (uitval),
    /// anders het gegeven antwoord als {"answer": ...} — zelfde patroon als
    /// JobCatalogDrainTests/ClaimMiningServiceTests.</summary>
    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { answer = a }) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet (patroon JobLedgerTests).</summary>
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
