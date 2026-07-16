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

/// <summary>Regressietests voor de FAQ-/clarificatie-concept-extractie
/// (#177). Sinds de autoriteits-review geldt een hybride poort: een concept
/// wordt alleen verified als het grounded is (citaat komt in de brontekst
/// voor) EN anchored (onderwerp resolvet — mechaniek "Legion" zit in het
/// seed-vocabulaire), anders unverified met reden (de reviewqueue in). De
/// seed-content bevat het Legion-citaat, zodat de Legion-fixture verified
/// wordt (Sjoerds doel: vindbaar); een concept zonder citaat (Reflection
/// tokens) belandt terecht ter review. De concept-niveau-dedupe en de
/// grounding/anchor-varianten staan apart in ClarificationMiningDedupeTests
/// resp. ClarificationGateTests. Zelfde testinfra als ClaimMiningServiceTests:
/// echte RbAiClient/EmbeddingService op gestubde HTTP-handlers, EF InMemory.</summary>
public class ClarificationMiningServiceTests
{
    private const string SourceId = "playriftbound-com-unleashed-rules-faq-and-clarifications";
    private const string SourceUrl =
        "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/";

    // Het citaat van het Legion-concept staat letterlijk in de seed-content
    // (SeedFaqDocAsync) ⇒ grounded; mechaniek "Legion" ⇒ anchored ⇒ verified.
    private const string LegionQuote = "Legion means you finalize an item on the chain";

    // Realistische, ingekorte fixture: één multi-concept-alinea zoals de
    // echte FAQ (Reflection tokens + Arcane Shift + Legion in dezelfde slab).
    // Legion draagt een gegrond citaat (verified); Reflection tokens heeft
    // geen citaat ⇒ terecht ter review (pending).
    private const string TwoConceptsAnswer =
        $$"""
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion", "sectionRef": "402.3",
           "clarification": "Legion betekent dat je een item op de chain finalizet.",
           "quote": "{{LegionQuote}}"},
          {"topicType": "concept", "topicRef": "Reflection tokens",
           "clarification": "Reflection tokens tellen niet mee voor het handlimiet."}
        ]}
        """;

    private const string OneConceptAnswer =
        $$"""{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent dat je een item op de chain finalizet.", "quote": "{{LegionQuote}}"}]}""";

