using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Unit-tests op de agentic-gate (#107, docs/BRAIN.md §2.4): alle
/// flag-standen, beide auto-triggers en het geen-escalatie-pad. De gate is
/// puur — dit is de volledige beslislogica, zonder I/O.</summary>
public class AgenticGateTests
{
    // ── ParseMode: default off, tikfouten vallen terug op off ─────────

    [Theory]
    [InlineData(null, AgenticMode.Off)]
    [InlineData("", AgenticMode.Off)]
    [InlineData("off", AgenticMode.Off)]
    [InlineData("auto", AgenticMode.Auto)]
    [InlineData("force", AgenticMode.Force)]
    [InlineData("AUTO", AgenticMode.Auto)]     // env-waarden zijn hoofdletterongevoelig
    [InlineData(" Force ", AgenticMode.Force)] // en whitespace-tolerant
    [InlineData("aan", AgenticMode.Off)]       // onbekende waarde → nooit stilzwijgend aan
    [InlineData("true", AgenticMode.Off)]
    public void ParseMode_ValtAltijdVeiligTerugOpOff(string? value, AgenticMode expected) =>
        Assert.Equal(expected, AgenticGate.ParseMode(value));

    // ── Off: nooit escaleren, ook niet als beide triggers vuren ───────

    [Theory]
    [InlineData(QuestionType.Ruling, 2, false)]
    [InlineData(QuestionType.Ruling, 5, true)]
    [InlineData(QuestionType.Definitie, 0, true)]
    public void Off_EscaleertNooit(QuestionType type, int mentions, bool emptyRetrieval) =>
        Assert.False(AgenticGate.ShouldEscalate(type, mentions, emptyRetrieval, AgenticMode.Off));

    // ── Force: altijd escaleren (bestaat alleen voor verificatie) ─────

    [Theory]
    [InlineData(QuestionType.Ruling, 0, false)]
    [InlineData(QuestionType.Definitie, 0, false)]
    [InlineData(QuestionType.Lijst, 3, true)]
    public void Force_EscaleertAltijd(QuestionType type, int mentions, bool emptyRetrieval) =>
        Assert.True(AgenticGate.ShouldEscalate(type, mentions, emptyRetrieval, AgenticMode.Force));

    // ── Auto, trigger (a): Ruling met ≥2 herkende kaartnamen ──────────

    [Fact]
    public void Auto_RulingMetTweeKaartnamen_Escaleert() =>
        Assert.True(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 2, emptyRetrieval: false, AgenticMode.Auto));

    [Fact]
    public void Auto_RulingMetMeerDanTweeKaartnamen_Escaleert() =>
        Assert.True(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 3, emptyRetrieval: false, AgenticMode.Auto));

    [Fact]
    public void Auto_RulingMetEenKaartnaam_EscaleertNiet() =>
        Assert.False(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 1, emptyRetrieval: false, AgenticMode.Auto));

    [Theory] // de kaartnamen-trigger geldt alléén voor Ruling (§2.4)
    [InlineData(QuestionType.Definitie)]
    [InlineData(QuestionType.Kaart)]
    [InlineData(QuestionType.Legaliteit)]
    [InlineData(QuestionType.Toernooi)]
    [InlineData(QuestionType.Lijst)]
    public void Auto_AnderTypeMetTweeKaartnamen_EscaleertNiet(QuestionType type) =>
        Assert.False(AgenticGate.ShouldEscalate(
            type, 2, emptyRetrieval: false, AgenticMode.Auto));

    // ── Auto, trigger (b): lege retrieval, ongeacht vraagtype ─────────

    [Theory]
    [InlineData(QuestionType.Ruling)]
    [InlineData(QuestionType.Definitie)]
    [InlineData(QuestionType.Lijst)]
    public void Auto_LegeRetrieval_EscaleertOngeachtType(QuestionType type) =>
        Assert.True(AgenticGate.ShouldEscalate(
            type, 0, emptyRetrieval: true, AgenticMode.Auto));

    // ── Auto, geen-escalatie-pad: gewone vraag blijft single-pass ─────

    [Fact]
    public void Auto_GewoneVraagZonderTriggers_EscaleertNiet() =>
        Assert.False(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 0, emptyRetrieval: false, AgenticMode.Auto));

    // ── Foto-vragen escaleren nooit (review #107): het Opus-visionpad
    //    is daar bewust, en de brein-tools zijn tekst-only ───────────────

    [Theory]
    [InlineData(AgenticMode.Force)] // ook force: verificatie mag vision niet downgraden
    [InlineData(AgenticMode.Auto)]
    public void FotoVraag_EscaleertNooit(AgenticMode mode) =>
        Assert.False(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 2, emptyRetrieval: true, mode, hasImage: true));

    [Fact]
    public void ZonderFoto_BlijftForceEscaleren() =>
        Assert.True(AgenticGate.ShouldEscalate(
            QuestionType.Ruling, 0, emptyRetrieval: false, AgenticMode.Force, hasImage: false));

    // ── Mention-telling (review #107): substring-matches dedupliceren ──
    //    "Jinx" matcht ook binnen "Jinx, Loose Cannon" — één genoemde
    //    kaart mag nooit als twee mentions tellen.

    [Fact]
    public void CountDistinctMentions_LeegBlijftNul() =>
        Assert.Equal(0, AgenticGate.CountDistinctMentions([]));

    [Fact]
    public void CountDistinctMentions_TweeEchteNamen_TeltTwee() =>
        Assert.Equal(2, AgenticGate.CountDistinctMentions(["Viktor", "Yasuo"]));

    [Fact]
    public void CountDistinctMentions_SubstringVanLangereNaam_TeltEen() =>
        Assert.Equal(1, AgenticGate.CountDistinctMentions(["Jinx", "Jinx, Loose Cannon"]));

    [Fact]
    public void CountDistinctMentions_SubstringDedupIsHoofdletterongevoelig() =>
        Assert.Equal(1, AgenticGate.CountDistinctMentions(["jinx", "JINX, Loose Cannon"]));

    [Fact]
    public void CountDistinctMentions_DubbeleNaam_TeltEen() =>
        Assert.Equal(1, AgenticGate.CountDistinctMentions(["Ahri", "ahri"]));

    [Fact]
    public void CountDistinctMentions_MengselTeltAlleenLangsteNamen() =>
        Assert.Equal(2, AgenticGate.CountDistinctMentions(
            ["Viktor", "Jinx", "Jinx, Loose Cannon"]));

    // ── ParseApproach (#153): default auto, tikfouten vallen op auto ───

    [Theory]
    [InlineData(null, AskApproach.Auto)]
    [InlineData("", AskApproach.Auto)]
    [InlineData("auto", AskApproach.Auto)]
    [InlineData("fast", AskApproach.Fast)]
    [InlineData("thorough", AskApproach.Thorough)]
    [InlineData("THOROUGH", AskApproach.Thorough)] // hoofdletterongevoelig
    [InlineData(" Fast ", AskApproach.Fast)]       // en whitespace-tolerant
    [InlineData("grondig", AskApproach.Auto)]      // onbekend → nooit stil forceren
    [InlineData("agentic", AskApproach.Auto)]
    public void ParseApproach_ValtAltijdVeiligTerugOpAuto(string? value, AskApproach expected) =>
        Assert.Equal(expected, AgenticGate.ParseApproach(value));

    // ── Decide (#153) — Fast: nooit escaleren, wie er ook wint ─────────

    [Theory] // ook onder force en met beide auto-triggers actief
    [InlineData(AgenticMode.Off)]
    [InlineData(AgenticMode.Auto)]
    [InlineData(AgenticMode.Force)]
    public void Decide_Fast_EscaleertNooit(AgenticMode mode)
    {
        var d = AgenticGate.Decide(
            AskApproach.Fast, mode, QuestionType.Ruling, cardMentions: 3,
            emptyRetrieval: true, hasImage: false, quotaAvailable: true);
        Assert.False(d.Escalate);
        Assert.Equal(AskDecider.User, d.DecidedBy);
        Assert.Null(d.FallbackReason);
        Assert.Equal("fast", AgenticGate.EffectiveApproach(d, AskApproach.Fast));
    }

    // ── Decide — Thorough gehonoreerd: flag aan, geen foto, quota over ─

    [Theory]
    [InlineData(AgenticMode.Auto)]
    [InlineData(AgenticMode.Force)]
    public void Decide_ThoroughMetQuota_EscaleertAlsGebruiker(AgenticMode mode)
    {
        // Bewust een vraag die de auto-gate NIET zou escaleren: de gebruiker
        // dwingt hem af.
        var d = AgenticGate.Decide(
            AskApproach.Thorough, mode, QuestionType.Definitie, cardMentions: 0,
            emptyRetrieval: false, hasImage: false, quotaAvailable: true);
        Assert.True(d.Escalate);
        Assert.Equal(AskDecider.User, d.DecidedBy);
        Assert.Null(d.FallbackReason);
        Assert.Equal("thorough", AgenticGate.EffectiveApproach(d, AskApproach.Thorough));
    }

    // ── Decide — Thorough onder flag off: Grondig bestaat niet ─────────

    [Fact]
    public void Decide_ThoroughOnderFlagOff_ValtTerugMetRedenDisabled()
    {
        var d = AgenticGate.Decide(
            AskApproach.Thorough, AgenticMode.Off, QuestionType.Ruling, cardMentions: 3,
            emptyRetrieval: true, hasImage: false, quotaAvailable: true);
        Assert.False(d.Escalate);
        Assert.Equal(AskDecider.Gate, d.DecidedBy);
        Assert.Equal(AgenticGate.ReasonDisabled, d.FallbackReason);
        Assert.Equal("auto", AgenticGate.EffectiveApproach(d, AskApproach.Thorough));
    }

    // ── Decide — Thorough met foto: het vision-pad wint (gate-regel) ───

    [Theory]
    [InlineData(AgenticMode.Auto)]
    [InlineData(AgenticMode.Force)]
    public void Decide_ThoroughMetFoto_BlijftOpHetVisionpad(AgenticMode mode)
    {
        var d = AgenticGate.Decide(
            AskApproach.Thorough, mode, QuestionType.Ruling, cardMentions: 2,
            emptyRetrieval: true, hasImage: true, quotaAvailable: true);
        Assert.False(d.Escalate);
        Assert.Equal(AskDecider.Gate, d.DecidedBy);
        Assert.Equal(AgenticGate.ReasonPhoto, d.FallbackReason);
        Assert.Equal("auto", AgenticGate.EffectiveApproach(d, AskApproach.Thorough));
    }

    // ── Decide — Thorough zonder quota: terugvallen op Auto mét reden ──

    [Fact]
    public void Decide_ThoroughZonderQuota_ValtTerugOpAutoZonderEscalatie()
    {
        var d = AgenticGate.Decide(
            AskApproach.Thorough, AgenticMode.Auto, QuestionType.Definitie, cardMentions: 0,
            emptyRetrieval: false, hasImage: false, quotaAvailable: false);
        Assert.False(d.Escalate);
        Assert.Equal(AskDecider.QuotaFallback, d.DecidedBy);
        Assert.Equal(AgenticGate.ReasonQuota, d.FallbackReason);
        Assert.Equal("auto", AgenticGate.EffectiveApproach(d, AskApproach.Thorough));
    }

    [Fact]
    public void Decide_ThoroughZonderQuota_MaarAutoTriggerVuurt_EscaleertAlsGate()
    {
        // De gate zou deze interactievraag zélf escaleren: dat blijft een
        // gate-escalatie (telt niet tegen het gebruikersquotum), maar de
        // niet-gehonoreerde keuze blijft als reden zichtbaar voor de UI.
        var d = AgenticGate.Decide(
            AskApproach.Thorough, AgenticMode.Auto, QuestionType.Ruling, cardMentions: 2,
            emptyRetrieval: false, hasImage: false, quotaAvailable: false);
        Assert.True(d.Escalate);
        Assert.Equal(AskDecider.Gate, d.DecidedBy);
        Assert.Equal(AgenticGate.ReasonQuota, d.FallbackReason);
        Assert.Equal("auto", AgenticGate.EffectiveApproach(d, AskApproach.Thorough));
    }

    // ── Decide — terugval-volgorde: flag boven foto boven quota ────────

    [Fact]
    public void Decide_TerugvalRedenen_FlagWintVanFotoWintVanQuota()
    {
        var flagOff = AgenticGate.Decide(
            AskApproach.Thorough, AgenticMode.Off, QuestionType.Ruling, 0,
            emptyRetrieval: false, hasImage: true, quotaAvailable: false);
        Assert.Equal(AgenticGate.ReasonDisabled, flagOff.FallbackReason);

        var foto = AgenticGate.Decide(
            AskApproach.Thorough, AgenticMode.Auto, QuestionType.Ruling, 0,
            emptyRetrieval: false, hasImage: true, quotaAvailable: false);
        Assert.Equal(AgenticGate.ReasonPhoto, foto.FallbackReason);
    }

    // ── Decide — Auto: exact het bestaande gate-gedrag, besluit = gate ─

    [Theory]
    [InlineData(AgenticMode.Off, QuestionType.Ruling, 3, true, false)]
    [InlineData(AgenticMode.Auto, QuestionType.Ruling, 2, false, true)]  // trigger (a)
    [InlineData(AgenticMode.Auto, QuestionType.Definitie, 0, true, true)] // trigger (b)
    [InlineData(AgenticMode.Auto, QuestionType.Ruling, 1, false, false)]
    [InlineData(AgenticMode.Force, QuestionType.Definitie, 0, false, true)]
    public void Decide_Auto_VolgtShouldEscalate(
        AgenticMode mode, QuestionType type, int mentions, bool emptyRetrieval, bool expected)
    {
        var d = AgenticGate.Decide(
            AskApproach.Auto, mode, type, mentions, emptyRetrieval,
            hasImage: false, quotaAvailable: true);
        Assert.Equal(expected, d.Escalate);
        Assert.Equal(AskDecider.Gate, d.DecidedBy);
        Assert.Null(d.FallbackReason);
        Assert.Equal("auto", AgenticGate.EffectiveApproach(d, AskApproach.Auto));
    }
}
