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

/// <summary>Steekproef-audit door een sterker model (#255).
///
/// De drie mutatie-eisen uit het issue staan hier als GEDRAG vastgelegd, met
/// uitgeschreven literals (een assertie tegen de constante die ze bewaakt schuift
/// mee — de #286/#293-les):
///  (a) een audit-oordeel verandert NOOIT zelf een tier — maak de service stil
///      promoverend/degraderend en <see cref="RunAsync_NegatiefOordeel_DegradeertNooit_AlleenZichtbaarKanaal"/>
///      gaat rood;
///  (b) een oordeel buiten het gesloten schema wordt GEWEIGERD — maak de parser
///      coulant en <see cref="RunAsync_OordeelBuitenSchema_TeltAlsUitval_ZonderWatermark"/>
///      gaat rood;
///  (c) de 1-op-N-selectie pakt nooit twee keer dezelfde interactie vóór de pool
///      rond is, en een GEFAALDE audit krijgt géén watermark (#249-les).</summary>
public class BreinInteractionAuditServiceTests
{
    // ── (a) De harde regel: meting, nooit een tier-wijziging ─────────────────

    [Fact]
    public async Task RunAsync_PositiefOordeel_SchrijftAuditRijMetProvenance_RaaktDeInteractieNiet()
    {
        using var db = NewDb();
        var ix = await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");
        var promotedAt = ix.PromotedAt;

        var svc = Service(db, () => Verdict(correct: true, supported: true,
            "The rules text explicitly states Deflect prevents Assault damage."));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Audited);
        Assert.Equal(1, r.Sound);
        Assert.Equal(0, r.Disputed);
        Assert.Equal(0, r.Failed);

        // De audit-rij draagt zijn EIGEN provenance (issue-eis): model +
        // promptversie + datum, als uitgeschreven literals.
        var audit = await db.InteractionAudits.SingleAsync();
        Assert.Equal(ix.Id, audit.InteractionId);
        Assert.Equal("claude-opus-4-8", audit.Model);
        Assert.Equal("interaction-audit-v1", audit.PromptVersion);
        Assert.True(audit.Correct);
        Assert.True(audit.SupportedByEvidence);
        Assert.Contains("explicitly", audit.Motivation);
        Assert.Equal("promoted", audit.InteractionStatusAtAudit);

        // Run-provenance: een eigen MiningRun-soort, met het audit-model erop.
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal("interaction_audit", run.Kind);
        Assert.Equal("claude-opus-4-8", run.LlmModel);
        Assert.Equal("interaction-audit-v1", run.PromptVersion);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(audit.RunId, run.Id);

        // En de interactie zelf is per constructie ONAANGERAAKT.
        var after = await db.Interactions.SingleAsync();
        Assert.Equal("promoted", after.Status);
        Assert.Equal(promotedAt, after.PromotedAt);
        Assert.Empty(await db.RejectionTombstones.ToListAsync());
        Assert.Empty(await db.InteractionDecisions.ToListAsync());
        Assert.Empty(await db.ReasoningConflicts.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_GebruiktWerkelijkProviderModelEnMetertUsage()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");
        var svc = Service(db, () =>
            """{"verdicts":[{"correct":true,"supported_by_evidence":true,"motivation":"ok"}],"provider":"claude-agent-sdk","model":"claude-opus-4-9","usage":{"inputTokens":90,"outputTokens":10,"unit":"tokens"}}""");

        await svc.RunAsync();

        Assert.Equal("claude-opus-4-9", (await db.InteractionAudits.SingleAsync()).Model);
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal("opus", run.LlmModelAlias); // auditkeuze blijft los van brein.extract.model
        Assert.Equal("claude-agent-sdk", run.LlmProvider);
        Assert.Equal("claude-opus-4-9", run.LlmModel);
        Assert.Equal(1, run.LlmCalls);
        Assert.Equal(90, run.InputTokens);
        Assert.Equal(10, run.OutputTokens);
        Assert.Null(run.CostUsd);
    }

    [Fact]
    public async Task RunAsync_NegatiefOordeel_DegradeertNooit_AlleenZichtbaarKanaal()
    {
        using var db = NewDb();
        var ix = await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");
        var promotedAt = ix.PromotedAt;

        var svc = Service(db, () => Verdict(correct: false, supported: false,
            "The evidence describes damage prevention, not a counter relationship."));

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Audited);
        Assert.Equal(0, r.Sound);
        Assert.Equal(1, r.Disputed);