    [Fact]
    public async Task RunAsync_GeslaagdeExtractie_MarkeertDocument_GrondedAnchoredVerified_RestTerReview()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Documents);
        Assert.Equal(1, r.Verified); // Legion: grounded + anchored
        Assert.Equal(1, r.Pending);  // Reflection tokens: geen citaat ⇒ ter review
        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClarifiedAt); // pending is een geldige uitkomst, document is verwerkt

        var legion = await db.Corrections.SingleAsync(c => c.Ref == "Legion");
        Assert.Equal("mechanic", legion.Scope);
        Assert.Equal("verified", legion.Status);
        Assert.Null(legion.StatusReason);
        Assert.NotNull(legion.VerifiedAt);
        Assert.NotNull(legion.Embedding);
        Assert.Equal(SourceUrl, legion.SourceRef);
        Assert.Equal($"clarify-mining:{SourceId}", legion.Provenance);
        Assert.Contains("finalize", legion.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Legion means you finalize", legion.Text); // citaat zichtbaar aangehaald
        Assert.Equal("Legion (§402.3)", legion.Question); // §-anker zichtbaar als label

        var concept = await db.Corrections.SingleAsync(c => c.Ref == "Reflection tokens");
        Assert.Equal("concept", concept.Scope);
        Assert.Equal("unverified", concept.Status);
        Assert.Contains("geen citaat", concept.StatusReason); // reden zichtbaar in de reviewqueue
        Assert.Null(concept.VerifiedAt);
        Assert.Equal("Reflection tokens", concept.Question); // geen sectionRef ⇒ kaal label
    }

    [Fact]
    public async Task RunAsync_SectionTopic_UsesRuleSectionScope()
    {
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var raw = """{"clarifications": [{"topicType": "section", "topicRef": "402.3", "clarification": "Een §-verduidelijking."}]}""";
        var svc = new ClarificationMiningService(db, Ai(() => raw), Embeddings(ok: true));

        await svc.RunAsync();

        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("rule_section", ruling.Scope);
        Assert.Equal("402.3", ruling.Ref);
        Assert.Equal("402.3", ruling.Question); // geen dubbele §-suffix als topic zelf al section is
    }

    [Fact]
    public async Task RunAsync_TweedeRunZonderForce_VerwerktGeenDocumentenMeer_IsIdempotent()
    {
        // "Tweede run = 0 nieuw": het document is al ClarifiedAt, dus de
        // eerstvolgende (niet-geforceerde) run slaat het simpelweg over.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        Assert.Equal(1, first.Verified);
        Assert.Equal(1, first.Pending);
        Assert.Equal(0, second.Documents);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    [Fact]
    public async Task RunAsync_GeforceerdeHerRun_ZelfdeExtractie_MaaktGeenDubbeleRuling()
    {
        // Idempotent op conceptniveau — óók als het document opnieuw wordt
        // verwerkt (force) en de LLM identiek antwoordt: StoreAsync werkt de
        // bestaande rulings bij i.p.v. te dupliceren. (Parafrase-herkenning:
        // zie ClarificationMiningDedupeTests.)
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var first = await svc.RunAsync();
        var second = await svc.RunAsync(force: true);

        Assert.Equal(1, first.Verified);
        Assert.Equal(1, first.Pending);
        // zelfde concepten al bekend — bijgewerkt, niet gedupliceerd
        Assert.Equal(0, second.Verified);
        Assert.Equal(0, second.Pending);
        Assert.Equal(2, second.Updated);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    [Fact]
    public async Task RunAsync_BestaandeAlGeingesteBron_WordtVanzelfGebackfilld()
    {
        // Sjoerd-eis: de mining moet terugwerkend over al-geïngeste FAQ-
        // bronnen draaien, niet alleen nieuw-gescande. De bronselectie kent
        // geen tijdvenster — een pre-existing Source+Document (ClarifiedAt
        // was er nog niet toen dit artikel voor het eerst gescand werd, dus
        // start op null) komt bij de eerste run van deze service gewoon mee.
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db, retrievedAt: DateTimeOffset.UtcNow.AddMonths(-6));
        Assert.Null(doc.ClarifiedAt); // "van vóór deze feature"

        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: true));
        var r = await svc.RunAsync();

        Assert.Equal(1, r.Documents);
        Assert.Equal(1, r.Verified); // grounded (citaat in seed-content) + anchored (mechaniek Legion)
        Assert.NotNull(doc.ClarifiedAt);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("Legion", ruling.Ref);
        Assert.Equal("verified", ruling.Status);
    }

    [Fact]
    public async Task RunAsync_NietMatchendeBron_WordtOvergeslagen()
    {
        // Een gewone officiële bron zonder faq/clarification-signaal in
        // id/url/naam hoort niet in deze pijplijn — dat is de claims-/errata-
        // paden, niet clarify.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "playriftbound-com-core-rules", Name = "Core Rules",
            Url = "https://playriftbound.com/core-rules.pdf",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = "playriftbound-com-core-rules", Content = "Regeltekst.", ContentHash = "hash",
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Documents);
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_CommunityBron_WordtOvergeslagen_OokMetFaqInDeNaam()
    {
        // Autoriteitsmodel #166: alleen TrustTier == 1 mag automatisch
        // verified worden — een community-mirror met "faq" in de naam
        // (trust 3) hoort hier niet mee te doen.
        using var db = NewDb();
        db.Sources.Add(new Source
        {
            Id = "community-faq-mirror", Name = "Community FAQ mirror",
            Url = "https://example.com/faq", Type = "community", TrustTier = 3,
            Rank = 10, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = "community-faq-mirror", Content = "Uitleg.", ContentHash = "hash",
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Documents);
    }

    [Fact]
    public async Task RunAsync_DubbeleKeywordBron_WordtNietGemined_MaarWelOpgeruimd()
    {
        // Review-regressie (#185): een bron waarvan de naam/url ZOWEL een
        // FAQ- ALS een patch-notes-woord bevat matcht beide predicaten. Zou
        // mining niet expliciet IsPatchNotesSignal uitsluiten, dan werd zo'n
        // bron gemíned én meteen weer door de retractie hard verwijderd, met
        // de ClarifiedAt-gate die her-mining blokkeert ⇒ stil, permanent
        // verlies van geldige rulings. Patch-notes wint: de bron wordt NIET
        // gemíned (r.Documents == 0), de AI-stub wordt genegeerd, en een
        // eventuele oude clarify-Correction van die bron wordt eenmalig
        // opgeruimd (geen thrash).
        using var db = NewDb();
        const string dualId = "rules-faq-and-patch-notes";
        const string dualUrl =
            "https://playriftbound.com/en-us/news/rules-and-releases/rules-faq-and-patch-notes/";
        db.Sources.Add(new Source
        {
            Id = dualId, Name = "Rules FAQ and Patch Notes", Url = dualUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        db.Documents.Add(new Document
        {
            SourceId = dualId, Content = "Legion means you finalize an item on the chain.",
            ContentHash = "hash",
        });
        // Een al bestaande (verified) clarify-ruling van vóór deze splitsing.
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion means you finalize an item on the chain.",
            Provenance = $"clarify-mining:{dualId}", SourceRef = dualUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Documents);   // niet gemíned (patch-notes wint)
        Assert.Equal(1, r.Retracted);   // oude ruling eenmalig opgeruimd
        Assert.Empty(await db.Corrections.ToListAsync()); // en niet opnieuw aangemaakt (geen thrash)
    }

    [Fact]
    public async Task RunAsync_EmbeddingFailure_LaatDocumentOngemarkeerd_EnLogtReden()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Verified);
        Assert.Equal(0, r.Pending);
        Assert.Contains("redenen in run_log", r.Message);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("embedding mislukt", error.Detail);

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents); // blijft staan voor een volgende run
    }

    [Fact]
    public async Task RunAsync_OnparseerbaarAntwoord_LogtSnippet_EnLaatDocumentStaan()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(
            db, Ai(() => "Ik zie hier geen bruikbare\nconcepten, sorry!"), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("LLM-antwoord onbruikbaar", error.Detail);
        Assert.Contains("Respons (afgekapt): Ik zie hier geen bruikbare concepten, sorry!", error.Detail);
    }

    [Fact]
    public async Task RunAsync_RbAiWeg_LogtUitval_EnLaatDocumentStaan()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => null), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Failed);
        Assert.Null(doc.ClarifiedAt);
        var error = await db.RunLogs.SingleAsync(l => l.Kind == "clarify" && l.Status == "error");
        Assert.Contains("rb-ai niet beschikbaar", error.Detail);
    }

    [Fact]
    public async Task RunAsync_GeenConceptenGevonden_MarkeertDocumentWel()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(
            db, Ai(() => """{"clarifications": []}"""), Embeddings(ok: false));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Failed);
        Assert.NotNull(doc.ClarifiedAt);
        var again = await svc.RunAsync();
        Assert.Equal(0, again.Documents);
    }

    [Fact]
    public async Task RunAsync_CapMiddenInDocument_LaatDocumentOngemarkeerd_TotEenHerRun()
    {
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        var svc = new ClarificationMiningService(db, Ai(() => TwoConceptsAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync(maxItems: 1);

        Assert.Equal(1, r.Verified); // alleen Legion verwerkt (verified), dan cap
        Assert.Contains("cap van 1 bereikt", r.Message);
        // #190: machine-leesbaar equivalent van die tekst — paden draineren hierop.
        Assert.True(r.CapHit);
        Assert.Null(doc.ClarifiedAt);

        var again = await svc.RunAsync();
        Assert.Equal(1, again.Documents);
        Assert.Equal(1, again.Updated); // Legion is al bekend — bijgewerkt
        Assert.Equal(1, again.Pending); // Reflection tokens is nieuw (ter review)
        Assert.False(again.CapHit);
        Assert.NotNull(doc.ClarifiedAt);
        Assert.Equal(2, await db.Corrections.CountAsync());
    }

    // --- hybride poort: grounding + anchoring (#177) --------------------

    [Fact]
    public async Task Gate_CitaatNietInBron_Unverified_MetReden_NietInAskRetrieval()
    {
        using var db = NewDb();
        // Content bevat het opgegeven citaat NIET ⇒ niet grounded (vangt een
        // gehallucineerd citaat, de kernzorg uit de autoriteits-review).
        var doc = await SeedFaqDocAsync(db, content: "Een FAQ zonder het betreffende citaat.");
        var answer = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen.", "quote": "dit citaat komt niet in de bron voor"}]}""";
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(1, r.Pending);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("unverified", ruling.Status);
        Assert.Contains("citaat niet terug te vinden in de bron", ruling.StatusReason);
        Assert.Null(ruling.VerifiedAt);
        // Niet in /ask-retrieval: AskService/BrainService/RulingsService filteren
        // allemaal op Status=="verified" — een pending item met embedding lekt dus niet.
        Assert.Equal(0, await db.Corrections.CountAsync(c => c.Status == "verified"));
    }

    [Fact]
    public async Task Gate_OnbekendAnker_Unverified_MetReden()
    {
        using var db = NewDb();
        // Grounded (citaat in content), maar het onderwerp resolvet niet: geen
        // kaart met deze naam bestaat ⇒ niet anchored ⇒ review (lost de MEDIUM
        // anker-bevinding op: geen stille koppeling aan een kaartpagina).
        var quote = "this quote is present in the source";
        var doc = await SeedFaqDocAsync(db, content: $"FAQ. {quote}. Meer tekst.");
        var answer = $$"""{"clarifications": [{"topicType": "card", "topicRef": "Onbestaande Kaart", "clarification": "Iets over een kaart.", "quote": "{{quote}}"}]}""";
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(1, r.Pending);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("unverified", ruling.Status);
        Assert.Contains("niet herkend", ruling.StatusReason);
        Assert.Contains("Onbestaande Kaart", ruling.StatusReason);
    }

    [Fact]
    public async Task Gate_AfgewezenItem_BijHerRun_BlijftRejected_NietHeropend()
    {
        // Requirement 4: een beheerder-afwijzing (rejected tombstone) mag een
        // volgende mining-run niet heropenen — de menselijke afwijzing houdt stand.
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        // Een eerdere run maakte deze ruling; de beheerder wees hem af (rejected).
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion betekent dat je een item op de chain finalizet.",
            Provenance = $"clarify-mining:{SourceId}", SourceRef = SourceUrl,
            Status = "rejected", Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(0, r.Pending);
        var ruling = await db.Corrections.SingleAsync(); // geen tweede rij
        Assert.Equal("rejected", ruling.Status); // niet heropend
    }

    [Fact]
    public async Task Gate_GoedgekeurdItem_BijHerRun_BlijftVerified_GeenDuplicaat()
    {
        // Een pending item dat de beheerder goedkeurde (verified) mag een
        // volgende run niet degraderen of dupliceren — no-demote + dedupe.
        using var db = NewDb();
        var doc = await SeedFaqDocAsync(db);
        db.Corrections.Add(new Correction
        {
            Scope = "mechanic", Ref = "Legion",
            Text = "Legion betekent dat je een item op de chain finalizet.",
            Provenance = $"clarify-mining:{SourceId}", SourceRef = SourceUrl,
            Status = "verified", VerifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Embedding = new Vector(Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray()),
        });
        await db.SaveChangesAsync();
        var svc = new ClarificationMiningService(db, Ai(() => OneConceptAnswer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(1, r.Updated);
        var ruling = await db.Corrections.SingleAsync(); // geen duplicaat
        Assert.Equal("verified", ruling.Status);
    }

    // --- informativiteits-poort (#185, #188) — de échte Legion-FAQ-fixture -
    //
    // De volgende twee tests geven GEEN "operative"-veld mee in de JSON-stub
    // (ExtractedClarification.Operative blijft dus null) — ze dekken zo
    // meteen ook de #188-fallback: ontbreekt het LLM-oordeel, dan valt de
    // poort terug op ClarificationInformativeness.IsMetaOnly, exact het
    // gedrag van vóór #188.

    [Fact]
    public async Task Gate_LegionOperatieveUitleg_GroundedAnchoredInformative_Verified()
    {
        // De #185-promptinstructie: vat de werkende uitspraak vast (play =
        // finalize voor Legion) MÉT het verbindende voorbeeld (Battering Ram)
        // — dit is precies wat de échte Unleashed Rules FAQ zegt over Legion,
        // in tegenstelling tot de kale aankondiging uit de patch notes. In
        // het Engels opgeslagen (#185, Sjoerd-eis): dicht bij de brontekst,
        // geen Nederlandse parafrase in de corpus.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var answer = $$"""
            {"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "sectionRef": "402.3",
              "clarification": "Play has three technical meanings; for Legion the relevant one is 'finalize' — the moment an item is actually resolved on the chain. Conditional effects such as on Battering Ram check this finalize status.",
              "quote": "{{LegionQuote}}"}]}
            """;
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Verified);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("verified", ruling.Status);
        Assert.Null(ruling.StatusReason);
    }

    [Fact]
    public async Task Gate_LegionKaleAankondiging_GroundedAnchoredMaarNietInformatief_Unverified()
    {
        // De #185-bugvorm: het citaat klopt (grounded) en het onderwerp
        // resolvet (anchored — mechaniek "Legion"), maar de "verduidelijking"
        // zelf zegt alleen DÁT Legion is verduidelijkt, niet WAT er nu geldt.
        // Vóór #185 verifieerde dit soort item automatisch (grounded+anchored
        // was toen de hele poort) — exact de lege Legion-"ruling" uit issue
        // #185. De informativiteits-poort moet dit nu naar review sturen.
        // (Ook al in het Engels, #185-opslagtaal.)
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        var answer = $$"""
            {"clarifications": [{"topicType": "mechanic", "topicRef": "Legion",
              "clarification": "Legion's behavior was clarified in this update.",
              "quote": "{{LegionQuote}}"}]}
            """;
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(1, r.Pending);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("unverified", ruling.Status);
        Assert.Contains("aankondiging zonder regelinhoud", ruling.StatusReason);
        Assert.Null(ruling.VerifiedAt);
    }

    // --- #188: het LLM-oordeel (ec.Operative) stuurt de poort, niet de
    // IsMetaOnly-regex — de twee scenario's uit de adversariële review #185
    // waarop die regex het zelf mis heeft (zie ClarificationInformativeness-
    // Tests voor de losse IsMetaOnly-assertie op dezelfde zinnen). ---------

    [Fact]
    public async Task Gate_OperatieveWijzigZin_LlmOperativeTrue_OverruledMetaOnlyRegex_Verified()
    {
        // Vals-positief-scenario (#185-review): een KORTE operatieve zin met
        // een wijzig-werkwoord ("was clarified") — IsMetaOnly alleen zou dit
        // ten onrechte meta-only vinden (zie ClarificationInformativenessTests.
        // IsMetaOnly_KaleAankondiging_ReturnsTrue voor dezelfde zin). Het LLM
        // zet hier terecht operative:true ⇒ de poort moet verified geven,
        // ondanks wat de regex alleen zou zeggen.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        const string clarification =
            "The rule was clarified so that activated abilities with Legion trigger only once per turn.";
        Assert.True(ClarificationInformativeness.IsMetaOnly(clarification)); // de regex zit er hier naast
        var answer = $$"""
            {"clarifications": [{"topicType": "mechanic", "topicRef": "Legion",
              "clarification": "{{clarification}}",
              "quote": "{{LegionQuote}}", "operative": true}]}
            """;
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Verified);
        Assert.Equal(0, r.Pending);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("verified", ruling.Status);
        Assert.Null(ruling.StatusReason);
        Assert.NotNull(ruling.VerifiedAt);
    }

    [Fact]
    public async Task Gate_LegeAankondigingMetDubbelePunt_LlmOperativeFalse_OverruledInformatieveRegex_Pending()
    {
        // Vals-negatief-scenario (#185-review): een lege aankondiging mét
        // dubbele punt glipt via de dubbele-punt-uitweg door IsMetaOnly heen
        // (zie ClarificationInformativenessTests.IsMetaOnly_OperatieveInhoud_
        // ReturnsFalse voor dezelfde zin — de regex alleen zou dit
        // "informatief" noemen). Het LLM zet hier terecht operative:false ⇒
        // de poort moet naar review sturen, ondanks wat de regex alleen zou
        // zeggen.
        using var db = NewDb();
        await SeedFaqDocAsync(db);
        const string clarification = "Legion was clarified: refer to the updated core rules.";
        Assert.False(ClarificationInformativeness.IsMetaOnly(clarification)); // de regex zit er hier naast
        var answer = $$"""
            {"clarifications": [{"topicType": "mechanic", "topicRef": "Legion",
              "clarification": "{{clarification}}",
              "quote": "{{LegionQuote}}", "operative": false}]}
            """;
        var svc = new ClarificationMiningService(db, Ai(() => answer), Embeddings(ok: true));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Verified);
        Assert.Equal(1, r.Pending);
        var ruling = await db.Corrections.SingleAsync();
        Assert.Equal("unverified", ruling.Status);
        Assert.Contains("aankondiging zonder regelinhoud", ruling.StatusReason);
        Assert.Null(ruling.VerifiedAt);
    }

    // --- testinfra (zelfde patroon als ClaimMiningServiceTests) ----------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

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

    private static RbAiClient Ai(Func<string?> answer) => new(
        new HttpClient(new StubHandler(_ => answer() is { } a
            ? Json(new { answer = a })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://rb-ai.test") },
        NullLogger<RbAiClient>.Instance);

    private static EmbeddingService Embeddings(bool ok) => new(
        new HttpClient(new StubHandler(_ => ok
            ? Json(new { embeddings = new[] { Enumerable.Repeat(0.1f, EmbeddingConfig.Dimensions).ToArray() } })
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)))
        { BaseAddress = new Uri("http://ollama.test") });

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static async Task<Document> SeedFaqDocAsync(
        RbRulesDbContext db, DateTimeOffset? retrievedAt = null, string? content = null)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Unleashed Rules FAQ and Clarifications", Url = SourceUrl,
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "weekly",
        });
        var doc = new Document
        {
            SourceId = SourceId,
            // Standaard bevat de content het Legion-citaat letterlijk ⇒ dat
            // concept is grounded; gate-tests geven eigen content mee.
            Content = content
                ?? "In deze FAQ verduidelijken we enkele mechanieken. "
                   + $"{LegionQuote}, dus het is het moment van finaliseren. "
                   + "Reflection tokens en Arcane Shift komen ook aan bod.",
            ContentHash = "hash",
        };
        if (retrievedAt is { } at) doc.RetrievedAt = at;
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }
}
