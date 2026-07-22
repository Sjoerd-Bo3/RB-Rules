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
    // (HAS_MECHANIC uit Card.Mechanics) en verdrong de gekwalificeerde interacties
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
    // is de bron waar GraphSyncService de HAS_MECHANIC-edges uit bouwt, en de mining
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
        await SeedRuleSectionAsync(db, "core-rules-pdf", "704.2",
            "Tank reduces incoming damage before Snipe assigns its damage.");

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
        // Geen oorzaak-staart: direct na "0 rb-ai-uitval" volgt de meting (#286), niet
        // een "(…)"-uitsplitsing van uitvalsoorzaken.
        Assert.Contains("0 rb-ai-uitval;", r.Summary);
        Assert.DoesNotContain("0 rb-ai-uitval (", r.Summary);
    }

    // ── #281: rb-ai's REDEN staat in de samenvatting, niet alleen de laag ──────
    // Dit is de keten waar de issue om vroeg: rb-ai classificeert de uitval, zet hem
    // in de foutbody, RbAiClient leest hem, de tally telt hem op en het run-detail
    // toont hem. Vóór #281 stopte alles bij "5xx×22" — de oorzaak stond nergens,
    // ook niet in de containerlog.
    [Fact]
    public async Task RunAsync_RbAiUitvalMetReden_ToontDeRedenInDeSamenvatting()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = new BreinInteractionMiningService(
            db, FailingAi("""{"error":"extractie mislukt","reason":"no_tool_call"}"""),
            new EntityResolutionService(db), new InteractionPromotionService(db));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Failed);
        Assert.Equal("5xx×1 (no_tool_call×1)", r.FailureDetail);
        Assert.Contains("(5xx×1 (no_tool_call×1))", r.Summary);
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

    // ── #249-review: het watermark hangt aan de EXTRACTIE, niet aan een feit ────
    // De blokkerende regressie van #249: de tautologie-poort slaat kaart↔eigen-keyword
    // over vóór de promotie, dus zo'n kaart schreef geen Assertion — en juist die
    // Assertion wás het voortgangs-watermark. Met OrderBy(RiftboundId).Take(cap) bleef
    // die kaart aan de kop van de wachtrij staan: de gecapte job herkauwde eeuwig
    // dezelfde kop, de nachtrun betaalde elke nacht opnieuw, en Drained bleef false.
    [Fact]
    public async Task RunAsync_KaartMetAlleenEigenKeyword_BlokkeertDeWachtrijNietBijDeTweedeRun()
    {
        using var db = NewDb();
        // ogn-040 levert UITSLUITEND kaart↔eigen-keyword op ⇒ 0 feiten, 0 Assertions.
        await SeedCardAsync(db, "ogn-040", "Cloth Armor", "Gear",
            "[Equip] Attach to a unit.", ["Equip"]);
        await SeedCardAsync(db, "ogn-041", "Second Card", "Gear",
            "[Equip] Attach to a unit.", ["Equip"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-040", to = "mechanic:Equip", kind = "REQUIRES", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r1 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(1, r1.SkippedKnown);
        Assert.Empty(await db.Assertions.ToListAsync());   // geen feit ⇒ geen Assertion

        // Het watermark staat er wél: de extractie sláágde, er viel alleen niets te
        // promoveren. Dat onderscheid kon het Assertion-proxy principieel niet maken.
        var first = await db.Cards.SingleAsync(c => c.RiftboundId == "ogn-040");
        Assert.NotNull(first.InteractionsMinedAt);
        Assert.Equal(await db.MiningRuns.Select(m => m.Id).FirstAsync(),
            first.InteractionsMinedByRunId);

        // Run 2 (cap 1) schuift dus DOOR naar de tweede kaart in plaats van dezelfde
        // kop opnieuw aan rb-ai aan te bieden.
        var stamp = first.InteractionsMinedAt;
        var r2 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(1, r2.FocusCards);
        Assert.Equal(stamp, (await db.Cards.SingleAsync(c => c.RiftboundId == "ogn-040"))
            .InteractionsMinedAt);   // niet opnieuw aangeboden
        Assert.NotNull((await db.Cards.SingleAsync(c => c.RiftboundId == "ogn-041"))
            .InteractionsMinedAt);

        // Run 3: de pool is leeg ⇒ gedraind (geen CapHit), i.p.v. eeuwig 'nog werk over'.
        var r3 = await svc.RunAsync(maxFocusCards: 1);
        Assert.Equal(0, r3.FocusCards);
        Assert.False(r3.CapHit);
    }

    // ── #249-review: rb-ai-uitval zet GEEN watermark — die kaart komt terug ──────
    [Fact]
    public async Task RunAsync_RbAiUitval_ZetGeenWatermark_KaartKomtDeVolgendeRunTerug()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var down = Service(db, () => null); // 500 → RbAiClient geeft null
        var r1 = await down.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r1.Failed);
        Assert.Null((await db.Cards.SingleAsync()).InteractionsMinedAt);

        // Zelfde kaart, rb-ai weer in de lucht: de kaart wordt opnieuw aangeboden en
        // levert nu wél een feit. Een watermark op de uitval had haar permanent
        // overgeslagen — dat is precies waarom het aan de GESLAAGDE extractie hangt.
        var up = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));
        var r2 = await up.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r2.FocusCards);
        Assert.Equal(1, r2.Promoted);
        Assert.NotNull((await db.Cards.SingleAsync()).InteractionsMinedAt);
    }

    // ── #249-review: ook een volledig VERWORPEN kaart verlaat de wachtrij ────────
    [Fact]
    public async Task RunAsync_AlleenVerworpenKandidaten_ZetTochHetWatermark()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-050", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = false,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Rejected);
        Assert.Empty(await db.Assertions.ToListAsync());   // weigering ⇒ geen Assertion
        Assert.NotNull((await db.Cards.SingleAsync()).InteractionsMinedAt);
    }

    // ── #251-review: een kapotte envelop is UITVAL, geen leeg resultaat ──────────
    // HTTP 200 met een afgekapte body of schema-drift werd door de Domain-parser stil
    // tot [] gereduceerd en als 'geslaagd, leeg' geteld: een run waarin rb-ai bij élke
    // kaart onzin teruggaf meldde 0% uitval. Dat ondermijnde de hele #251-meting.
    [Theory]
    [InlineData("{\"interactions\":[{\"from\":\"card:ogn-001\"")]   // afgekapt
    [InlineData("{\"interactions\":\"none\"}")]                      // schema-drift
    public async Task RunAsync_KapotteEnvelop_TeltAlsOnleesbaar_EnZetGeenWatermark(string body)
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);

        var svc = Service(db, () => body);

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Failed);
        Assert.Equal("onleesbaar antwoord×1", r.FailureDetail);
        // Géén watermark: een kaart met een kapot antwoord moet juist terugkomen.
        Assert.Null((await db.Cards.SingleAsync()).InteractionsMinedAt);
    }

    // ── #249-review: woordgrens — generiek proza is geen bewijszin ───────────────
    // TextContains was een kale substring-match. Sinds #249 is het HELE regelcorpus
    // bewijsbron én ankertoets, en Riftbound-keywords zijn deels gewone Engelse
    // woorden — "Deflection"/"assaulting" leverden zo een officieel-ogende bewijszin
    // voor mechanic:Deflect ↔ mechanic:Assault die de termen niet eens noemt.
    [Fact]
    public async Task RunAsync_TermAlleenAlsWoorddeelInRegeltekst_GeeftGeenLexicaleSteun()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-080", "Alpha", "Unit", "It has some ability.",
            ["Deflect", "Assault"]);
        await SeedRuleSectionAsync(db, "core-rules-pdf", "101.1",
            "Deflection and assaulting are informal terms that players sometimes use.");

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Deflect", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(0, r.Promoted);
        Assert.Equal(1, r.Candidates);   // wacht op corroboratie, geen valse promotie
    }

    // ── #249-review: gebracket + meerwoords blijft wél een treffer ───────────────
    [Fact]
    public async Task RunAsync_GebracktKeywordInRegeltekst_BlijftBewijs()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-081", "Alpha", "Unit", "It has some ability.",
            ["Assault 2", "Tank"]);
        await SeedRuleSectionAsync(db, "core-rules-pdf", "704.3",
            "[Assault 2] resolves before Tank reduces the incoming damage.");

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Assault", to = "mechanic:Tank", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Promoted);
    }

    // ── #249-review: community-regeltekst is geen deterministische steun ─────────
    [Fact]
    public async Task RunAsync_CommunityRegelsectie_TeltNietAlsBewijs()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-082", "Alpha", "Unit", "It has some ability.",
            ["Snipe", "Tank"]);
        await SeedRuleSectionAsync(db, "beginners-guide-riftboundgg", null,
            "Tank basically cancels out Snipe, that is the rule of thumb.", trustTier: 3);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Snipe", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // De kennislagen zijn bindend: officieel > community. Zonder officieel anker
        // blijft dit kandidaat i.p.v. gepromoveerd op een forumparafrase.
        Assert.Equal(0, r.Promoted);
        Assert.Equal(1, r.Candidates);
    }

    // ── #249-review: de bewijs-begroting wordt niet door één paar opgesoupeerd ───
    // De eerste drie secties die ≥2 labels noemen vulden MaxRuleSections, ook als het
    // steeds hetzelfde paar was — de sectie die een ánder paar documenteert werd dan
    // nooit geladen en dat paar bleef eeuwig kandidaat. De uitslag hing zo aan de
    // corpusvolgorde (SourceId/ChunkIndex) in plaats van aan het bewijs.
    [Fact]
    public async Task RunAsync_BewijsBegroting_BereiktOokHetLaterGedocumenteerdePaar()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-090", "Alpha", "Unit", "It has some ability.",
            ["Snipe", "Tank", "Vision", "Barrier"]);
        // Drie vroege secties over hetzelfde paar (Snipe+Tank) …
        for (var i = 1; i <= 3; i++)
            await SeedRuleSectionAsync(db, "core-rules-pdf", $"70{i}.1",
                "Tank reduces the damage that Snipe assigns.", chunkIndex: i);
        // … en pas dáárna de sectie die Vision↔Barrier beschrijft.
        await SeedRuleSectionAsync(db, "core-rules-pdf", "704.1",
            "Barrier hides a unit from Vision until the end of the turn.", chunkIndex: 4);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Barrier", to = "mechanic:Vision", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
    }

    // ══ #286: de vraag herijkt — minder vocabulaire, meer vragen, mechanic-niveau ══

    /// <summary>DE regressietest van #286. Op productie stuurde één kaart-aanroep 39
    /// refs mee en liep daarmee tegen de 90s-timeout van rb-ai; met 3 refs lukte
    /// dezelfde kaart in 49s. Het aangeboden vocabulaire IS dus de kostenpost, en het
    /// groeit met elke set — een schaalklip, geen vaste faalkans.
    ///
    /// De fixture bootst die 39-ref-situatie ECHT na (#286-review, blokkade 1): 39
    /// canonieke keyword-entiteiten, waarvan er 35 in de focus-tekst voorkomen en dus
    /// allemaal scoorbaar zijn. Zonder dat vocabulaire kón de oude versie van deze test
    /// de begroting onder geen enkele instelling overschrijden — hij was groen bij
    /// `MaxNeighbourKeywords: 999`, en bewees dus niets.
    ///
    /// De drempel is bewust een LETTERLIJKE 12 en niet
    /// <c>OfferingLimits.Card.MaxRefs</c>: een assertie tegen de constante die ze
    /// bewaakt schuift mee als je die constante verhoogt.</summary>
    [Fact]
    public async Task RunAsync_GroteBuurt_BiedtNooitHetHeleVocabulaireAan()
    {
        using var db = NewDb();

        // 39 canonieke keywords — precies de productie-orde van grootte.
        await SeedEntityAsync(db, "Tank");
        var vocab = Enumerable.Range(1, 35).Select(i => $"Vocabkw{i}").ToList();
        foreach (var label in vocab) await SeedEntityAsync(db, label);
        foreach (var label in new[] { "Losskw1", "Losskw2", "Losskw3" })
            await SeedEntityAsync(db, label);

        // De focus-tekst noemt ze allemaal: zonder begroting zijn dat ~39 refs.
        await SeedCardAsync(db, "ogn-000", "Focus", "Unit",
            "Tank reduces damage from " + string.Join(", ", vocab) + ".", ["Tank"]);
        for (var i = 1; i <= 12; i++)
            await SeedCardAsync(db, $"ogn-{i:000}", $"Partner {i}", "Unit",
                $"Tank interacts with Vocabkw{i}.", ["Tank", $"Vocabkw{i}"]);

        var bodies = new List<string>();
        var svc = CapturingService(db, bodies, () => Interactions());

        await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 0);

        var refs = OfferedRefs(Assert.Single(bodies));
        Assert.True(refs.Count <= 12,
            $"kaart-aanroep bood {refs.Count} refs aan; de begroting van #286 is 12");

        // En de begroting mag geen dekking kosten waar het om gaat: het GEDRUKTE
        // keyword van de kaart zelf en de kaart-rollen blijven staan.
        Assert.Contains("mechanic:Tank", refs);
        Assert.Contains("card:ogn-000", refs);
        Assert.True(refs.Count(r => r.StartsWith("card:")) >= 2, "geen partner-rol over");
    }

    /// <summary>Dekking mag niet dalen (#286): de begrensde aanbieding houdt de
    /// buur-keywords die er inhoudelijk toe doen, en gooit alleen de rest weg. Hier
    /// staat het relevante keyword in de tekst van de focus-kaart zélf, tussen 20
    /// irrelevante keywords in de buurt.</summary>
    [Fact]
    public async Task RunAsync_GroteBuurt_HoudtHetRelevanteBuurKeyword()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-000", "Focus", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank"]);
        await SeedCardAsync(db, "ogn-001", "Bearer", "Unit",
            "Assault deals damage.", ["Tank", "Assault"]);
        for (var i = 2; i <= 11; i++)
            await SeedCardAsync(db, $"ogn-{i:000}", $"Noise {i}", "Unit",
                $"Tank does something with Noisekw{i}.", ["Tank", $"Noisekw{i}"]);

        var bodies = new List<string>();
        var svc = CapturingService(db, bodies, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        var r = await svc.RunAsync(maxFocusCards: 1);

        // Assault co-occurreert met Tank in de focus-tekst; de ruis-keywords doen dat
        // alleen in hun eigen kaart. Het paar overleeft de begroting en promoveert.
        Assert.Contains("mechanic:Assault", OfferedRefs(Assert.Single(bodies)));
        Assert.Equal(1, r.Promoted);
    }

    /// <summary>De mechanic-niveau-vraag (#286): 38 mechanics tegenover 1311 kaarten,
    /// dus mech↔mech hoort één keer per mechaniek gevraagd te worden — niet opnieuw bij
    /// elke kaart. Het feit wordt uit de MECHANIEK afgeleid (DERIVED_FROM =
    /// mechanic:…), niet uit een toevallige kaart.</summary>
    [Fact]
    public async Task RunAsync_MechanicNiveau_LevertMechMechUitEenSubject()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank");
        await SeedEntityAsync(db, "Assault");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank", "Assault"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        // maxFocusCards: 0 isoleert de mechanic-pass — anders zou de kaart-pass
        // hetzelfde paar (idempotent) nog eens aandragen. Eén subject, zodat het bij
        // precies één aanroep en dus één provenance-Assertion blijft.
        var r = await svc.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);

        Assert.Equal(1, r.MechanicSubjects);
        Assert.Equal(0, r.FocusCards);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("mechanic:Tank", ix.AgentRef);
        Assert.Equal("mechanic:Assault", ix.PatientRef);

        var assertion = await db.Assertions.SingleAsync();
        Assert.Equal("mechanic:Tank", assertion.DerivedFromRef);

        // Watermark: het verwerkte subject komt niet terug (anders herkauwt de gecapte
        // job eeuwig dezelfde kop van de wachtrij — het gat dat #249 op kaartniveau
        // dichtte), het onaangeroerde subject juist wél.
        var entities = await db.CanonicalEntities.OrderBy(e => e.Id).ToListAsync();
        Assert.NotNull(entities[0].InteractionsMinedAt);
        Assert.Null(entities[1].InteractionsMinedAt);
    }

    /// <summary>#286-review, blokkade 2 — de tegenhanger van
    /// <see cref="RunAsync_RbAiUitval_ZetGeenWatermark_KaartKomtDeVolgendeRunTerug"/>
    /// voor mechanic-subjecten. Zonder deze test blijft de hele suite groen als je
    /// <c>MarkEntityMinedAsync</c> onvoorwaardelijk maakt, en dat is letterlijk de
    /// #249-bug: een subject krijgt bij rb-ai-uitval een watermark, verdwijnt uit de
    /// wachtrij en komt nooit meer terug. Met >50% rb-ai-uitval op productie is dat het
    /// zwaarste pad dat er is.</summary>
    [Fact]
    public async Task RunAsync_RbAiUitval_ZetGeenMechanicWatermark_SubjectKomtTerug()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank");
        await SeedEntityAsync(db, "Assault");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank", "Assault"]);

        var down = Service(db, () => null); // 500 → RbAiClient geeft null
        var r1 = await down.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);

        Assert.Equal(1, r1.Failed);
        Assert.All(await db.CanonicalEntities.ToListAsync(),
            e => Assert.Null(e.InteractionsMinedAt));

        // Zelfde subject, rb-ai weer in de lucht: het wordt opnieuw aangeboden en levert
        // nu wél een feit. Een watermark op de uitval had het permanent overgeslagen.
        var up = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Assault", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));
        var r2 = await up.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);

        Assert.Equal(1, r2.MechanicSubjects);
        var entities = await db.CanonicalEntities.OrderBy(e => e.Id).ToListAsync();
        Assert.NotNull(entities[0].InteractionsMinedAt);
        Assert.Single(await db.Interactions.ToListAsync());
    }

    /// <summary>Een kapotte envelop op mechanic-niveau is UITVAL, geen leeg resultaat —
    /// dus ook géén watermark (#251-review, nu ook voor de mechanic-pass).</summary>
    [Fact]
    public async Task RunAsync_KapotteEnvelop_ZetGeenMechanicWatermark()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank");
        await SeedEntityAsync(db, "Assault");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank", "Assault"]);

        var svc = Service(db, () => "{\"interactions\":\"none\"}");

        var r = await svc.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);

        Assert.Equal(1, r.Failed);
        Assert.All(await db.CanonicalEntities.ToListAsync(),
            e => Assert.Null(e.InteractionsMinedAt));
    }

    /// <summary>Op mechanic-niveau zijn ALLEEN keywords een rol; kaarten en
    /// regelsecties zijn bewijs. Zonder die scheiding zou de pass terugvallen op de
    /// kaart↔eigen-keyword-tautologie die #249 uitroeide.</summary>
    [Fact]
    public async Task RunAsync_MechanicNiveau_BiedtAlleenKeywordRollenAan()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank");
        await SeedEntityAsync(db, "Assault");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank", "Assault"]);

        var bodies = new List<string>();
        var svc = CapturingService(db, bodies, () => Interactions());

        await svc.RunAsync(maxFocusCards: 0, maxMechanicSubjects: 1);

        var refs = OfferedRefs(Assert.Single(bodies));
        Assert.All(refs, r => Assert.StartsWith("mechanic:", r));
        Assert.True(refs.Count <= OfferingLimits.Mechanic.MaxRefs);
    }

    /// <summary>Dekking, expliciet (#286): wat de mechanic-pass per constructie NIET
    /// kan vinden, moet de kaart-pass blijven vinden. Een kaart↔kaart-paar heeft geen
    /// enkele keyword-rol en bestaat dus alleen op kaartniveau.</summary>
    [Fact]
    public async Task RunAsync_MechanicPassDektGeenKaartKaart_KaartPassBlijftDatDoen()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Fury");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit", "Some effect.", ["Fury"]);
        await SeedCardAsync(db, "ogn-002", "Beta", "Unit", "Another effect.", ["Fury"]);

        var svc = Service(db, () => Interactions(new
        {
            from = "card:ogn-001", to = "card:ogn-002", kind = "COUNTERS", interacts = true,
            conditions = Array.Empty<object>(),
        }));

        // De mechanic-pass kan dit paar niet aanbieden (geen keyword-rollen), de
        // kaart-pass wel — als cold-start-hypothese, precies zoals vóór #286.
        var r = await svc.RunAsync(maxFocusCards: 1);

        Assert.Equal(1, r.Hypothesized);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("card:ogn-001", ix.AgentRef);
        Assert.Equal("card:ogn-002", ix.PatientRef);
    }

    /// <summary>Rijkere vraag in dezelfde aanroep (#286): welke aangeboden regelsectie
    /// verankert de interactie? Dat vult <c>Interaction.GovernedByRef</c> (GOVERNED_BY),
    /// dat sinds #226 bestond maar altijd null bleef — en het kost niets, want die
    /// sectie stond toch al als bewijs in de prompt.</summary>
    [Fact]
    public async Task RunAsync_GovernedBy_VultDeNormatieveAnkerRef()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-060", "Gamma", "Unit", "It has some ability.", ["Snipe", "Tank"]);
        await SeedRuleSectionAsync(db, "core-rules-pdf", "704.2",
            "Tank reduces incoming damage before Snipe assigns its damage.");

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Snipe", kind = "COUNTERS", interacts = true,
            governed_by = "section:core-rules-pdf/704.2",
            conditions = Array.Empty<object>(),
        }));

        await svc.RunAsync(maxFocusCards: 1);

        var ix = await db.Interactions.SingleAsync();
        Assert.Equal("section:core-rules-pdf/704.2", ix.GovernedByRef);
    }

    /// <summary>...en nooit een anker buiten het aangeboden lijstje (CLAUDE.md, de
    /// gesloten LLM-vraag). Een verzonnen sectie-ref valt weg zoals een verzonnen
    /// rol-ref dat doet — het feit blijft, alleen zonder normatief anker.</summary>
    [Fact]
    public async Task RunAsync_GovernedBy_BuitenDeAanbieding_ValtWeg()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-060", "Gamma", "Unit", "It has some ability.", ["Snipe", "Tank"]);
        await SeedRuleSectionAsync(db, "core-rules-pdf", "704.2",
            "Tank reduces incoming damage before Snipe assigns its damage.");

        var svc = Service(db, () => Interactions(new
        {
            from = "mechanic:Tank", to = "mechanic:Snipe", kind = "COUNTERS", interacts = true,
            governed_by = "section:verzonnen-bron/9.9",
            conditions = Array.Empty<object>(),
        }));

        await svc.RunAsync(maxFocusCards: 1);

        var ix = await db.Interactions.SingleAsync();
        Assert.Null(ix.GovernedByRef);
    }

    /// <summary>Meten, niet gokken (acceptatiecriterium #286): het run-detail draagt
    /// het aantal aanroepen, de gemiddelde duur en het gemiddelde aantal aangeboden
    /// refs — per fase. Zonder die drie is "de vraag is nu goedkoper" een gok.</summary>
    [Fact]
    public async Task RunAsync_RunDetail_DraagtDeMeting()
    {
        using var db = NewDb();
        await SeedEntityAsync(db, "Tank");
        await SeedEntityAsync(db, "Assault");
        await SeedCardAsync(db, "ogn-001", "Alpha", "Unit",
            "Tank reduces the damage that Assault would deal.", ["Tank"]);

        var svc = Service(db, () => Interactions());

        var r = await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 1);

        Assert.NotNull(r.CallMetrics);
        Assert.Contains("mechanic 1×", r.CallMetrics);
        Assert.Contains("kaart 1×", r.CallMetrics);
        Assert.Contains("refs", r.CallMetrics);
        Assert.Contains("meting:", r.Summary);
    }

    [Fact]
    public async Task RunAsync_BeheerdeAliasReistMee_EnProviderUsageLandtOpRun()
    {
        using var db = NewDb();
        await SeedCardAsync(db, "ogn-325", "Provider Test", "Unit",
            "Deflect prevents Assault damage.", ["Deflect", "Assault"]);
        var bodies = new List<string>();
        var managed = new ManagedSettingsService(seed: new Dictionary<string, string>
        {
            [SettingKeys.BreinExtractModel] = BreinExtractModelAliases.Codex,
        });
        var svc = new BreinInteractionMiningService(
            db,
            CapturingAi(bodies, () =>
                """{"interactions":[],"provider":"codex-sdk","model":"gpt-5.3-codex","usage":{"inputTokens":80,"outputTokens":12,"unit":"tokens"}}"""),
            new EntityResolutionService(db), new InteractionPromotionService(db),
            managedSettings: managed);

        await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 0);

        using var payload = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal("codex", payload.RootElement.GetProperty("model").GetString());
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal("codex", run.LlmModelAlias);
        Assert.Equal("codex-sdk", run.LlmProvider);
        Assert.Equal("gpt-5.3-codex", run.LlmModel);
        Assert.Equal(1, run.LlmCalls);
        Assert.Equal(80, run.InputTokens);
        Assert.Equal(12, run.OutputTokens);
    }

    // ── testinfra ─────────────────────────────────────────────────────────────

    private static BreinInteractionMiningService Service(RbRulesDbContext db, Func<string?> body) =>
        new(db, Ai(body), new EntityResolutionService(db), new InteractionPromotionService(db));

    /// <summary>Als <see cref="Service"/>, maar legt elke rb-ai-payload vast zodat de
    /// test kan zien WAT er is aangeboden — de enige manier om de begroting van #286 op
    /// de echte productiecode te toetsen in plaats van op een tweede implementatie.</summary>
    private static BreinInteractionMiningService CapturingService(
        RbRulesDbContext db, List<string> bodies, Func<string?> body) =>
        new(db, CapturingAi(bodies, body), new EntityResolutionService(db),
            new InteractionPromotionService(db));

    /// <summary>De <c>refs</c>-lijst uit een vastgelegde payload.</summary>
    private static List<string> OfferedRefs(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return [.. doc.RootElement.GetProperty("refs").EnumerateArray()
            .Select(r => r.GetProperty("ref").GetString()!)];
    }

    /// <summary>Een levende canonieke keyword-entiteit — het subject van de
    /// mechanic-niveau-pass (#286).</summary>
    private static async Task SeedEntityAsync(RbRulesDbContext db, string label)
    {
        db.CanonicalEntities.Add(new CanonicalEntity
        {
            Kind = CanonicalEntityKinds.Keyword, CanonicalLabel = label,
            Status = CanonicalEntityStatus.Canonical, CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();
    }

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

    /// <summary>Een regelsectie mét haar bron-rij: de bewijsselectie filtert sinds de
    /// #249-review op trust-tier-1, dus een chunk zonder (officiële) bron telt niet mee.</summary>
    private static async Task SeedRuleSectionAsync(
        RbRulesDbContext db, string sourceId, string? sectionCode, string text,
        short trustTier = 1, int chunkIndex = 1)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == sourceId))
            db.Sources.Add(new Source
            {
                Id = sourceId, Name = sourceId, Url = $"https://playriftbound.com/{sourceId}",
                Type = trustTier == 1 ? "official" : "community", TrustTier = trustTier,
                Parser = "pdf", Cadence = "daily",
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

    /// <summary>rb-ai dat structureel 429 geeft (#251), met een no-op backoff zodat
    /// de test niet echt wacht.</summary>
    private static RbAiClient RateLimitedAi() => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance)
    {
        RetryDelay = (_, _) => Task.CompletedTask,
    };

    /// <summary>rb-ai dat 500 geeft mét een machine-leesbare uitvalsoort (#281) —
    /// het pad dat de 22 spoorloze mining-uitvallen alsnog verklaarbaar maakt.</summary>
    private static RbAiClient FailingAi(string body) => new(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>Als <see cref="Ai"/>, maar bewaart elke verzonden payload.</summary>
    private static RbAiClient CapturingAi(List<string> bodies, Func<string?> body) => new(
        new HttpClient(new StubHandler(req =>
        {
            lock (bodies) bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return body() is { } b
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(b, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

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
