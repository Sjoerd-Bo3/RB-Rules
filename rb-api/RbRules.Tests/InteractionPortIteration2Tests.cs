using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Domain.Ontology;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Poorten-iteratie 2 (#335), end-to-end door de mining- en promotielaag,
/// gekalibreerd op de VOLLEDIGE era-3-populatie-audit (2026-07-22, 16 oordelen:
/// 11 bevestigd, 5 afgekeurd). De vijf afgekeurde promoties stranden elk op hun
/// eigen klasse (A eindpunt-aanwezigheid, B hoofdletter-werkwoord, C kind-omleiding,
/// D resource-vs-keyword); de elf bevestigde blijven promoveren — twee daarvan
/// (Level↔XP, Weaponmaster↔Equip) staan al als Bevestigd1/2 in
/// <c>InteractionKindGateMiningTests</c> en bewaken dezelfde grens.
///
/// De bewijsteksten zijn de ÉCHTE kaartteksten uit de Riot-gallery (VEN/UNL) en de
/// echte definities; alleen unl-t08 (XP Tracker, token — niet in de gallery-dump),
/// de Hidden-definitie en de Predict-sectie zijn gereconstrueerd rond de letterlijke
/// audit-citaten (benoemd per test).
///
/// Corpus-meting die de catalogi draagt (1429 kaartteksten, zie PR): Ready 0×
/// gebracket / 28× hoofdletter / 180× kleine letter; Recycle 0/26/51; XP 0/84/0 —
/// tegenover echte toekenbare keywords als Reaction 153/0/0 en Deflect 83/0/0.
/// Maar 0× gebracket alléén is GEEN werkwoord-criterium: Channel meet óók 0/10/30
/// en is nota bene een bevestigde GRANTS (Baccai Witherclaw) — de catalogus is dus
/// op de audit-oordelen gekalibreerd, niet op de bracket-telling alleen.</summary>
public class InteractionPortIteration2Tests
{
    // ── Klasse A: eindpunt-aanwezigheid in keyword-gedaante ──────────────────

    [Fact]
    public async Task AfgekeurdA_BurnModifiesFlow_StrandtOpEndpointPresence()
    {
        // Audit: "The evidence never mentions a 'Burn' keyword at all". De sectie
        // (gereconstrueerd: Flow-regeltekst met een MODIFIES-anker en het WERKWOORD
        // burn) verankert Burn alleen via de hoofdletter-ongevoelige woordmatch —
        // proza, geen keyword-vermelding. Burn heeft écht geen definitie (fixture).
        using var db = NewDb();
        await SeedEntityAsync(db, "Burn");   // definitie ontbreekt echt (era-3-dump)
        await SeedEntityAsync(db, "Flow", "Flow is present on Spells.");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "826.4",
            "Spells with Flow remain on the stack until the start of your next turn. " +
            "If such a spell would leave play this way, you may burn the top card of " +
            "your deck instead.");

        var r = await RunMechanic(db, "mechanic:Burn", "mechanic:Flow", "MODIFIES");

        await AssertDegraded(db, r, "endpoint_presence");
    }

    // ── Klasse B: hoofdletter-werkwoord-lek (verb-like catalogus) ────────────

    [Fact]
    public async Task AfgekeurdB_HungryWolfGrantsReady_StrandtOpWoordvorm()
    {
        // Echte kaarttekst VEN-125/166: "Ready me" is de gebiedende wijs midden in
        // een ability-zin (na ":rb_rune_order::" — niet zins-initieel volgens de
        // poortdefinitie) en passeerde zo de #330-woordvormpoort. Gemeten: [Ready]
        // komt in 1429 kaartteksten 0× voor — een GRANTS-Ready-claim op kaarttekst
        // is per constructie de werkwoord-verwarring.
        using var db = NewDb();
        await SeedEntityAsync(db, "Ready");
        await SeedCardAsync(db, "ven-125-166", "Hungry Wolf",
            ":rb_rune_order:: Ready me and give me +1 :rb_might: this turn. Use only " +
            "if you've chosen an enemy unit this turn and only once each turn.");

        var r = await RunCard(db, "card:ven-125-166", "mechanic:Ready", "GRANTS");

        await AssertDegraded(db, r, "word_form");
    }

    // ── Klasse C: kind-omleiding (woordvormpoort verbreed naar REQUIRES) ─────

    [Fact]
    public async Task AfgekeurdC_PredictRequiresRecycle_StrandtOpWoordvorm()
    {
        // Gisteren strandde dit paar als GRANTS op de woordvormpoort (#330,
        // Betwist7); vandaag kwam het terug als REQUIRES — "may recycle it" is
        // optioneel, geen vereiste. Sectie gereconstrueerd rond het audit-citaat,
        // mét een schoon REQUIRES-anker elders (anders was dit een kind_anchor-
        // strand en geen omleiding).
        using var db = NewDb();
        await SeedEntityAsync(db, "Predict");
        await SeedEntityAsync(db, "Recycle");
        await SeedRuleSectionAsync(db, "core-rules-pdf", "819.1",
            "Predict is a keyword action. To Predict, look at the top card of your " +
            "deck. You may recycle it. A player must resolve each Predict fully " +
            "before taking further game actions.");

        var r = await RunMechanic(db, "mechanic:Predict", "mechanic:Recycle", "REQUIRES");

        await AssertDegraded(db, r, "word_form");
    }

    [Fact]
    public async Task SynthetischC2_OptioneleKostAlsRequires_StrandtOpOptionality()
    {
        // SYNTHETISCH (benoemd): het echte era-3-voorbeeld van deze klasse
        // (Safety Inspector, "You may spend 3 XP as an additional cost") draagt in
        // zin 2 een los "must kill" — precies het gedocumenteerde zin-scope-
        // restrisico. Deze fixture isoleert het tegen-anker: het enige
        // REQUIRES-anker ("spend") staat in dezelfde zin als "may".
        using var db = NewDb();
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedCardAsync(db, "syn-001-001", "Synthetic Tutor",
            "You may spend 3 XP to draw a card this turn.");

        var r = await RunCard(db, "card:syn-001-001", "mechanic:XP", "REQUIRES");

        await AssertDegraded(db, r, "optionality");
    }

    // ── Klasse D: resource-vs-keyword (XP) ───────────────────────────────────

    [Fact]
    public async Task AfgekeurdD1_SafetyInspectorModifiesXp_StrandtOpResourcePatient()
    {
        // Echte kaarttekst UNL-164/219: 3 XP betalen als optionele extra kost
        // VERBRUIKT de resource; het modificeert het XP-mechanisme niet. XP is
        // expliciet geen Game Object (definitie) en meet 0× gebracket / 84×
        // hoeveelheids-taal — GRANTS/MODIFIES met een resource-patient eist de
        // gebrackete keyword-vorm.
        using var db = NewDb();
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedCardAsync(db, "unl-164-219", "Safety Inspector",
            "You may spend 3 XP as an additional cost to play me. When you play me, " +
            "each player must kill one of their units. If you paid my additional " +
            "cost, you don't kill a unit this way.");

        var r = await RunCard(db, "card:unl-164-219", "mechanic:XP", "MODIFIES");

        await AssertDegraded(db, r, "resource_patient");
    }

    [Fact]
    public async Task AfgekeurdD2_GardensGrantsXp_StrandtOpResourcePatient()
    {
        // Echte kaarttekst UNL-213/219: een activated ability die 1 XP oplevert is
        // geen toekenning van een keyword "XP". "have" draagt het GRANTS-anker en
        // XP staat als hoofdletter-term midden in de zin — beide #330-poorten
        // passeren, alleen de resource-poort ziet het verschil.
        using var db = NewDb();
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedCardAsync(db, "unl-213-219", "Gardens of Becoming",
            "Units here have \":rb_exhaust:: Gain 1 XP.\"");

        var r = await RunCard(db, "card:unl-213-219", "mechanic:XP", "GRANTS");

        await AssertDegraded(db, r, "resource_patient");
    }

    // ── De bevestigde era-3-rijen: blijven promoveren (de kalibratie-wachters) ──

    [Theory]
    // UNL-158/219 Shepherd's Heirloom — "Spend 1 XP (Pay the cost: …)" (echt).
    [InlineData("unl-158-219", "Shepherd's Heirloom",
        "When you play this, gain 1 XP.[Equip] — Spend 1 XP (Pay the cost: Attach " +
        "this to a unit you control.)",
        "XP", "XP is not a Game Object.", "REQUIRES")]
    // UNL-203/219 Poppy — "Spend 3 XP, :rb_exhaust:: Draw 1." (echt).
    [InlineData("unl-203-219", "Poppy - Keeper of the Hammer",
        "When you hold, gain 1 XP.Spend 3 XP, :rb_exhaust:: Draw 1.",
        "XP", "XP is not a Game Object.", "REQUIRES")]
    // UNL-T08 XP Tracker — token, niet in de gallery-dump; GERECONSTRUEERD rond de
    // audit-citaten ("tracking gained XP, spending XP").
    [InlineData("unl-t08", "XP Tracker",
        "Use this to track the XP you have gained. When you spend XP, update the " +
        "total here.",
        "XP", "XP is not a Game Object.", "REQUIRES")]
    // VEN-054/166 Questionable Tome — "Disempower this, …: Draw 1." (echt).
    [InlineData("ven-054-166", "Questionable Tome",
        "[Empower] —  :rb_exhaust: (Pay the cost: Empower me. Use only if not " +
        "Empowered.)Disempower this, :rb_energy_1:, :rb_exhaust:: Draw 1.",
        "Disempower", null, "REQUIRES")]
    // VEN-078/166 Baccai Witherclaw — "Channel 2 runes exhausted." (echt). Dé
    // wachter tegen over-verbreding van de verb-like catalogus: Channel meet óók
    // 0× gebracket, maar dit is een bevestigde GRANTS op de hoofdlettervorm.
    [InlineData("ven-078-166", "Baccai Witherclaw",
        "[Empower] :rb_energy_1::rb_rune_rainbow::rb_rune_rainbow: " +
        "(:rb_energy_1::rb_rune_rainbow::rb_rune_rainbow:: Empower me. Use only if " +
        "not Empowered.)[Empowered][>] I have +2 :rb_might:.[Empowered][>][>>]" +
        "[Deathknell][>] Channel 2 runes exhausted. (When I die while Empowered, " +
        "get the effect.)",
        "Channel", null, "GRANTS")]
    // VEN-087/166 Hextech Disc — "Disempower this, …" (echt).
    [InlineData("ven-087-166", "Hextech Disc",
        "[Empower] — :rb_exhaust: (Pay the cost: Empower this. Use only if not " +
        "Empowered.)Disempower this, :rb_energy_1:, :rb_exhaust:: Play a 3 " +
        ":rb_might: Mech unit token to your base.",
        "Disempower", null, "REQUIRES")]
    // VEN-133/166 Glowstone — "Use only if not Empowered." (echt).
    [InlineData("ven-133-166", "Glowstone",
        "[Empower] :rb_rune_rainbow::rb_rune_rainbow: " +
        "(:rb_rune_rainbow::rb_rune_rainbow:: Empower me. Use only if not " +
        "Empowered.)Disempower this, :rb_exhaust:: Choose a player. They gain " +
        "control of this and recall it. (Send it to their base.)At the end of your " +
        "turn, kill this and deal 5 to all units you control.",
        "Empowered", "Empowered is a Dependent Keyword.", "REQUIRES")]
    // VEN-146/166 Siphoning Strike — "channel 1 rune exhausted" in kleine letters
    // (echt). Dé wachter op de scope van de C1-verbreding: een MODIFIES-doel drukt
    // legitiem in werkwoordsvorm, dus de woordvormpoort blijft daar uit.
    [InlineData("ven-146-166", "Siphoning Strike",
        "Deal 4 to a unit at a battlefield. If you control 7 or more runes, deal 7 " +
        "to it instead. When it dies this turn, channel 1 rune exhausted.",
        "Channel", null, "MODIFIES")]
    public async Task Bevestigd_CardClaims_BlijvenPromoveerbaar(
        string cardId, string cardName, string text,
        string patient, string? patientDefinition, string kind)
    {
        using var db = NewDb();
        await SeedEntityAsync(db, patient, patientDefinition);
        await SeedCardAsync(db, cardId, cardName, text);

        var r = await RunCard(db, $"card:{cardId}", $"mechanic:{patient}", kind);

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
        Assert.NotNull(ix.PromotedAt);
    }

    [Fact]
    public async Task Bevestigd_HiddenGrantsReaction_BlijftPromoveerbaar()
    {
        // Mech↔mech-wachter: "(Hidden cards have [Reaction].)" — audit-citaat; de
        // definitie is daaromheen GERECONSTRUEERD (de era-3-dump droeg haar niet).
        // "have" draagt GRANTS, [Reaction] is gebracket, Hidden staat in
        // keyword-gedaante — alle poorten passeren.
        using var db = NewDb();
        await SeedEntityAsync(db, "Hidden",
            "Hidden is a keyword ability. Hidden cards have [Reaction] while they " +
            "are face down.");
        await SeedEntityAsync(db, "Reaction", "Reaction is a Permissive keyword.");

        var r = await RunMechanic(db, "mechanic:Hidden", "mechanic:Reaction", "GRANTS");

        Assert.Equal(1, r.Promoted);
        Assert.Equal(0, r.Candidates);
        var ix = await db.Interactions.SingleAsync();
        Assert.Equal(InteractionStatus.Promoted, ix.Status);
    }

    // ── Soort-wissel-telemetrie (C3): zichtbaar in het run-detail ─────────────

    [Fact]
    public async Task KindSwitch_HerVoorstelOnderAndereSoort_WordtGeteldInRunDetail()
    {
        // De dedupe-sleutel bevat Kind, dus een soort-wissel omzeilt de
        // upsert-historie van zijn broertje — de wissel is bewust telemetrie
        // (geen poort: een soort-correctie is legitiem; de inhouds-poorten vangen
        // de junk op inhoud), maar nooit stil (ADR-20). Hier: GRANTS strandt op
        // resource_patient, het her-voorstel REQUIRES in dezelfde run telt als
        // soort-wissel.
        using var db = NewDb();
        await SeedEntityAsync(db, "XP", "XP is not a Game Object.");
        await SeedCardAsync(db, "unl-213-219", "Gardens of Becoming",
            "Units here have \":rb_exhaust:: Gain 1 XP.\"");

        var payload = JsonSerializer.Serialize(new
        {
            interactions = new object[]
            {
                new { from = "card:unl-213-219", to = "mechanic:XP", kind = "GRANTS",
                      interacts = true, conditions = Array.Empty<object>() },
                new { from = "card:unl-213-219", to = "mechanic:XP", kind = "REQUIRES",
                      interacts = true, conditions = Array.Empty<object>() },
            },
        });
        var svc = new BreinInteractionMiningService(
            db, Ai(() => payload),
            new EntityResolutionService(db), new InteractionPromotionService(db));
        var r = await svc.RunAsync(maxFocusCards: 1, maxMechanicSubjects: 0);

        Assert.Equal(1, r.KindSwitches);
        Assert.Contains("soort-wissels×1", r.Summary);
        Assert.Equal(2, await db.Interactions.CountAsync());   // beide rijen bestaan
    }

    // ── Gate-takken en tombstone-symmetrie voor de drie nieuwe signalen ──────

    [Fact]
    public void Gate_NieuwePoorten_DegraderenNaarCandidateMetEigenToken()
    {
        Assert.Equal((InteractionGateOutcome.Candidate, InteractionGatePorts.EndpointPresence),
            Verdict(Signals(endpointPresence: false)));
        Assert.Equal((InteractionGateOutcome.Candidate, InteractionGatePorts.Optionality),
            Verdict(Signals(requiresNotOptional: false)));
        Assert.Equal((InteractionGateOutcome.Candidate, InteractionGatePorts.ResourcePatient),
            Verdict(Signals(resourcePatient: false)));

        static (InteractionGateOutcome, string?) Verdict(InteractionGateSignals s)
        {
            var r = InteractionPromotionGate.Evaluate(s);
            Assert.False(r.WritesTombstone);   // stranden = Candidate, nooit een grafsteen
            return (r.Outcome, r.DegradedBy);
        }
    }

    [Fact]
    public void Gate_NegatiefVerdictMetGestrandeNieuwePoort_GeenDuurzameTombstone()
    {
        // #324b-symmetrie: bewijs dat niet sterk genoeg is om te promoveren, is
        // niet sterk genoeg om de sleutel permanent te sluiten.
        foreach (var s in new[]
        {
            Signals(endpointPresence: false, verdict: false),
            Signals(requiresNotOptional: false, verdict: false),
            Signals(resourcePatient: false, verdict: false),
        })
        {
            var r = InteractionPromotionGate.Evaluate(s);
            Assert.Equal(InteractionGateOutcome.Rejected, r.Outcome);
            Assert.False(r.WritesTombstone);
        }
    }

    private static InteractionGateSignals Signals(
        bool endpointPresence = true, bool requiresNotOptional = true,
        bool resourcePatient = true, bool verdict = true) => new(
        SchemaValid: true, SchemaReason: null, LexicalSupport: true,
        ConsensusCount: 1, ConsensusThreshold: 2, LlmVerdictInteracts: verdict,
        IsEmergentCardCardPair: false, HasBlockingTombstone: false,
        EndpointPresenceSupport: endpointPresence,
        RequiresNotOptional: requiresNotOptional,
        ResourcePatientSupport: resourcePatient);

    // ── Helper-units, gekalibreerd op de echte teksten ────────────────────────

    [Fact]
    public void EndpointPresence_KleineLetterEnGlyphVormen_TellenNiet()
    {
        Assert.True(InteractionEndpointPresence.Applies(EntityType.Mechanic));
        Assert.False(InteractionEndpointPresence.Applies(EntityType.Card));
        Assert.False(InteractionEndpointPresence.Applies(EntityType.Unit));

        // Werkwoord- en glyphtoken-vormen zijn geen keyword-vermelding.
        Assert.False(InteractionEndpointPresence.MentionedAsKeyword(
            "you may burn the top card of your deck instead", "Burn"));
        Assert.False(InteractionEndpointPresence.MentionedAsKeyword(
            "Pay :rb_burn: to trigger this.", "Burn"));
        // Hoofdletter (óók zins-initiaal: aanwezigheids-check, milder dan de
        // woordvormpoort) en gebracket tellen wél.
        Assert.True(InteractionEndpointPresence.MentionedAsKeyword(
            "Burn is a keyword ability.", "Burn"));
        Assert.True(InteractionEndpointPresence.MentionedAsKeyword(
            "Spells with [Burn] deal damage over time.", "Burn"));
        Assert.True(InteractionEndpointPresence.MentionedAsKeyword(
            "Spells with Flow remain on the stack.", "Flow"));
    }

    [Fact]
    public void Optionality_EchteTeksten_MayZinOndermijntAnkerZinNiet()
    {
        // Safety Inspector, zin 1 (echt): het enige anker ("spend"/"cost") staat
        // in dezelfde zin als "may" — ondermijnd.
        const string maySentence = "You may spend 3 XP as an additional cost to play me.";
        Assert.True(RequiresOptionality.HasAnchor(maySentence));
        Assert.False(RequiresOptionality.HasCleanAnchor(maySentence));

        // Poppy (echt): "Spend 3 XP, :rb_exhaust:: Draw 1." — schoon anker.
        Assert.True(RequiresOptionality.HasCleanAnchor(
            "When you hold, gain 1 XP.Spend 3 XP, :rb_exhaust:: Draw 1."));

        // GEDOCUMENTEERD RESTRISICO (zin-scope): de volledige Safety
        // Inspector-tekst draagt in zin 2 een los "must kill" over iets anders —
        // dat schone anker redt een REQUIRES-claim op deze kaart. Zelfde grens
        // als het kind-anker (#330).
        Assert.True(RequiresOptionality.HasCleanAnchor(
            "You may spend 3 XP as an additional cost to play me. When you play " +
            "me, each player must kill one of their units."));
    }

    [Fact]
    public void ResourceMechanics_AlleenGrantsModifiesMetResourcePatient()
    {
        Assert.True(ResourceMechanics.Applies("GRANTS", EntityType.Mechanic, "XP"));
        Assert.True(ResourceMechanics.Applies("MODIFIES", EntityType.Mechanic, "xp"));
        // REQUIRES blijft erbuiten: XP spenderen is er echt van afhangen (drie
        // bevestigde era-3-rijen).
        Assert.False(ResourceMechanics.Applies("REQUIRES", EntityType.Mechanic, "XP"));
        // Geen resource, geen mechanic-patient → niet van toepassing.
        Assert.False(ResourceMechanics.Applies("GRANTS", EntityType.Mechanic, "Deflect"));
        Assert.False(ResourceMechanics.Applies("GRANTS", EntityType.Card, "XP"));

        // De gebrackete vorm is de enige keyword-taal die de poort accepteert.
        Assert.False(KeywordWordForm.AppearsBracketed("Gain 1 XP.", "XP"));
        Assert.True(KeywordWordForm.AppearsBracketed("Units with [XP] counters.", "XP"));
    }

    [Fact]
    public void VerbLike_AlleenGebracketTelt_CatalogusIsReadyEnRecycle()
    {
        // Catalogus-inhoud met uitgeschreven literals (#286d): op audit-oordelen
        // gekalibreerd — Channel/Disempower meten óók 0× gebracket maar zijn
        // bevestigde promoties en horen er NIET in.
        Assert.Equal(2, KeywordWordForm.VerbLikeKeywords.Count);
        Assert.Contains("Ready", KeywordWordForm.VerbLikeKeywords);
        Assert.Contains("Recycle", KeywordWordForm.VerbLikeKeywords);
        Assert.DoesNotContain("Channel", KeywordWordForm.VerbLikeKeywords);
        Assert.DoesNotContain("Disempower", KeywordWordForm.VerbLikeKeywords);
    }

    // ── testinfra (spiegel van InteractionKindGateMiningTests, minimaal) ──────

    private static Task<BreinInteractionMiningResult> RunMechanic(
        RbRulesDbContext db, string from, string to, string kind) =>
        Run(db, from, to, kind, maxFocusCards: 0, maxMechanicSubjects: 1);

    private static Task<BreinInteractionMiningResult> RunCard(
        RbRulesDbContext db, string from, string to, string kind) =>
        Run(db, from, to, kind, maxFocusCards: 1, maxMechanicSubjects: 0);

    private static async Task<BreinInteractionMiningResult> Run(
        RbRulesDbContext db, string from, string to, string kind,
        int maxFocusCards, int maxMechanicSubjects)
    {
        var payload = new { from, to, kind, interacts = true, conditions = Array.Empty<object>() };
        var svc = new BreinInteractionMiningService(
            db, Ai(() => JsonSerializer.Serialize(new { interactions = new[] { payload } })),
            new EntityResolutionService(db), new InteractionPromotionService(db));
        return await svc.RunAsync(
            maxFocusCards: maxFocusCards, maxMechanicSubjects: maxMechanicSubjects);
    }

    /// <summary>De gedeelde strand-vorm: één Candidate (nooit stil weg), nul
    /// promoties, de poort-token in de status_reason, geen grafsteen. Dat de reden
    /// een poort-token draagt bewijst de fixture-echtheid (#286d): de poorten vuren
    /// alleen ná deterministische steun, dus zonder poort was dit een promotie.</summary>
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

    private static async Task SeedCardAsync(
        RbRulesDbContext db, string id, string name, string text)
    {
        db.Cards.Add(new Card
        {
            RiftboundId = id, Name = name, Type = "Unit", TextPlain = text,
            Mechanics = [],
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRuleSectionAsync(
        RbRulesDbContext db, string sourceId, string sectionCode, string text)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == sourceId))
            db.Sources.Add(new Source
            {
                Id = sourceId, Name = sourceId, Url = $"https://playriftbound.com/{sourceId}",
                Type = "official", TrustTier = 1, Parser = "pdf", Cadence = "daily",
            });
        db.RuleChunks.Add(new RuleChunk
        {
            SourceId = sourceId, SectionCode = sectionCode, ChunkIndex = 1, Text = text,
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
