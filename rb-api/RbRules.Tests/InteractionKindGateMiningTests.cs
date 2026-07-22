using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De regressie-fixtures van #330, end-to-end door de mining- en
/// promotielaag: de ZEVEN betwiste paren uit de opus-audit op de eerste
/// fable-batch (2026-07-22) stranden op de soort-poorten en landen als Candidate
/// (reviewqueue, juiste poort-reden, geteld in het run-detail); de TWEE
/// bevestigde REQUIRES-paren promoveren ongewijzigd op hun echte sectieteksten.
///
/// De bewijsteksten zijn de échte regelsectie-/definitieteksten (Core Rules) —
/// alleen de Predict-definitie is gereconstrueerd rond het letterlijke
/// audit-citaat "You may recycle it" (de opgeslagen definitie was niet in de
/// audit-dump meegeleverd; benoemd in de PR).
///
/// Fixture-echtheid (#286d) zit in de asserts zelf: de status_reason draagt de
/// poort-token, en de poorten vuren alléén wanneer er deterministische steun is —
/// een fixture zonder echte bewijszin zou "wacht op corroboratie" tonen en rood
/// gaan. De mutatie "poort uit → promoveert weer" is daarnaast handmatig
/// gedraaid en staat in de PR-body.</summary>
public class InteractionKindGateMiningTests
{
    // ── De zeven betwiste paren (allemaal → Candidate) ────────────────────────

    [Fact]
    public async Task Betwist1_LevelModifiesLegion_StrandtOpKindAnker()
    {
        // §727.1.b.3 noemt Level en Legion als voorbeelden van dezelfde
        // keyword-categorie — co-occurrence, geen MODIFIES-anker.
        using var db = NewDb();
        await SeedEntityAsync(db, "Level", "Level is a Dependent Keyword.");
        await SeedEntityAsync(db, "Legion", "Legion is a Dependent Keyword.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "727.1.b",
            "If an ability has multiple Dependent Keywords, all of them must have their " +
            "Condition met in order for the ability to be active. Example: A unit reads " +
            "“[Level 11][>>][Legion][>] When you conquer, gain 1 point.” In order for " +
            "the conquer effect of the unit to be active, its controller must have 11 XP " +
            "and have finalized a card other than the unit that turn.");

        var r = await Run(db, "mechanic:Level", "mechanic:Legion", "MODIFIES");

        await AssertDegraded(db, r, "kind_anchor");
        Assert.Equal(1, r.KindAnchorDegraded);
        Assert.Equal(0, r.WordFormDegraded);
        Assert.Contains("kind_anchor×1", r.Summary);   // run-detail telt, nooit stil (ADR-20)
    }

    [Fact]
    public async Task Betwist2_AddCountersReaction_StrandtOpKindAnker()
    {
        // §164.2.a citeert de gedrukte vorm: Add wordt gebrúikt binnen
        // Reaction-getimede abilities — geen counter-anker in de tekst.
        using var db = NewDb();
        await SeedEntityAsync(db, "Add");
        await SeedEntityAsync(db, "Reaction", "Reaction is a Permissive keyword.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "164.2.a", "[Reaction] — Add [1].");

        var r = await Run(db, "mechanic:Add", "mechanic:Reaction", "COUNTERS");

        await AssertDegraded(db, r, "kind_anchor");
        Assert.Equal(1, r.KindAnchorDegraded);
    }

    [Fact]
    public async Task Betwist3_VisionGrantsRecycle_StrandtOpWoordvorm()
    {
        // §817.2.a: "may choose to recycle or not recycle" — recycle is het
        // werkwoord (de optionele actie binnen Vision), geen toegekend [Recycle].
        using var db = NewDb();
        await SeedEntityAsync(db, "Vision", "Vision is a Triggered Ability keyword.");
        await SeedEntityAsync(db, "Recycle");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "817.2.a",
            "The player may choose to recycle or not recycle for each instance of " +
            "Vision separately.");

        var r = await Run(db, "mechanic:Vision", "mechanic:Recycle", "GRANTS");

        await AssertDegraded(db, r, "word_form");
        Assert.Equal(1, r.WordFormDegraded);
        Assert.Contains("word_form×1", r.Summary);
    }

    [Fact]
    public async Task Betwist4_AccelerateGrantsReady_StrandtOpWoordvorm()
    {
        // §805.6/§805.6.a: "entering ready … become ready" is een
        // vervangingseffect-toestand; §805.6.a zegt zelfs expliciet dat
        // Accelerate NIET interacteert met ready-abilities.
        using var db = NewDb();
        await SeedEntityAsync(db, "Accelerate", "Accelerate is a Unit ability.");
        await SeedEntityAsync(db, "Ready");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "805.6",
            "Accelerate generates a delayed replacement effect that replaces a unit " +
            "entering the board exhausted with it entering ready. It does not enter " +
            "exhausted and then become ready.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "805.6.a",
            "Accelerate will not interact with, or trigger, abilities that are affected " +
            "by units becoming ready.", chunkIndex: 2);

