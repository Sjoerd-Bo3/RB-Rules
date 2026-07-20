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

/// <summary>Orkestratie-tests voor de brein-interactie-mining (#226, §3.1/§3.4).
/// Mock rb-ai (gestubde HTTP-handler op de echte RbAiClient) levert
/// tool-forced-achtige kandidaten; de test bewaakt de bedrading: extractie →
/// entity-resolutie (fase 1) VÓÓR kandidaat-creatie → fase-2-promotie-poort →
/// atomair feit+provenance. Plus de degradatie (rb-ai null → geen half feit, job
/// rondt netjes af). De poort-tiers zelf staan al in ReifiedInteractionTests; hier
/// gaat het om de koppeling.</summary>
public class BreinInteractionMiningServiceTests
{
    // ── Promotie + provenance (lexicale steun aanwezig) ──────────────────────
    [Fact]
    public async Task RunAsync_LexicaleSteun_PromoveertMetProvenanceEnConditie()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage during a Showdown. Assault deals damage.",
            ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = new[] { new { on_kind = "WINDOW", window = "Showdown", subject_role = "patient" } },
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.FocusCards);
        Assert.Equal(1, r.Extracted);
        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Failed);

        var ix = await db.Interactions.Include(x => x.Conditions).SingleAsync();
        Assert.Equal("mechanic:Deflect", ix.AgentRef);
        Assert.Equal("mechanic:Assault", ix.PatientRef);
        Assert.Equal("COUNTERS", ix.Kind);
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
        var cond = Assert.Single(ix.Conditions);
        Assert.Equal(InteractionConditionKinds.Window, cond.OnKind);
        Assert.Equal("Showdown", cond.Value);

        // Provenance (0a): het feit draagt een Assertion die naar de run + de bronkaart
        // wijst — atomair met het feit geschreven (rode draad #236).
        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal(BrainRef.Interaction(ix.Id).Format(), assertion.Subject);
        Assert.Equal(FactKinds.Interaction, assertion.FactKind);
        Assert.Equal("card:ogn-001", assertion.DerivedFromRef);
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(run.Id, assertion.MiningRunId);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(FactKinds.Interaction, run.Kind);
    }

    // ── Entity-resolutie VÓÓR kandidaat-creatie: synoniem → canonieke ref ────
    [Fact]
    public async Task RunAsync_ResolvetKeywordSynoniem_NaarCanoniekeRef_VoorCreatie()
    {
        using var db = NewDb();
        // Canonieke keyword-entiteit met een alias; de kaart draagt de aliasvorm.
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = "Deflect",
            AltLabels = ["Deflecting"], Status = CanonicalEntityStatus.Canonical,
            CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();
        await SeedCardAsync(db, "ogn-010", "Gamma", "Unit",
            "Deflecting prevents Assault damage.", ["Deflecting", "Assault"]);

        // rb-ai noemt de canonieke ref (die de service aanbood na resolutie).
        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Extracted);
        var ix = await db.Interactions.SingleAsync();
        // De aliasvorm "Deflecting" is naar de canonieke "Deflect" geresolveerd VÓÓR
        // de ref ontstond — geen tweede knoop (versla #2).
        Assert.Equal("mechanic:Deflect", ix.AgentRef);
    }

    // ── Cold-start: emergente card×card zonder steun → hypothese, niet weg ───
    [Fact]
    public async Task RunAsync_CardCardZonderSteun_ParkeertAlsHypothese()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit", "Some effect about combat.", ["Fury"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit", "Another effect entirely.", ["Fury"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-001", to = "card:ogn-002", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        // Eén focus-kaart zodat er precies één extractie-call is (deterministisch).
        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Hypothesized);
        Assert.Equal(0, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.ModelHypothesizedUnruled, ix.Status);
    }

    // ── Keyword-paar zonder lexicale steun → kandidaat (wacht op corroboratie) ─
    [Fact]
    public async Task RunAsync_KeywordPaarZonderSteun_WordtKandidaat()
    {
        using var db = NewDb();
        // Tekst bevat de keyword-labels NIET letterlijk ⇒ geen lexicale steun.
        await SeedCardAsync(db, "ogn-020", "Delta", "Unit", "It has some ability.", ["Snipe", "Tank"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Snipe", to = "mechanic:Tank", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Candidates);
        Assert.Equal(0, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Candidate, ix.Status);
    }

    // ── Degradatie: rb-ai null → geen half feit, job rondt netjes af ─────────
    [Fact]
    public async Task RunAsync_RbAiUitval_GeenHalfFeit_JobRondtNetjesAf()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = Service(db, () => null); // 500 → RbAiClient geeft null

        var r = await svc.RunAsync(); // mag NIET gooien

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Extracted);
        Assert.Empty(await db.Interactions.ToListAsync());
        Assert.Empty(await db.Assertions.ToListAsync());
        // De run is aangemaakt maar leeg — een geldige, lege attempt, geen half feit.
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(0, run.Verified);
    }

    // ── Idempotent: herhaald draaien maakt geen duplicaten ───────────────────
    [Fact]
    public async Task RunAsync_TweeKeer_GeenDuplicaat()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage during a Showdown. Assault deals damage.",
            ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        await svc.RunAsync();
        await svc.RunAsync();

        Assert.Equal(1, await db.Interactions.CountAsync());
    }

    // ── Voortgangs-watermark: al-verwerkte focus-kaart wordt overgeslagen (#226) ─
    [Fact]
    public async Task RunAsync_SlaatAlGemineFocusKaartOver_SchuiftDoorDePool()
    {
        using var db = NewDb();
        // Twee kaarten met dezelfde mechanieken zodat de vaste stub op beide slaat.
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit", "It has some ability.", ["Snipe", "Tank"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit", "It has some ability.", ["Snipe", "Tank"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Snipe", to = "mechanic:Tank", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        // Run 1 (cap 1): verwerkt de eerste kaart, legt een interactie-feit +
        // Assertion(DERIVED_FROM=card:ogn-001) vast; CapHit ⇒ er is nog werk.
        var r1 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(1, r1.FocusCards);
        Assert.True(r1.CapHit);

        // Run 2 (cap 1): de eerste kaart is nu al-gemined ⇒ de selectie schuift op
        // naar de TWEEDE kaart (versla defect 1/2), niet dezelfde kop opnieuw.
        var r2 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(1, r2.FocusCards);

        var derived = await db.Assertions.Select(a => a.DerivedFromRef).ToListAsync();
        Assert.Contains("card:ogn-001", derived);
        Assert.Contains("card:ogn-002", derived); // run 2 bereikte de tweede kaart

        // Run 3 (cap 1): beide kaarten verwerkt ⇒ niets meer te doen, drain-signaal
        // gaat naar 'drained' (geen CapHit) i.p.v. eeuwig 'niet gedraind'.
        var r3 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(0, r3.FocusCards);
        Assert.False(r3.CapHit);
    }

    // ── Lexicale steun eist co-occurrence binnen ÉÉN kaart, niet cross-card (#226) ─
    [Fact]
    public async Task RunAsync_TermenInVerschillendeKaarten_GeenLexicaleSteun_Kandidaat()
    {
        using var db = NewDb();
        // Focus-tekst draagt WEL "Tank", NIET "Snipe"; de partner (deelt Snipe) draagt
        // "Snipe". Geen enkele kaart draagt beide termen ⇒ geen lexicale steun.
        await SeedCardAsync(db, "ogn-030", "Malphite", "Unit",
            "This grants Tank to an ally.", ["Tank", "Snipe"]);
        await SeedCardAsync(db, "ogn-031", "Caitlyn", "Unit",
            "Snipe deals damage.", ["Snipe"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Snipe", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // Vroeger telde de samengevoegde tekst van beide kaarten als co-occurrence en
        // promoveerde dit paar. Nu blijft het kandidaat (wacht op corroboratie).
        Assert.Equal(1, r.Candidates);
        Assert.Equal(0, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Candidate, ix.Status);
    }

    // ── #249: kaart↔EIGEN keyword levert géén promoveerbare Interaction meer ─────
    // Regressie op de kernbevinding: 264 van 383 live interacties (69%) waren
    // kaart↔eigen-keyword. Dat feit staat al deterministisch in de graph
    // (HAS_KEYWORD uit Card.Mechanics) en verdrong de gekwalificeerde interacties
    // waar de tabel voor bedoeld is. De lexicale poort beloonde het bovendien
    // triviaal: de kaart ís de ene rol en haar keyword staat in haar eigen tekst.
    [Fact]
    public async Task RunAsync_KaartMetEigenKeyword_LevertGeenInteractie()
    {
        using var db = NewDb();
        // Precies de live-vorm: "Cloth Armor REQUIRES Equip" terwijl Equip gewoon
        // in Card.Mechanics staat.
        await SeedCardAsync(db, "ogn-040", "Cloth Armor", "Gear",
            "[Equip] Attach to a unit. It gets +1 might.", ["Quick-Draw", "Equip"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-040", to = "mechanic:Equip", kind = "REQUIRES", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(0, r.Promoted);
        Assert.Equal(0, r.Candidates);
        Assert.Equal(0, r.Hypothesized);
        Assert.Equal(0, r.Extracted);       // geen kandidaat: herkauwde kennis
        Assert.Equal(1, r.SkippedKnown);    // wél zichtbaar geteld
        Assert.Empty(await db.Interactions.ToListAsync());
        Assert.Empty(await db.Assertions.ToListAsync());
        // Géén grafsteen: er is niets verworpen dat later gegrond kan blijken — de
        // kennis leeft gewoon deterministisch in de graph.
        Assert.Empty(await db.RejectionTombstones.ToListAsync());
    }

    // De deterministische kaart→keyword-projectie blijft ONGEMOEID: Card.Mechanics
    // is de bron waar GraphSyncService de HAS_KEYWORD-edges uit bouwt, en de mining
    // raakt dat bronveld niet aan (#249-acceptatie).
    [Fact]
    public async Task RunAsync_LaatCardMechanicsOngemoeid_GraphProjectieBlijftIntact()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-040", "Cloth Armor", "Gear",
            "[Equip] Attach to a unit.", ["Quick-Draw", "Equip"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-040", to = "mechanic:Equip", kind = "REQUIRES", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        await svc.RunAsync(maxFocusCards: 1);

        var card = await db.Cards.SingleAsync();
        Assert.Equal(["Quick-Draw", "Equip"], card.Mechanics!);
    }

    // ── #249: card↔ANDERMANS keyword blijft wél een geldige interactie ───────────
    // De poort mag niet doorslaan: een kaart die een keyword beïnvloedt dat zij
    // zelf NIET draagt, is precies wel een echte relatie — en die staat letterlijk
    // in haar tekst.
    [Fact]
    public async Task RunAsync_CardKeyword_NietHaarEigen_PromoveertOpKaarttekst()
    {
        using var db = NewDb();
        // De kaartnaam staat NIET in de tekst; het keyword "Deflect" wel — maar
        // Deflect is een keyword van de PARTNER, niet van de focus-kaart zelf.
        await SeedCardAsync(db, "ogn-040", "Vanguard Sentinel", "Unit",
            "This unit counters Deflect.", ["Tank"]);
        await SeedCardAsync(db, "ogn-041", "Shieldbearer", "Unit",
            "Deflect prevents damage.", ["Tank", "Deflect"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-040", to = "mechanic:Deflect", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.SkippedKnown);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("card:ogn-040", ix.AgentRef);
        Assert.Equal("mechanic:Deflect", ix.PatientRef);
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
    }

    // ── #249: buurt-keywords worden aangeboden ⇒ mech↔mech kan ontstaan ──────────
    // Vroeger boden we alleen de keywords van de FOCUS-kaart aan; een keyword van
    // een partner viel buiten de enum en het model kón het paar niet noemen (live:
    // mech↔mech was 5 van 383). Nu draagt de aanbieding de keywords van de hele
    // gedeelde-mechaniek-buurt.
    [Fact]
    public async Task RunAsync_BiedtBuurtKeywordsAan_MechMechParenZijnMogelijk()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-050", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank"]);
        // Assault is een keyword van de PARTNER (gedeelde mechaniek: Tank).
        await SeedCardAsync(db, "ogn-051", "Beta", "Unit", "Assault deals damage.", ["Tank", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // Beide labels staan in de tekst van de focus-kaart ⇒ relatie-bewijs.
        Assert.Equal(1, r.Promoted);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("mechanic:Tank", ix.AgentRef);
        Assert.Equal("mechanic:Assault", ix.PatientRef);
    }

    // ── #249: een regelsectie is bewijs voor een mech↔mech-relatie ───────────────
    // De officiële regeltekst is waar keyword↔keyword-relaties daadwerkelijk staan
    // opgeschreven; zonder die bewijsbron bleven zulke paren eeuwig 'kandidaat'.
    [Fact]
    public async Task RunAsync_RegelsectieAlsBewijs_PromoveertMechMech()
    {
        using var db = NewDb();
        // De kaarttekst noemt de labels NIET — alleen de regelsectie doet dat.
        await SeedCardAsync(db, "ogn-060", "Gamma", "Unit", "It has some ability.", ["Snipe", "Tank"]);
        db.RuleChunks.Add(new RuleChunk
        {
            SourceId = "core-rules-pdf", SectionCode = "704.2", ChunkIndex = 1,
            Text = "Tank reduces incoming damage before Snipe assigns its damage.",
        });
        await db.SaveChangesAsync();

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Snipe", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // Vroeger: geen bewijs in de kaarttekst ⇒ kandidaat. Nu draagt de officiële
        // regelsectie het bewijs en promoveert het paar.
        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
    }

    // ── #249: twee identiteits-ankers zijn geen relatie-bewijs ───────────────────
    [Fact]
    public async Task RunAsync_AlleenIdentiteitsAnkers_GeenLexicaleSteun()
    {
        using var db = NewDb();
        // Kaart A noemt kaart B niet bij naam; beide zijn alleen "zichzelf".
        await SeedCardAsync(db, "ogn-070", "Alpha", "Unit", "Some effect.", ["Fury"]);
        await SeedCardAsync(db, "ogn-071", "Beta", "Unit", "Another effect.", ["Fury"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-070", to = "card:ogn-071", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // Cold-start-tier, niet gepromoveerd: er is geen bewijszin die de relatie uitdrukt.
        Assert.Equal(0, r.Promoted);
        Assert.Equal(1, r.Hypothesized);
    }

    // ── Negatief verdict + deterministische steun → Rejected + tombstone, geen knoop ─
    [Fact]
    public async Task RunAsync_NegatiefVerdictMetSteun_Weigert_ZonderInteractieKnoop()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-050", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        // interacts=false + beide termen letterlijk in één kaart (deterministische
        // steun) ⇒ de poort weigert duurzaam (tombstone), maar legt GEEN interactie-
        // knoop aan — alleen een beslissings-memo.
        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = false,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Rejected);
        Assert.Equal(0, r.Promoted);
        Assert.Equal(0, r.Candidates);
        Assert.Equal(0, r.Failed);
        Assert.Empty(await db.Interactions.ToListAsync());     // geen gepromoveerde/kandidaat-knoop
        Assert.Empty(await db.Assertions.ToListAsync());        // geen feit-provenance bij een weigering
        var decision = await db.InteractionDecisions.SingleAsync();
        Assert.Equal(InteractionStatus.Rejected, decision.Outcome);
        var tomb = await db.RejectionTombstones.SingleAsync();  // herstelpad tegen flip-flop
        Assert.False(tomb.Lifted);
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal(1, run.Rejected);
    }

    // ── AI staat aan maar levert geen kandidaten (200 + lege lijst) ≠ uitval (#226) ─
    [Fact]
    public async Task RunAsync_AiLeegResultaat_GeenUitval_GeenFeit()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-060", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        // 200 met een lege interactie-lijst: een geldige, lege attempt — GEEN degradatie.
        var svc = Service(db, () => Interactions());

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.FocusCards);
        Assert.Equal(0, r.Extracted);
        Assert.Equal(0, r.Failed); // onderscheid 'geen kandidaten' ↔ 'rb-ai weg' blijft bewaard
        Assert.Empty(await db.Interactions.ToListAsync());
        var run = await db.MiningRuns.SingleAsync();
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(0, run.Verified);
    }

    // ── #251: de uitvals-OORZAAK staat in de samenvatting (run-detail/cockpit) ──
    [Fact]
    public async Task RunAsync_RbAiUitval_SplitstDeOorzaakUitInDeSamenvatting()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        // Aanhoudende rate-limit: vroeger telde dit als een kale "1 rb-ai-uitval"
        // en was niet te zien dat het om 429's ging.
        var svc = new BreinInteractionMiningService(
            db, RateLimitedAi(), new EntityResolutionService(db), new InteractionPromotionService(db));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Failed);
        Assert.Equal("429 rate-limit×1", r.FailureDetail);
        Assert.Contains("(429 rate-limit×1)", r.Summary);
    }

    [Fact]
    public async Task RunAsync_ZonderUitval_HeeftGeenOorzaakStaart()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions());

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(0, r.Failed);
        Assert.Null(r.FailureDetail);
        Assert.EndsWith("0 rb-ai-uitval", r.Summary);   // geen oorzaak-staart
    }

    // ── Nachtrun-deadline (#245) ───────────────────────────────────────────────
    [Fact]
    public async Task RunAsync_DeadlineVerstreken_StoptDirect_GeenHalfFeit_MeerWerk()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage. Assault deals damage.", ["Deflect", "Assault"]);
        // rb-ai zou promoveren, maar de deadline is al verstreken: de lus breekt vóór
        // de eerste rb-ai-aanroep — geen (half) feit, en er blijft vers werk liggen.
        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: NightlyWindow.UncappedItems,
            deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(0, r.FocusCards);   // niets verwerkt
        Assert.True(r.CapHit);           // deadline afgekapt ⇒ vers werk blijft liggen
        Assert.Empty(await db.Interactions.ToListAsync());
    }

    // ── testinfra ─────────────────────────────────────────────────────────────

    private static BreinInteractionMiningService Service(RbRulesDbContext db, Func<string?> body) =>
        new(db, Ai(body), new EntityResolutionService(db), new InteractionPromotionService(db));

    private static string Interactions(params object[] items) =>
        JsonSerializer.Serialize(new { interactions = items });

    private static async Task SeedCardAsync(
        RbRulesDbContext db, string id, string name, string type, string text, string[] mechanics)
    {
        db.Cards.Add(new Card
        {
            RiftboundId = id, Name = name, Type = type, TextPlain = text, Mechanics = mechanics,
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

    /// <summary>rb-ai dat structureel 429 geeft (#251), met een no-op backoff zodat
    /// de test niet echt wacht.</summary>
    private static RbAiClient RateLimitedAi() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance)
    {
        RetryDelay = (_, _) => Task.CompletedTask,
    };

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
