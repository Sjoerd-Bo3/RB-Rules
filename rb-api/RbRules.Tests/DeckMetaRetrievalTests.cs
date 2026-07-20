using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Poort en prompt-opbouw van het deck-meta-kanaal (kennislaag 3,
/// #267): de hotpath-poort laat alléén kaart-/lijstvragen mét herkende
/// kaartnaam door, en het blok is expliciet gelabeld als de zwakste laag —
/// community-metagegevens, geen officiële regel (docs/KNOWLEDGE.md).</summary>
public class DeckMetaRetrievalTests
{
    // ── De hotpath-poort ────────────────────────────────────────────────

    [Theory]
    [InlineData(QuestionType.Kaart, true, true)]
    [InlineData(QuestionType.Lijst, true, true)] // Lijst dekt ook meta-vragen
    [InlineData(QuestionType.Ruling, true, false)] // normatief: meta hoort er niet bij
    [InlineData(QuestionType.Legaliteit, true, false)] // banlijst is gezaghebbend, meta zou vervuilen
    [InlineData(QuestionType.Toernooi, true, false)]
    [InlineData(QuestionType.Definitie, true, false)]
    public void ShouldRetrieve_TypePoort(QuestionType type, bool mentionsCard, bool expected) =>
        Assert.Equal(expected, DeckMetaRetrieval.ShouldRetrieve(type, mentionsCard));

    [Theory]
    [InlineData(QuestionType.Kaart)]
    [InlineData(QuestionType.Lijst)]
    [InlineData(QuestionType.Ruling)]
    public void ShouldRetrieve_ZonderHerkendeKaart_NooitWaar(QuestionType type) =>
        // Zonder kaart-ref valt er niets op te halen: het signaal is per
        // kaart berekend (archetype-detectie is expliciet buiten scope).
        Assert.False(DeckMetaRetrieval.ShouldRetrieve(type, mentionsCard: false));

    // ── Het prompt-blok ─────────────────────────────────────────────────

    private static RetrievedDeckMeta Meta(
        bool thin = false, double? avg = 2.4, DeckMetaCoPlay[]? coPlayed = null) => new(
        "Jinx, Loose Cannon", DeckCount: 200, RecentDeckCount: 500, Percentage: 40.0,
        AverageCopies: avg, ThinData: thin,
        coPlayed ?? [new DeckMetaCoPlay("Powder Monkey", 88)]);

    [Fact]
    public void PromptBlock_LeegZonderItems() =>
        Assert.Equal("", DeckMetaRetrieval.PromptBlock([]));

    [Fact]
    public void PromptBlock_ExpliciteLaag3LabelingEnOmgangsregels()
    {
        var block = DeckMetaRetrieval.PromptBlock([Meta()]);

        // Hard principe (docs/KNOWLEDGE.md): expliciet gelabeld als laag 3 én
        // als de zwakste laag — community-metagegevens, geen officiële regel.
        Assert.Contains("DECK-META (kennislaag 3", block);
        Assert.Contains("zwakste laag", block);
        Assert.Contains("community-metagegevens", block);
        Assert.Contains("géén officiële regel, ruling of kaarttekst", block);
        // Omgangsregels: meta kleurt context, draagt nooit een oordeel.
        Assert.Contains("mag hier nooit op steunen", block);
        // En het per-regel-label naast het blok-label.
        Assert.Contains("- [deck-meta] Jinx, Loose Cannon:", block);
    }

    [Fact]
    public void Line_BovenDrempel_PercentageMetNoemerEnCoOccurrence()
    {
        var line = DeckMetaRetrieval.Line(Meta());

        // Percentage nooit los van zijn noemer (#15), invariant genoteerd.
        Assert.Contains("gespeeld in 40% van de 500 recentste decks", line);
        Assert.Contains("gemiddeld 2.4 exemplaren", line);
        Assert.Contains("vaak samen met: Powder Monkey (88 decks)", line);
    }

    [Fact]
    public void Line_ThinData_AbsoluteAantallenInPlaatsVanPercentage()
    {
        var line = DeckMetaRetrieval.Line(Meta(thin: true));

        // Onder de drempel is een percentage-claim misleidend (#15): dan
        // absolute aantallen plus een expliciete dun-sample-markering.
        Assert.Contains("gespeeld in 200 van de 500 recentste decks", line);
        Assert.Contains("dun sample", line);
        Assert.DoesNotContain("%", line);
    }

    [Fact]
    public void Line_ZonderGemiddeldeEnCoOccurrence_LaatBeideWeg()
    {
        var line = DeckMetaRetrieval.Line(Meta(avg: null, coPlayed: []));

        Assert.DoesNotContain("gemiddeld", line);
        Assert.DoesNotContain("vaak samen met", line);
    }

    [Fact]
    public void Line_EnkelvoudMeervoud_Decks()
    {
        var line = DeckMetaRetrieval.Line(Meta(coPlayed:
            [new DeckMetaCoPlay("Powder Monkey", 1), new DeckMetaCoPlay("Get Excited!", 2)]));

        Assert.Contains("Powder Monkey (1 deck)", line);
        Assert.Contains("Get Excited! (2 decks)", line);
    }
}
