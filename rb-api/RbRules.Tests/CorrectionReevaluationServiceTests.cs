using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Her-evaluatie van één Correction op een beheerder-opmerking (#184):
/// draait de hybride poort (#177/#185/#188) opnieuw voor dít ene item. Roept
/// de bestaande domeinlogica aan (ClarificationGrounding/ClaimTopicMapper)
/// zonder ze te wijzigen — deze tests dekken de nieuwe orkestratie, niet die
/// onderliggende poort zelf (die staat al onder test in
/// ClarificationMiningServiceTests). Sinds #188 draait de informativiteits-
/// toets hier via een lichte rb-ai-call (<see cref="RbAiClient"/> gestubd,
/// zelfde patroon als ClarificationMiningServiceTests) i.p.v. rechtstreeks
/// <see cref="ClarificationInformativeness.IsMetaOnly"/> — de meeste tests
/// hieronder geven de AI-stub bewust null (AI-uitval) mee, wat het bestaande
/// IsMetaOnly-fallbackgedrag oplevert; twee nieuwe tests onderaan dekken
/// expliciet het LLM-oordeel en de AI-uitval-fallback zelf.</summary>
public class CorrectionReevaluationServiceTests
{
    private const string SourceId = "playriftbound-com-unleashed-rules-faq";
    private const string GroundedQuote = "Recall means you finalize an item on the chain";

    [Fact]
    public async Task ReevaluateAsync_AnkerCorrectieInOpmerking_PromoveertPendingNaarVerified()
    {
        // De LLM ankerde het onderwerp fout (typo "Recal", resolvet niet) —
        // de beheerder corrigeert met een ankerregel in de opmerking.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        db.MechanicKeywords.Add(new MechanicKeyword { Term = "Recall", Status = "accepted" });
        var correction = new Correction
        {
            Scope = "concept", Ref = "Recal",
            Text = $"Recall betekent finalizen op de chain.\n\nCitaat uit de bron: “{GroundedQuote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "onderwerp 'Recal' (concept) niet herkend",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "mechanic:Recall");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("mechanic", correction.Scope);
        Assert.Equal("Recall", correction.Ref);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.StatusReason);
        Assert.NotNull(correction.VerifiedAt);
        Assert.Equal("mechanic:Recall", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_OpmerkingZonderGeldigAnker_BlijftPending_MetOpmerkingBewaard()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "concept", Ref = "Onbekende term", Text = "Iets over een onbekende term.",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "onderwerp 'Onbekende term' (concept) niet herkend",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "dit klopt volgens mij toch echt wel");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status);
        Assert.Contains("niet herkend", correction.StatusReason);
        Assert.Equal("dit klopt volgens mij toch echt wel", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AfgewezenItem_BewaartOpmerking_HerEvalueertNiet()
    {
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "...",
            Provenance = $"clarify-mining:{SourceId}", Status = "rejected",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "toch even nakijken");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Contains("afgewezen", r.Reason);
        Assert.Equal("rejected", correction.Status); // geen stiekeme her-evaluatie op een tombstone
        Assert.Equal("toch even nakijken", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_GeenClarifyMiningOorsprong_MaarOnverified_BewaartAlleenOpmerking()
    {
        // Een niet-clarify-mining Correction is in de praktijk altijd meteen
        // verified (chat-ruling/review-notitie-promotie) — dit dekt de
        // defensieve tak voor een hypothetisch onverified geval zonder
        // brontekst: geen poort om tegen te draaien, alleen de opmerking bewaart.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "claim", Ref = "claim:1", Text = "Zo zit het wél.",
            Provenance = "review-notitie", Status = "unverified",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "extra toelichting");

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status); // ongemoeid, geen poort van toepassing
        Assert.Equal("extra toelichting", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AlGeverifieerdMaarNietClarifyMining_DegradeertNooit()
    {
        // Chat-rulings/review-notitie-promoties zijn al verified — her-
        // evaluatie mag dat nooit terugdraaien, ongeacht de oorsprong.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "claim", Ref = "claim:1", Text = "Zo zit het wél.",
            Provenance = "review-notitie", Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "extra toelichting");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status); // ongemoeid
        Assert.Equal("extra toelichting", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_AlGeverifieerdItem_DegradeertNooit_BewaartAlleenOpmerking()
    {
        // Zelfde no-demote-invariant als ClarificationMiningService.StoreAsync:
        // her-evaluatie op een al geverifieerde ruling mag hem nooit terugzetten
        // naar pending, ook niet als de brontekst inmiddels is verdwenen.
        using var db = NewDb();
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion", Text = "Legion betekent finalizen.",
            Provenance = $"clarify-mining:{SourceId}", // geen Document gezaaid: zou "niet grounded" zijn
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, "nog even bevestigd");

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
        Assert.Equal("nog even bevestigd", correction.ReviewNote);
    }

    [Fact]
    public async Task ReevaluateAsync_OnbekendId_GeeftNotFound()
    {
        using var db = NewDb();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(999, "opmerking");

        Assert.Equal(ReevaluateOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task ReevaluateAsync_ZonderOpmerking_HerToetstMetBestaandeOnderwerp()
    {
        // Geen nieuwe opmerking meegeven (leeg formulier "opnieuw evalueren")
        // draait de poort gewoon opnieuw met het bestaande Scope/Ref.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"Legion betekent finalizen.\n\nCitaat uit de bron: “{GroundedQuote.Replace("Recall", "Legion")}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, null);

        // "Legion" zit al in het seed-mechaniek-vocabulaire (MechanicMiner) ⇒
        // anchored; grounded via het citaat in de brontekst ⇒ verified.
        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.ReviewNote); // geen opmerking meegegeven ⇒ niets bewaard
    }

    // --- #188: het LLM-oordeel stuurt de informativiteitspoort, met
    // IsMetaOnly als deterministisch vangnet bij AI-uitval/parse-gat --------

    [Fact]
    public async Task ReevaluateAsync_LlmOperativeTrue_OverruledMetaOnlyRegex_Verified()
    {
        // Vals-positief-scenario (#185-review, zelfde zin als
        // ClarificationMiningServiceTests.Gate_OperatieveWijzigZin_…):
        // IsMetaOnly alleen zou dit ten onrechte meta-only vinden. Het LLM
        // zegt hier terecht operative:true.
        using var db = NewDb();
        const string quote = "Legion means you finalize an item on the chain";
        const string clarification =
            "The rule was clarified so that activated abilities with Legion trigger only once per turn.";
        Assert.True(ClarificationInformativeness.IsMetaOnly(clarification)); // de regex zit er hier naast
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ", Url = "https://playriftbound.com/en-us/news/faq",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = SourceId, Content = $"Uitleg over Legion. {quote}.", ContentHash = "hash",
        });
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"{clarification}\n\nCitaat uit de bron: “{quote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => """{"operative": true}"""));

        var r = await svc.ReevaluateAsync(correction.Id, null);

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.StatusReason);
        Assert.NotNull(correction.VerifiedAt);
    }

    [Fact]
    public async Task ReevaluateAsync_LlmOperativeFalse_OverruledInformatieveRegex_BlijftPending()
    {
        // Vals-negatief-scenario (#185-review, zelfde zin als
        // ClarificationMiningServiceTests.Gate_LegeAankondigingMetDubbelePunt_…):
        // IsMetaOnly alleen zou dit "informatief" noemen (dubbele-punt-uitweg).
        // Het LLM zegt hier terecht operative:false.
        using var db = NewDb();
        const string quote = "Legion means you finalize an item on the chain";
        const string clarification = "Legion was clarified: refer to the updated core rules.";
        Assert.False(ClarificationInformativeness.IsMetaOnly(clarification)); // de regex zit er hier naast
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ", Url = "https://playriftbound.com/en-us/news/faq",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = SourceId, Content = $"Uitleg over Legion. {quote}.", ContentHash = "hash",
        });
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"{clarification}\n\nCitaat uit de bron: “{quote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => """{"operative": false}"""));

        var r = await svc.ReevaluateAsync(correction.Id, null);

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status);
        Assert.Contains("aankondiging zonder regelinhoud", correction.StatusReason);
        Assert.Null(correction.VerifiedAt);
    }

    [Fact]
    public async Task ReevaluateAsync_AiUitval_ValtTerugOpIsMetaOnly_KaleAankondiging_BlijftPending()
    {
        // AI-uitval (rb-ai niet beschikbaar — de stub geeft null, zoals een
        // niet-2xx-respons) mag nooit een 500 geven: de poort degradeert naar
        // de deterministische IsMetaOnly-heuristiek. Voor een kale
        // aankondiging is dat "pending", exact het gedrag van vóór #188.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"Legion is verduidelijkt.\n\nCitaat uit de bron: “{GroundedQuote.Replace("Recall", "Legion")}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.ReevaluateAsync(correction.Id, null);

        Assert.Equal(ReevaluateOutcome.StillPending, r.Outcome);
        Assert.Equal("unverified", correction.Status);
        Assert.Contains("aankondiging zonder regelinhoud", correction.StatusReason);
    }

    [Fact]
    public async Task ReevaluateAsync_OnbruikbaarAiAntwoord_ValtTerugOpIsMetaOnly_Verified()
    {
        // Parse-gat (rb-ai antwoordt wél, maar zonder bruikbaar
        // "operative"-veld): zelfde fallback als AI-uitval, niet een crash.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"Legion betekent finalizen.\n\nCitaat uit de bron: “{GroundedQuote.Replace("Recall", "Legion")}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = "eerdere reden",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => "geen bruikbare JSON hier"));

        var r = await svc.ReevaluateAsync(correction.Id, null);

        Assert.Equal(ReevaluateOutcome.Verified, r.Outcome);
        Assert.Equal("verified", correction.Status);
    }

    // --- #188 increment 3: anker-herstel-pas -----------------------------
    //
    // Productiedata (issue #199, comment 2026-07-16): 117 van de 133 pending
    // clarify-corrections falen op anker-resolutie — de extractie koos een
    // vrije-vorm-onderwerp buiten het vocabulaire. RepairPendingAnchorsAsync
    // is de geautomatiseerde tegenhanger van de handmatige
    // ReviewNoteAnchor-correctie hierboven: de LLM kiest zelf een anker uit
    // het echte vocabulaire, en dezelfde poort (ApplyGateAsync) her-toetst.

    [Fact]
    public async Task RepairPendingAnchorsAsync_HerkendAnker_GrondEnInformatief_PromoveertNaarVerified()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var quote = GroundedQuote.Replace("Recall", "Legion");
        var correction = new Correction
        {
            Scope = "concept", Ref = "battlefield control without units",
            Text = $"Legion means you finalize an item on the chain, so Battering Ram checks that status.\n\nCitaat uit de bron: “{quote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified",
            StatusReason = "onderwerp 'battlefield control without units' (concept) niet herkend",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var ai = RoutedAi((prompt, system) => system.Contains("herstelt het onderwerp-anker")
            ? """{"topicType": "mechanic", "topicRef": "Legion"}"""
            : """{"operative": true}""");
        var svc = new CorrectionReevaluationService(db, ai);

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(1, r.Repaired);
        Assert.Equal(0, r.Skipped);
        Assert.False(r.CapHit);
        Assert.Equal("mechanic", correction.Scope);
        Assert.Equal("Legion", correction.Ref);
        Assert.Equal("verified", correction.Status);
        Assert.Null(correction.StatusReason);
        Assert.NotNull(correction.VerifiedAt);
        // #188 increment 3 (bewust): geen ReviewNote — een geautomatiseerde
        // keuze is geen "beheerder heeft hiernaar gekeken"-signaal.
        Assert.Null(correction.ReviewNote);
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_NoneKeuze_LaatItemOngemoeidStaan()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        const string reason = "onderwerp 'iets heel vaags' (concept) niet herkend";
        var correction = new Correction
        {
            Scope = "concept", Ref = "iets heel vaags",
            Text = "Een verduidelijking die nergens goed bij past.",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = reason,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var ai = RoutedAi((prompt, system) => system.Contains("herstelt het onderwerp-anker")
            ? """{"none": true}"""
            : """{"operative": true}""");
        var svc = new CorrectionReevaluationService(db, ai);

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(0, r.Repaired);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("concept", correction.Scope); // ongewijzigd
        Assert.Equal("iets heel vaags", correction.Ref);
        Assert.Equal("unverified", correction.Status);
        Assert.Equal(reason, correction.StatusReason); // exact ongewijzigd, niet herberekend
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_ReviewNoteItem_BlijftBuitenBeschouwing_BeheerderEigendom()
    {
        // #184: een ReviewNote betekent "een beheerder heeft hiernaar
        // gekeken" — de herstel-pas mag zo'n item niet eens als kandidaat
        // selecteren, laat staan aanraken.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var correction = new Correction
        {
            Scope = "concept", Ref = "iets waar de beheerder al naar keek",
            Text = "Een verduidelijking.",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified",
            StatusReason = "onderwerp 'iets waar de beheerder al naar keek' (concept) niet herkend",
            ReviewNote = "nog even nakijken",
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var calls = 0;
        var ai = new RbAiClient(
            new HttpClient(new StubHandler(_ =>
            {
                calls++;
                return Json(new { answer = """{"topicType": "mechanic", "topicRef": "Legion"}""" });
            }))
            { BaseAddress = new Uri("http://rb-ai.test") },
            NullLogger<RbAiClient>.Instance);
        var svc = new CorrectionReevaluationService(db, ai);

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(0, r.Repaired);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(0, calls); // niet eens een kandidaat — geen LLM-aanroep
        Assert.Equal("concept", correction.Scope);
        Assert.Equal("nog even nakijken", correction.ReviewNote);
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_CapBereikt_RapporteertCapHit_VolgendeRunPaktRestOp()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var quote = GroundedQuote.Replace("Recall", "Legion");
        // Drie kandidaten, elk met een eigen (bestaand, niet-botsend) seed-
        // mechaniek als beoogd anker — MechanicMiner.SeedVocabulary bevat
        // "Legion", "Tank" en "Shield" al zonder dat MechanicKeywords gezaaid
        // hoeft te worden.
        string[] mechanics = ["Legion", "Tank", "Shield"];
        for (var i = 0; i < mechanics.Length; i++)
        {
            db.Corrections.Add(new Correction
            {
                Scope = "concept", Ref = $"vaag onderwerp {i}",
                Text = $"Clarification about mechanic variant {i}.\n\nCitaat uit de bron: “{quote}”",
                Provenance = $"clarify-mining:{SourceId}",
                Status = "unverified",
                StatusReason = $"onderwerp 'vaag onderwerp {i}' (concept) niet herkend",
            });
        }
        await db.SaveChangesAsync();
        var ai = RoutedAi((prompt, system) =>
        {
            if (!system.Contains("herstelt het onderwerp-anker")) return """{"operative": true}""";
            for (var i = 0; i < mechanics.Length; i++)
                if (prompt.Contains($"variant {i}")) return $$"""{"topicType": "mechanic", "topicRef": "{{mechanics[i]}}"}""";
            return """{"none": true}""";
        });
        var svc = new CorrectionReevaluationService(db, ai);

        var first = await svc.RepairPendingAnchorsAsync(maxItems: 2);

        Assert.True(first.CapHit);
        Assert.Equal(2, first.Repaired);
        Assert.Contains("cap van 2 bereikt", first.Message);

        var second = await svc.RepairPendingAnchorsAsync(maxItems: 2);

        Assert.False(second.CapHit); // nog maar 1 kandidaat over, past ruim binnen de cap
        Assert.Equal(1, second.Repaired);
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_AiUitval_SlaatOverZonderCrash()
    {
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        const string reason = "onderwerp 'vaag onderwerp' (concept) niet herkend";
        var correction = new Correction
        {
            Scope = "concept", Ref = "vaag onderwerp", Text = "Een verduidelijking.",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified", StatusReason = reason,
        };
        db.Corrections.Add(correction);
        await db.SaveChangesAsync();
        var svc = new CorrectionReevaluationService(db, Ai(() => null)); // AI-uitval (500)

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(0, r.Repaired);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("unverified", correction.Status);
        Assert.Equal(reason, correction.StatusReason);
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_AnkerWijstNaarBestaandeCorrection_BlijftPending_GeenSpookduplicaat()
    {
        // Het spookduplicaat-scenario (#188 increment 3, Grenzen-afweging):
        // een eerdere run (mining of herstel-pas) plaatste al een verified
        // ruling op het ECHTE anker. Een latere her-mine extraheerde het
        // onderwerp opnieuw met een ANDER (vrij-vorm) anker — zonder
        // ReviewNote is dit tweede item niet zichtbaar voor StoreAsync's
        // cross-bucket-redding. Zou de herstel-pas dit blindelings naar
        // hetzelfde echte anker verplaatsen, dan ontstaan er twee verified
        // rulings over hetzelfde onderwerp. ApplyGateAsync's collision-guard
        // moet dit voorkomen: het item blijft pending, met een zichtbare
        // reden, en de bestaande verified ruling blijft de enige.
        using var db = NewDb();
        await SeedFaqDocumentAsync(db);
        var quote = GroundedQuote.Replace("Recall", "Legion");
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = $"Legion means finalize.\n\nCitaat uit de bron: “{quote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
        });
        var duplicate = new Correction
        {
            Scope = "concept", Ref = "battlefield control without units",
            Text = $"Legion means finalize, alternate phrasing of the same rule.\n\nCitaat uit de bron: “{quote}”",
            Provenance = $"clarify-mining:{SourceId}",
            Status = "unverified",
            StatusReason = "onderwerp 'battlefield control without units' (concept) niet herkend",
        };
        db.Corrections.Add(duplicate);
        await db.SaveChangesAsync();
        var ai = RoutedAi((prompt, system) => system.Contains("herstelt het onderwerp-anker")
            ? """{"topicType": "mechanic", "topicRef": "Legion"}"""
            : """{"operative": true}""");
        var svc = new CorrectionReevaluationService(db, ai);

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(0, r.Repaired);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("concept", duplicate.Scope); // NIET verplaatst
        Assert.Equal("battlefield control without units", duplicate.Ref);
        Assert.Equal("unverified", duplicate.Status);
        Assert.Contains("wijst al naar een bestaande correction", duplicate.StatusReason);
        Assert.Equal(2, await db.Corrections.CountAsync()); // geen derde rij ontstaan
        Assert.Equal(1, await db.Corrections.CountAsync(c => c.Status == "verified")); // nog altijd maar één verified Legion-ruling
    }

    [Fact]
    public async Task RepairPendingAnchorsAsync_GeenKandidaten_MeldtNiksTeDoen()
    {
        using var db = NewDb();
        var svc = new CorrectionReevaluationService(db, Ai(() => null));

        var r = await svc.RepairPendingAnchorsAsync();

        Assert.Equal(0, r.Repaired);
        Assert.Equal(0, r.Skipped);
        Assert.False(r.CapHit);
    }

    private static async Task SeedFaqDocumentAsync(RbRulesDbContext db)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ", Url = "https://playriftbound.com/en-us/news/faq",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = SourceId,
            Content = $"Uitleg over Recall en Legion. {GroundedQuote}. "
                      + $"{GroundedQuote.Replace("Recall", "Legion")}.",
            ContentHash = "hash",
        });
        await db.SaveChangesAsync();
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als ClarificationMiningServiceTests).</summary>
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

    // --- RbAiClient-stub (#188), zelfde patroon als ClarificationMiningServiceTests --

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>null ⇒ 500 (AI-uitval, zoals ClarificationMiningServiceTests.Ai).</summary>
    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    /// <summary>#188 increment 3: RepairPendingAnchorsAsync doet TWEE
    /// verschillende rb-ai-aanroepen per kandidaat (anker-keuze + de
    /// bestaande informativiteitstoets) — deze stub routeert op de
    /// meegestuurde <c>system</c>/<c>prompt</c>-tekst zodat een test elk apart
    /// kan sturen, i.p.v. de kale <see cref="Ai"/>-stub die elke aanroep
    /// hetzelfde antwoord geeft. null ⇒ 500 (AI-uitval).</summary>
    private static RbAiClient RoutedAi(Func<string, string, string?> respond) => new(
        new HttpClient(new StubHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
            var system = doc.RootElement.TryGetProperty("system", out var s) ? s.GetString() ?? "" : "";
            return respond(prompt, system) is { } a
                ? Json(new { answer = a })
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}