        var r = await Run(db, "mechanic:Accelerate", "mechanic:Ready", "GRANTS");

        await AssertDegraded(db, r, "word_form");
    }

    [Fact]
    public async Task Betwist5_HuntGrantsXp_StrandtOpKindAnker()
    {
        // §823.1.c.1: een SPELER "gains X XP" (resource) — "gains" is bewust géén
        // GRANTS-anker (dit ís de gemeten overclaim). NB: XP staat als
        // hoofdletter-term midden in de zin, dus de wóórdvormpoort passeert —
        // precies waarom dit paar het kind-anker nodig heeft.
        using var db = NewDb();
        await SeedEntityAsync(db, "Hunt", "Hunt is present on Units.");
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "823.1.c",
            "Hunt is formatted as “Hunt X”");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "823.1.c.1",
            "Hunt is functionally short for: “When I Conquer or Hold, my controller " +
            "gains X XP.” See rule 728. XP for more information", chunkIndex: 2);

        var r = await Run(db, "mechanic:Hunt", "mechanic:XP", "GRANTS");

        await AssertDegraded(db, r, "kind_anchor");
        Assert.Equal(1, r.KindAnchorDegraded);
        Assert.Equal(0, r.WordFormDegraded);
    }

    [Fact]
    public async Task Betwist6_TankModifiesBackline_StrandtOpKindAnker_GovernedByBlijft()
    {
        // §465.2.c: Tank en Backline in één toewijzings-voorbeeld — louter
        // co-occurrence. Het governed_by-anker blijft op de Candidate-rij staan:
        // degraderen is geen informatie weggooien.
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank", "Tank is a Passive Ability keyword.");
        await SeedEntityAsync(db, "Backline", "Backline is a Passive Ability keyword.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "465.2.c",
            "A player must obey all requirements and restrictions on damage assignment " +
            "if able. Example: A player is assigning damage to the following units: a " +
            "unit with Tank (“I must be assigned combat damage first.”); a unit with " +
            "Backline (“I must be assigned combat damage last.”); and another unit " +
            "without any abilities. That player must assign combat damage first to the " +
            "unit with Tank, then to the unit with no abilities, then to the unit with " +
            "Backline.");

        var r = await Run(db, "mechanic:Tank", "mechanic:Backline", "MODIFIES",
            governedBy: "section:core-rules-pdf/465.2.c");

        await AssertDegraded(db, r, "kind_anchor");
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("section:core-rules-pdf/465.2.c", ix.GovernedByRef);
    }

    [Fact]
    public async Task Betwist7_PredictGrantsRecycle_StrandtOpWoordvorm()
    {
        // "You may recycle it" (audit-citaat) — het werkwoord binnen Predicts
        // resolutie, geen toegekend keyword. Definitie gereconstrueerd rond het
        // citaat; zie de klasse-samenvatting.
        using var db = NewDb();
        await SeedEntityAsync(db, "Predict",
            "Predict is functionally short for: “Look at the top card of your deck. " +
            "You may recycle it.”");
        await SeedEntityAsync(db, "Recycle");

        var r = await Run(db, "mechanic:Predict", "mechanic:Recycle", "GRANTS");

        await AssertDegraded(db, r, "word_form");
    }

    // ── De twee bevestigde paren (blijven promoveren) ─────────────────────────

    [Fact]
    public async Task Bevestigd1_LevelRequiresXp_PromoveertOpEchteSectietekst()
    {
        // §727.1.b.2/.3: "As long as its controller has 3 XP" / "must have 11 XP"
        // — de REQUIRES-ankers staan letterlijk in de echte teksten.
        using var db = NewDb();
        await SeedEntityAsync(db, "Level", "Level is a Dependent Keyword.");
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "727.1.b.2",
            "The Dependent Ability is Active exactly as written while the Condition is " +
            "true Example: Gustwalker has “[Level 3][>] I have +1 [M] and Ganking.” " +
            "As long as its controller has 3 XP, Gustwalker’s Ganking is active.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "727.1.b.3",
            "If an ability has multiple Dependent Keywords, all of them must have their " +
            "Condition met in order for the ability to be active. In order for the " +
            "conquer effect of the unit to be active, its controller must have 11 XP.",
            chunkIndex: 2);

        var r = await Run(db, "mechanic:Level", "mechanic:XP", "REQUIRES");

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
        Assert.Equal(0, r.KindAnchorDegraded);
        Assert.Equal(0, r.WordFormDegraded);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
        Assert.NotNull(ix.PromotedAt);
    }

    [Fact]
    public async Task Bevestigd2_WeaponmasterRequiresEquip_PromoveertOpEchteSectietekst()
    {
        // §821.1.b: "pay its Equip cost at a discount" — pay/cost dragen REQUIRES.
        using var db = NewDb();
        await SeedEntityAsync(db, "Weaponmaster", "Weaponmaster is present on Units.");
        await SeedEntityAsync(db, "Equip", "Equip is formatted as “Equip [Cost]”");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "821.1.b",
            "Weaponmaster is a Play Effect that chooses an Equipment you control and " +
            "allows you to pay its Equip cost at a discount.");

        var r = await Run(db, "mechanic:Weaponmaster", "mechanic:Equip", "REQUIRES");

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
    }

    // ── Poort B geïsoleerd: anker aanwezig, alleen de woordvorm strandt ───────
    // SYNTHETISCH (benoemd): de zeven echte betwiste teksten dragen geen enkel
    // GRANTS-anker, dus daar vangt poort A ze óók — dit paar isoleert poort B
    // voor de mutatie "poort B uit → promoveert weer".

    [Fact]
    public async Task Synthetisch_AnkerAanwezigMaarWerkwoordvorm_StrandtAlleenOpPoortB()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Vision", "Vision is a Triggered Ability keyword.");
        await SeedEntityAsync(db, "Recycle");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "817.9",
            "Vision grants you the option to recycle the top card of your deck.");

        var r = await Run(db, "mechanic:Vision", "mechanic:Recycle", "GRANTS");

        await AssertDegraded(db, r, "word_form");
        Assert.Equal(0, r.KindAnchorDegraded);   // poort A was gehaald ("grants")
        Assert.Equal(1, r.WordFormDegraded);
    }

    // ── Positieve controle: gebracket keyword-doel promoveert ────────────────
    // SYNTHETISCH (benoemd): geen van de echte definitie-fixtures draagt een
    // gebracket keyword-DOEL, dus de andere richting van poort B heeft een
    // geconstrueerde bewijszin nodig — in de gemeten drukconventie (#211).

    [Fact]
    public async Task Synthetisch_GebracketKeywordDoel_PasseertBeidePoorten()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Vision", "Vision is a Triggered Ability keyword.");
        await SeedEntityAsync(db, "Recycle");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "817.9",
            "Vision grants [Recycle] to its controller when it triggers.");

        var r = await Run(db, "mechanic:Vision", "mechanic:Recycle", "GRANTS");

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
        Assert.Equal(0, r.KindAnchorDegraded);
        Assert.Equal(0, r.WordFormDegraded);
        Assert.DoesNotContain("poorten:", r.Summary);   // niets te melden = geen ruis (#302)
    }

    // ── testinfra (spiegel van BreinInteractionMiningServiceTests, minimaal) ──

    /// <summary>Draait de mechanic-pass met één subject en een stub-rb-ai die
    /// precies dit paar voorstelt (interacts=true, geen condities).</summary>
    private static async Task<BreinInteractionMiningResult> Run(
        RbRulesDbContext db, string from, string to, string kind, string? governedBy = null)
    {
        var payload = governedBy is null
            ? (object)new { from, to, kind, interacts = true, conditions = Array.Empty<object>() }
            : new { from, to, kind, interacts = true, governed_by = governedBy, conditions = Array.Empty<object>() };
        var svc = new BreinInteractionMiningService(
            db, Ai(() => JsonSerializer.Serialize(new { interactions = new[] { payload } })),
            new EntityResolutionService(db), new InteractionPromotionService(db));
        return await svc.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);
    }

    /// <summary>De gedeelde uitkomst-vorm van de zeven betwiste paren: één
    /// Candidate (nooit stil weg), nul promoties, de poort-token in de
    /// status_reason. Dat de reden een poort-token draagt bewijst meteen de
    /// fixture-echtheid (#286d): de poorten vuren alleen ná deterministische
    /// steun, dus zonder poort was dit een promotie geweest.</summary>
    private static async Task AssertDegraded(
        RbRulesDbContext db, BreinInteractionMiningResult r, string port)
    {
        Assert.Equal(0, r.Promoted);
        Assert.Equal(1, r.Candidates);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Candidate, ix.Status);
        Assert.Contains(port, ix.StatusReason);
        Assert.Null(ix.PromotedAt);
        Assert.False(await db.RejectionTombstones.AnyAsync());   // soft-pad (#324-patroon)
    }

    private static async Task SeedEntityAsync(
        RbRulesDbContext db, string label, string? definition = null)
    {
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = label,
            Definition = definition,
            Status = CanonicalEntityStatus.Canonical, CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRuleSectionAsync(
        RbRulesDbContext db, string sourceId, string sectionCode, string text, int chunkIndex = 1)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == sourceId))
            db.Sources.Add(new Source
            {
                Id = sourceId, Name = sourceId, Url = $"https://playriftbound.com/{sourceId}",
                Type = "official", TrustTier = 1, Parser = "pdf", Cadence = "daily",
            });
        db.RuleChunks.Add(new RuleChunk
        {
            SourceId = sourceId, SectionCode = sectionCode, ChunkIndex = chunkIndex, Text = text,
        });
        await db.SaveChangesAsync();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static RbAiClient Ai(Func<string?> body) => new(
        new HttpClient(new StubHandler(_ => body() is { } b
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(b, Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
