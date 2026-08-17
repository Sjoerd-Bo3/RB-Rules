using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Deck-integratie (#15/#231, spec §7): CO_OCCURS kruis-valideert
/// structureel voorspelde combo-paden (fase 5) met de echte Piltover-meta. Kern: een
/// paar dat samen gespeeld wordt corroboreert (lift &gt; 1); een structureel
/// voorspeld paar dat niemand samen speelt niet; varianten die naar dezelfde canonical
/// resolven dubbeltellen niet.</summary>
public class DeckCoOccurrenceTests
{
    private static IReadOnlyList<IReadOnlyCollection<string>> Decks(params string[][] decks) =>
        [.. decks.Select(d => (IReadOnlyCollection<string>)d)];

    [Fact]
    public void SamenGespeeldPaar_Corroboreert_MetLiftBovenEen()
    {
        // A en B zitten samen in 2 van 3 decks; los komen ze niet extra voor.
        var decks = Decks(["A", "B"], ["A", "B"], ["C"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "B")]);

        var stat = Assert.Single(report.Stats);
        Assert.Equal(2, stat.CoDecks);
        Assert.True(stat.Corroborated);
        Assert.Equal(2.0 / 3, stat.Support, 6);
        Assert.True(stat.Lift > 1.0);
        Assert.Equal(1, report.CorroboratedPairs);
        Assert.Equal(1.0, report.CorroborationRate, 6);
    }

    [Fact]
    public void NooitSamenGespeeld_Corroboreert_Niet()
    {
        var decks = Decks(["A", "C"], ["B", "D"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "B")]);

        var stat = Assert.Single(report.Stats);
        Assert.Equal(0, stat.CoDecks);
        Assert.False(stat.Corroborated);
        Assert.Equal(0.0, stat.Lift, 6);
        Assert.Equal(0.0, report.CorroborationRate, 6);
    }

    [Fact]
    public void OnbekendeKaart_GeeftNulSignaal_GeenCrash()
    {
        var decks = Decks(["A", "B"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "ONBEKEND")]);

        var stat = Assert.Single(report.Stats);
        Assert.Equal(0, stat.CoDecks);
        Assert.Equal(1, stat.DecksWithA);
        Assert.Equal(0, stat.DecksWithB);
        Assert.False(stat.Corroborated);
    }

    [Fact]
    public void DubbeleKaartregel_TeltPerDeckEenmaal()
    {
        // Zelfde canonical twee keer in één deck (variant + basis) mag niet dubbeltellen.
        var decks = Decks(["A", "A", "B"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "B")]);

        var stat = Assert.Single(report.Stats);
        Assert.Equal(1, stat.DecksWithA);
        Assert.Equal(1, stat.CoDecks);
    }

    [Fact]
    public void VoorspeldeParen_WordenGededupeerdOpOngeordendeSleutel()
    {
        var decks = Decks(["A", "B"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "B"), ("B", "A")]);
        Assert.Single(report.Stats);        // (A,B) en (B,A) zijn één paar
        Assert.Equal(1, report.PredictedPairs);
    }

    [Fact]
    public void ZelfPaar_WordtOvergeslagen()
    {
        var decks = Decks(["A"]);
        var report = DeckCoOccurrence.Measure(decks, [("A", "A")]);
        Assert.Empty(report.Stats);
    }

    [Fact]
    public void SorteertOpSterksteSynergie()
    {
        // Paar (A,B) altijd samen; (C,D) toevallig samen in de helft → hogere lift bovenaan.
        var decks = Decks(
            ["A", "B", "C"], ["A", "B", "D"], ["A", "B", "C", "D"], ["C"], ["D"]);
        var report = DeckCoOccurrence.Measure(decks, [("C", "D"), ("A", "B")]);

        Assert.Equal("A|B", report.Stats[0].PairKey);
        Assert.True(report.Stats[0].Lift >= report.Stats[1].Lift);
    }
}