        // MUTATIE-EIS (a): een negatief LLM-oordeel draagt geen actie alleen. De
        // interactie blijft LETTERLIJK "promoted", met haar oorspronkelijke
        // PromotedAt; er komt geen tombstone en geen beslissings-memo — wie de
        // service stil laat degraderen, maakt deze asserts rood.
        var after = await db.Interactions.SingleAsync();
        Assert.Equal("promoted", after.Status);
        Assert.Equal(promotedAt, after.PromotedAt);
        Assert.Empty(await db.RejectionTombstones.ToListAsync());
        Assert.Empty(await db.InteractionDecisions.ToListAsync());

        // Wél zichtbaar: het bestaande reviewqueue-kanaal, waar een beheerder
        // beslist. Kanaal en soort als literals.
        var conflict = await db.ReasoningConflicts.SingleAsync();
        Assert.Equal("audit-disputes-interaction", conflict.Kind);
        Assert.Equal("reviewqueue", conflict.Channel);
        Assert.Equal($"interaction:{ix.Id}", conflict.SubjectRef);
        Assert.Equal("open", conflict.Status);
        Assert.Contains("counter relationship", conflict.Memo);
        Assert.Contains("claude-opus-4-8", conflict.Memo);

        // Het oordeel zelf staat als audit-rij vast — de meting blijft eerlijk.
        var audit = await db.InteractionAudits.SingleAsync();
        Assert.False(audit.Correct);
        Assert.False(audit.SupportedByEvidence);
    }

    [Fact]
    public async Task RunAsync_CorrectMaarNietGedragen_TeltAlsBetwist()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");

        // Een feit dat toevallig klopt zonder dat het bewijs het draagt is GEEN
        // gezond oordeel — dat onderscheid is precies wat de audit meet.
        var svc = Service(db, () => Verdict(correct: true, supported: false,
            "Plausible, but the provided evidence never mentions both keywords."));

        var r = await svc.RunAsync();

        Assert.Equal(0, r.Sound);
        Assert.Equal(1, r.Disputed);
        Assert.Single(await db.ReasoningConflicts.ToListAsync());
        Assert.Equal("promoted", (await db.Interactions.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunAsync_HerAuditNaPromptBump_MaaktGeenTweedeConflictRij()
    {
        using var db = NewDb();
        var ix = await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");
        // Eerdere audit-ronde (oude promptversie) liet al een conflict achter.
        db.InteractionAudits.Add(new InteractionAudit
        {
            InteractionId = ix.Id, RunId = Ulid.NewUlid(), Model = "claude-opus-4-8",
            PromptVersion = "interaction-audit-v0", Correct = false, SupportedByEvidence = false,
        });
        db.ReasoningConflicts.Add(new RbRules.Domain.Reasoning.ReasoningConflict
        {
            PatternId = "interaction-audit-sample", Kind = "audit-disputes-interaction",
            Channel = "reviewqueue", SubjectRef = $"interaction:{ix.Id}",
            DedupeKey = $"interaction-audit-sample|interaction:{ix.Id}|",
            RunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();

        var svc = Service(db, () => Verdict(correct: false, supported: false, "Still disputed."));
        var r = await svc.RunAsync();

        // De oude promptversie telt niet als recent oordeel: de interactie is
        // opnieuw geauditeerd (stale-conditie, §3.5)…
        Assert.Equal(1, r.Audited);
        Assert.Equal(2, await db.InteractionAudits.CountAsync());
        // …maar het kanaal is idempotent op de dedupe-sleutel: één open rij.
        Assert.Single(await db.ReasoningConflicts.ToListAsync());
    }

    // ── (b) Het gesloten oordeel: buiten het schema = geweigerd ──────────────

    [Theory]
    [InlineData("""{"verdicts":[{"correct":"yes","supported_by_evidence":true,"motivation":"x"}]}""")] // string i.p.v. boolean
    [InlineData("""{"verdicts":[]}""")]                                                                 // geen oordeel
    [InlineData("""{"verdicts":[{"correct":true,"supported_by_evidence":true},{"correct":false,"supported_by_evidence":false}]}""")] // twee oordelen
    [InlineData("""{"verdicts":[{"correct":true""")]                                                    // afgekapt
    [InlineData("""{"verdicts":"none"}""")]                                                             // schema-drift
    public async Task RunAsync_OordeelBuitenSchema_TeltAlsUitval_ZonderWatermark(string body)
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");

        var svc = Service(db, () => body);
        var r = await svc.RunAsync();

        // MUTATIE-EIS (b): geen coulante lezing. Geen audit-rij, geen conflict,
        // en de uitval is zichtbaar geteld.
        Assert.Equal(0, r.Audited);
        Assert.Equal(1, r.Failed);
        Assert.Equal("onleesbaar antwoord×1", r.FailureDetail);
        Assert.Empty(await db.InteractionAudits.ToListAsync());
        Assert.Empty(await db.ReasoningConflicts.ToListAsync());

        // Geen watermark: dezelfde interactie komt de volgende run terug en wordt
        // dan wél geauditeerd.
        var up = Service(db, () => Verdict(correct: true, supported: true, "ok"));
        var r2 = await up.RunAsync();
        Assert.Equal(1, r2.Audited);
    }

    [Fact]
    public async Task RunAsync_RbAiUitval_GeenWatermark_InteractieKomtTerug()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:Deflect", "mechanic:Assault");

        var down = Service(db, () => null); // 500 → RbAiClient geeft null
        var r1 = await down.RunAsync();

        Assert.Equal(0, r1.Audited);
        Assert.Equal(1, r1.Failed);
        Assert.Empty(await db.InteractionAudits.ToListAsync());

        // rb-ai weer in de lucht: zelfde interactie wordt opnieuw aangeboden. Een
        // watermark op de uitval had haar permanent uit de steekproef gehaald.
        var up = Service(db, () => Verdict(correct: true, supported: true, "ok"));
        var r2 = await up.RunAsync();
        Assert.Equal(1, r2.Audited);
        Assert.Single(await db.InteractionAudits.ToListAsync());
    }

    // ── (c) 1-op-N-selectie met watermark ────────────────────────────────────

    [Fact]
    public async Task RunAsync_PaktNooitTweeKeerDezelfde_VoorDePoolRondIs()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:A", "mechanic:B");
        await SeedPromotedAsync(db, "mechanic:C", "mechanic:D");
        await SeedPromotedAsync(db, "mechanic:E", "mechanic:F");

        var svc = Service(db, () => Verdict(correct: true, supported: true, "ok"));

        // Cap 1 per run: drie runs verwerken drie VERSCHILLENDE interacties.
        var r1 = await svc.RunAsync(maxAudits: 1);
        Assert.Equal(1, r1.Audited);
        Assert.True(r1.CapHit);
        var r2 = await svc.RunAsync(maxAudits: 1);
        Assert.Equal(1, r2.Audited);
        var r3 = await svc.RunAsync(maxAudits: 1);
        Assert.Equal(1, r3.Audited);
        Assert.False(r3.CapHit);

        var audited = await db.InteractionAudits.Select(a => a.InteractionId).ToListAsync();
        Assert.Equal(3, audited.Count);
        Assert.Equal(3, audited.Distinct().Count()); // nooit twee keer dezelfde

        // Pool rond: de vierde run heeft niets meer te doen.
        var r4 = await svc.RunAsync(maxAudits: 1);
        Assert.Equal(0, r4.Audited);
        Assert.False(r4.CapHit);
        Assert.Equal(3, await db.InteractionAudits.CountAsync());
    }

    [Fact]
    public async Task RunAsync_EenOpN_SelecteertDeterministischEenDeelVanDePool()
    {
        using var db = NewDb();
        for (var i = 0; i < 4; i++)
            await SeedPromotedAsync(db, $"mechanic:A{i}", $"mechanic:B{i}");

        // Dichtheid 2: alleen de interacties met een even Id horen bij de
        // steekproef — deterministisch, dus stabiel over runs.
        var svc = Service(db, () => Verdict(correct: true, supported: true, "ok"), divisor: 2);
        var r = await svc.RunAsync();

        Assert.Equal(2, r.Audited);
        var ids = await db.InteractionAudits.Select(a => a.InteractionId).ToListAsync();
        Assert.All(ids, id => Assert.Equal(0, id % 2));

        // Een tweede run pakt niets nieuws: de rest van de pool hoort niet bij de
        // steekproef en de steekproef-leden dragen hun watermark.
        var r2 = await svc.RunAsync();
        Assert.Equal(0, r2.Audited);
        Assert.Equal(2, await db.InteractionAudits.CountAsync());
    }

    [Fact]
    public async Task RunAsync_AuditeertAlleenGepromoveerdeInteracties()
    {
        using var db = NewDb();
        db.Interactions.Add(new Interaction
        {
            AgentRef = "mechanic:A", PatientRef = "mechanic:B", Kind = "COUNTERS",
            Status = InteractionStatus.Candidate, CreatedByRunId = Ulid.NewUlid(),
        });
        db.Interactions.Add(new Interaction
        {
            AgentRef = "card:ogn-001", PatientRef = "card:ogn-002", Kind = "COUNTERS",
            Status = InteractionStatus.ModelHypothesizedUnruled, CreatedByRunId = Ulid.NewUlid(),
        });
        await db.SaveChangesAsync();

        var svc = Service(db, () => Verdict(correct: true, supported: true, "ok"));
        var r = await svc.RunAsync();

        Assert.Equal(0, r.Audited);
        Assert.Empty(await db.InteractionAudits.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_BeheerdeInstellingWint_OpHetGebruiksmoment()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:A", "mechanic:B"); // Id 1

        // Env-default 10 zou Id 1 overslaan (1 % 10 != 0); de beheerde override
        // "1" (#254, gelezen op het gebruiksmoment) haalt hem wél binnen.
        var settings = new ManagedSettingsService(
            seed: new Dictionary<string, string> { ["brein.audit.sample_n"] = "1" },
            auditBase: new BreinAuditSettings(10));
        var svc = new BreinInteractionAuditService(
            db, Ai(() => Verdict(correct: true, supported: true, "ok")), settings);

        var r = await svc.RunAsync();

        Assert.Equal(1, r.Audited);
    }

    [Fact]
    public async Task RunAsync_DeadlineVerstreken_StoptDirect_ZonderOordeel()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:A", "mechanic:B");

        var svc = Service(db, () => Verdict(correct: true, supported: true, "ok"));
        var r = await svc.RunAsync(deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(0, r.Audited);
        Assert.True(r.CapHit); // vers werk blijft liggen voor de volgende nacht
        Assert.Empty(await db.InteractionAudits.ToListAsync());
    }

    // ── Bewering + bewijs in de aanroep ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_StuurtClaimEnBewijsNaarHetAuditEndpoint()
    {
        using var db = NewDb();
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001", Name = "Alpha", Type = "Unit",
            TextPlain = "Deflect prevents Assault damage during a Showdown.",
            Mechanics = ["Deflect", "Assault"],
        });
        await db.SaveChangesAsync();
        var ix = await SeedPromotedAsync(db, "card:ogn-001", "mechanic:Assault", "MODIFIES");
        db.Assertions.Add(new Assertion
        {
            Id = Ulid.NewUlid(), Subject = $"interaction:{ix.Id}", FactKind = "interaction",
            MiningRunId = Ulid.NewUlid(), DerivedFromRef = "card:ogn-001",
        });
        await db.SaveChangesAsync();

        var bodies = new List<string>();
        var svc = new BreinInteractionAuditService(
            db, CapturingAi(bodies, () => Verdict(correct: true, supported: true, "ok")),
            Settings(1));

        await svc.RunAsync();

        var payload = Assert.Single(bodies);
        using var doc = JsonDocument.Parse(payload);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        // De bewering noemt beide rollen en het kind; het bewijs draagt de
        // kaarttekst waarop het feit gemined is.
        Assert.Contains("MODIFIES", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("mechanic:Assault", text);
        Assert.Contains("Deflect prevents Assault damage", text);
        // En het systeem-prompt is de audit-instructie, niet de extractie-prompt.
        var system = doc.RootElement.GetProperty("system").GetString()!;
        Assert.Contains("emit_audit_verdict", system);
        Assert.Equal("opus", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task RunAsync_LeestAuditmodelEenmaal_StuurtAliasEnBewaartWerkelijkeProvider()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:A", "mechanic:B");
        var bodies = new List<string>();
        var svc = new BreinInteractionAuditService(
            db,
            CapturingAi(bodies, () =>
                """{"verdicts":[{"correct":true,"supported_by_evidence":true,"motivation":"ok"}],"provider":"codex-sdk","model":"gpt-5.6-sol","usage":{"inputTokens":12,"outputTokens":3,"unit":"tokens"}}"""),
            Settings(1, "codex"));

        await svc.RunAsync();

        using var payload = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal("codex", payload.RootElement.GetProperty("model").GetString());
        var run = await db.MiningRuns.SingleAsync();
        Assert.Equal("codex", run.LlmModelAlias);
        Assert.Equal("codex-sdk", run.LlmProvider);
        Assert.Equal("gpt-5.6-sol", run.LlmModel);
        Assert.Equal("gpt-5.6-sol", (await db.InteractionAudits.SingleAsync()).Model);
    }

    [Fact]
    public async Task RunAsync_Samenvatting_BenoemtDeSteekproefEnHetModel()
    {
        using var db = NewDb();
        await SeedPromotedAsync(db, "mechanic:A", "mechanic:B");

        var svc = Service(db, () => Verdict(correct: true, supported: true, "ok"));
        var r = await svc.RunAsync();

        Assert.Contains("steekproef door claude-opus-4-8", r.Summary);
        Assert.Contains("1 bevestigd", r.Summary);
        Assert.Contains("0 betwist", r.Summary);
    }

    // ── Observability: gemeten precisie náást de poort-accept-ratio ──────────

    [Fact]
    public async Task Observability_GemetenPrecisie_LosVanDePoortRatio()
    {
        using var db = NewDb();
        // Vier audit-oordelen: drie gezond, één betwist → gemeten precisie 0,75.
        for (var i = 0; i < 4; i++)
            db.InteractionAudits.Add(new InteractionAudit
            {
                InteractionId = i + 1, RunId = Ulid.NewUlid(),
                Model = "claude-opus-4-8", PromptVersion = "interaction-audit-v1",
                Correct = i < 3, SupportedByEvidence = true,
            });
        // En een audit-RUN in het grootboek: die mag NIET als mining-precisie-rij
        // verschijnen — dan vermomt de meting zich alsnog als accept-ratio.
        db.MiningRuns.Add(new MiningRun
        {
            Id = Ulid.NewUlid(), Kind = "interaction_audit", LlmModel = "claude-opus-4-8",
            CompletedAt = DateTimeOffset.UtcNow, Candidates = 4, Verified = 3, Rejected = 1,
        });
        await db.SaveChangesAsync();

        var obs = await new BrainExplorerService(db).ObservabilityAsync();

        var row = Assert.Single(obs.Report.AuditPrecision);
        Assert.Equal("claude-opus-4-8", row.Model);
        Assert.Equal("interaction-audit-v1", row.PromptVersion);
        Assert.Equal(4, row.Audited);
        Assert.Equal(3, row.Sound);
        Assert.Equal(1, row.Incorrect);
        Assert.Equal(0, row.Unsupported);
        Assert.Equal(0.75, row.Precision, 10);

        Assert.DoesNotContain(obs.Report.MiningPrecision, r => r.Kind == "interaction_audit");
    }

    [Fact]
    public void AuditPrecision_SplitstOnjuistEnOngedragenUit()
    {
        var rows = ObservabilityRollups.AuditPrecision(
        [
            new InteractionAudit { InteractionId = 1, RunId = "r", Model = "claude-opus-4-8", PromptVersion = "interaction-audit-v1", Correct = true, SupportedByEvidence = true },
            new InteractionAudit { InteractionId = 2, RunId = "r", Model = "claude-opus-4-8", PromptVersion = "interaction-audit-v1", Correct = false, SupportedByEvidence = true },
            new InteractionAudit { InteractionId = 3, RunId = "r", Model = "claude-opus-4-8", PromptVersion = "interaction-audit-v1", Correct = true, SupportedByEvidence = false },
        ]);

        var row = Assert.Single(rows);
        Assert.Equal(3, row.Audited);
        Assert.Equal(1, row.Sound);
        Assert.Equal(1, row.Incorrect);
        Assert.Equal(1, row.Unsupported);
        // Gemeten precisie = correct-én-gedragen ÷ geauditeerd: 1/3.
        Assert.Equal(1.0 / 3.0, row.Precision, 10);
    }

    // ── Beheerde instelling: catalogus + parsing ─────────────────────────────

    [Theory]
    [InlineData("1", "1")]
    [InlineData("10", "10")]
    [InlineData("100", "100")]
    public void ParseValue_SampleN_AccepteertBinnenBereik(string raw, string expected)
    {
        var p = ManagedSettingsCatalog.ParseValue("brein.audit.sample_n", raw);
        Assert.True(p.Ok);
        Assert.Equal(expected, p.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    [InlineData("tien")]
    public void ParseValue_SampleN_WeigertBuitenBereik(string raw)
    {
        Assert.False(ManagedSettingsCatalog.ParseValue("brein.audit.sample_n", raw).Ok);
    }

    [Fact]
    public void BreinAuditSettings_DefaultIsTien_OverrideWint_OnzinValtTerug()
    {
        Assert.Equal(10, BreinAuditSettings.Default.SampleDivisor);
        var basis = new BreinAuditSettings(10);
        Assert.Equal(5, basis.WithOverrides(
            new Dictionary<string, string> { ["brein.audit.sample_n"] = "5" }).SampleDivisor);
        Assert.Equal(10, basis.WithOverrides(
            new Dictionary<string, string> { ["brein.audit.sample_n"] = "onzin" }).SampleDivisor);
        Assert.Equal(10, basis.WithOverrides(new Dictionary<string, string>()).SampleDivisor);
    }

    // ── Levenscyclus: de brein-reset neemt de oordelen mee ───────────────────

    [Fact]
    public async Task BreinReset_VerwijdertOokDeAuditOordelen()
    {
        using var db = NewDb();
        var ix = await SeedPromotedAsync(db, "mechanic:A", "mechanic:B");
        db.InteractionAudits.Add(new InteractionAudit
        {
            InteractionId = ix.Id, RunId = Ulid.NewUlid(), Model = "claude-opus-4-8",
            PromptVersion = "interaction-audit-v1", Correct = true, SupportedByEvidence = true,
        });
        await db.SaveChangesAsync();

        // Een oordeel over een verwijderd feit zou de gemeten precisie voor eeuwig
        // kleuren — en na her-mining als vals watermark werken.
        var r = await new BreinMiningResetService(db).ResetAsync(BreinResetScope.Interactions);

        Assert.Equal(1, r.Audits);
        Assert.Contains("1 audit-oordelen verwijderd", r.Message);
        Assert.Empty(await db.InteractionAudits.ToListAsync());
    }

    // ── Parser (tweede muur), los van de service ─────────────────────────────

    [Fact]
    public void ParseDetailed_GeldigOordeel_ParsetPrecisEen()
    {
        var parse = InteractionAuditExtraction.ParseDetailed(
            """{"verdicts":[{"correct":true,"supported_by_evidence":false,"motivation":"m"}]}""");
        Assert.False(parse.Malformed);
        var v = Assert.Single(parse.Items);
        Assert.True(v.Correct);
        Assert.False(v.SupportedByEvidence);
        Assert.Equal("m", v.Motivation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("geen json")]
    [InlineData("""{"verdicts":[{"correct":1,"supported_by_evidence":true}]}""")]   // getal ≠ boolean
    [InlineData("""{"verdicts":[{"supported_by_evidence":true}]}""")]                // veld ontbreekt
    public void ParseDetailed_BuitenHetSchema_IsMalformed(string? raw)
    {
        Assert.True(InteractionAuditExtraction.ParseDetailed(raw).Malformed);
    }

    // ── testinfra (zelfde snit als BreinInteractionMiningServiceTests) ───────

    private static BreinInteractionAuditService Service(
        RbRulesDbContext db, Func<string?> body, int divisor = 1) =>
        new(db, Ai(body), Settings(divisor));

    /// <summary>Instellingen zonder DB-laag: de audit-basis is de enige waarheid —
    /// divisor 1 (alles in de steekproef) tenzij de test anders vraagt.</summary>
    private static ManagedSettingsService Settings(
        int divisor, string modelAlias = BreinExtractModelAliases.Opus) =>
        new(auditBase: new BreinAuditSettings(divisor, modelAlias));

    private static string Verdict(bool correct, bool supported, string motivation) =>
        JsonSerializer.Serialize(new
        {
            verdicts = new[]
            {
                new { correct, supported_by_evidence = supported, motivation },
            },
        });

    private static async Task<Interaction> SeedPromotedAsync(
        RbRulesDbContext db, string agent, string patient, string kind = "COUNTERS")
    {
        var ix = new Interaction
        {
            AgentRef = agent, PatientRef = patient, Kind = kind,
            Status = InteractionStatus.Promoted,
            PromotedAt = DateTimeOffset.UtcNow.AddDays(-1),
            StatusReason = "schema-ok; bewijszin gevonden; LLM-verdict positief",
            CreatedByRunId = Ulid.NewUlid(),
        };
        db.Interactions.Add(ix);
        await db.SaveChangesAsync();
        return ix;
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
