using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase 4 (#228): de β(q)-router (OMD-GraphRAG) en de modus-selector achter
/// de vraag-router (§4-tabel). Puur en volledig getest.</summary>
public class GraphRagRouterTests
{
    // ── β(q): entity-dicht → graph-kanaal, abstract → community-kanaal ──

    [Fact]
    public void Beta_EntityDichteVraag_LeidtNaarGraphKanaal()
    {
        // "Werkt Empowered samen met Might in een showdown?" — 3 ankers, geen
        // abstractie-cues.
        var signals = QuestionSignals.From(linkedEntities: 3, contentWords: 7, abstractionCues: 0);
        var beta = BetaRouter.Beta(signals);
        Assert.True(beta > 0.5);
        Assert.True(BetaRouter.GraphChannelLeads(beta));
    }

    [Fact]
    public void Beta_AbstracteVraag_LeidtNaarCommunityKanaal()
    {
        // "Geef een overzicht van alle timing-windows" — geen anker, 2 abstractie-cues.
        var signals = QuestionSignals.From(linkedEntities: 0, contentWords: 7, abstractionCues: 2);
        var beta = BetaRouter.Beta(signals);
        Assert.True(beta < 0.5);
        Assert.False(BetaRouter.GraphChannelLeads(beta));
    }

    [Fact]
    public void Beta_Blend_MengtBeideKanalen()
    {
        Assert.Equal(0.8, BetaRouter.Blend(1.0, 0.8, 0.2), 6); // β=1 → puur graph
        Assert.Equal(0.2, BetaRouter.Blend(0.0, 0.8, 0.2), 6); // β=0 → puur community
        Assert.Equal(0.5, BetaRouter.Blend(0.5, 0.8, 0.2), 6); // gemengd
    }

    [Fact]
    public void QuestionSignals_GeenAnker_IsInherentAbstract()
    {
        var signals = QuestionSignals.From(linkedEntities: 0, contentWords: 5, abstractionCues: 0);
        Assert.Equal(0, signals.EntityDensity);
        Assert.True(signals.Abstraction >= 0.5);
    }

    // ── Modus-selectie (§4-tabel) ──

    [Fact]
    public void Mode_Definitie_LocalK1_GeenDrift()
    {
        var m = ModeSelector.Select(QuestionType.Definitie, "Wat betekent Exhaust?", linkedEntities: 1);
        Assert.Equal(RetrievalMode.Local, m.Primary);
        Assert.Equal(1, m.KHops);
        Assert.False(m.UseDrift);
    }

    [Fact]
    public void Mode_BanVraag_DirecteLookup_GeenGraaf()
    {
        var m = ModeSelector.Select(QuestionType.Legaliteit, "Is kaart X gebanned?", linkedEntities: 1);
        Assert.Equal(RetrievalMode.Direct, m.Primary);
    }

    [Fact]
    public void Mode_Interactie_DriftPlusPath()
    {
        var m = ModeSelector.Select(QuestionType.Ruling,
            "Werkt Empowered samen met Might in een showdown?", linkedEntities: 3);
        Assert.Equal(RetrievalMode.Drift, m.Primary);
        Assert.True(m.UsePath);
        Assert.Equal(2, m.KHops);
    }

    [Fact]
    public void Mode_CausaleWaaromVraag_PathFirst()
    {
        var m = ModeSelector.Select(QuestionType.Ruling,
            "Waarom verliest A de showdown van B?", linkedEntities: 2);
        Assert.Equal(RetrievalMode.Path, m.Primary);
        Assert.True(m.UsePath);
    }

    [Fact]
    public void Mode_OverzichtsVraag_Global()
    {
        var m = ModeSelector.Select(QuestionType.Lijst,
            "Geef een overzicht van alle timing-windows", linkedEntities: 0);
        Assert.Equal(RetrievalMode.Global, m.Primary);
    }

    [Fact]
    public void Mode_MisvattingCheck_MisvattingenKanaal()
    {
        var m = ModeSelector.Select(QuestionType.Ruling,
            "Klopt het dat je in een showdown altijd eerst mag blocken?", linkedEntities: 1);
        Assert.True(m.UseMisconceptionChannel);
    }

    [Fact]
    public void Mode_ToLocalOnly_ZetDureKanalenUit()
    {
        var m = ModeSelector.Select(QuestionType.Ruling,
            "Werkt A samen met B?", linkedEntities: 2).ToLocalOnly("test");
        Assert.Equal(RetrievalMode.Local, m.Primary);
        Assert.False(m.UsePath);
        Assert.False(m.UseDrift);
    }

    // ── Cue-detectie ──

    [Theory]
    [InlineData("Geef een overzicht van alle keywords", true)]
    [InlineData("Wat doet deze kaart?", false)]
    public void Cues_Abstractie(string q, bool expected) => Assert.Equal(expected, RetrievalCues.IsAbstract(q));

    [Theory]
    [InlineData("Waarom verliest A?", true)]
    [InlineData("Wat betekent Exhaust?", false)]
    public void Cues_Causaal(string q, bool expected) => Assert.Equal(expected, RetrievalCues.IsCausal(q));
}
