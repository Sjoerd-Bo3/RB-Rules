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
}
