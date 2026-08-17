using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Anker-correctie in een beheerder-opmerking (#184): puur/regex-
/// based, geen I/O. CorrectionReevaluationServiceTests dekt hoe dit anker
/// vervolgens de her-evaluatie beïnvloedt.</summary>
public class ReviewNoteAnchorTests
{
    [Theory]
    [InlineData("mechanic:Recall", "mechanic", "Recall")]
    [InlineData("section:402.3", "section", "402.3")]
    [InlineData("concept: turn structure", "concept", "turn structure")]
    [InlineData("MECHANIC:Recall", "mechanic", "Recall")] // case-insensitief
    [InlineData("mechanic:Recall.", "mechanic", "Recall")] // afsluitende punt weg
    public void TryParse_GeldigeAnkerregel_Herkend(string note, string expectedType, string expectedRef)
    {
        var r = ReviewNoteAnchor.TryParse(note);

        Assert.NotNull(r);
        Assert.Equal(expectedType, r.Value.TopicType);
        Assert.Equal(expectedRef, r.Value.TopicRef);
    }

    [Fact]
    public void TryParse_AnkerMetKommaInOnderwerp_VolledigeRefBewaard()
    {
        // Kaartnamen bevatten vaak een komma ("Taric, the Shield of Valoran")
        // — de ankerregel neemt de hele rest van de regel als onderwerp.
        var r = ReviewNoteAnchor.TryParse("card:Taric, the Shield of Valoran");

        Assert.NotNull(r);
        Assert.Equal("card", r.Value.TopicType);
        Assert.Equal("Taric, the Shield of Valoran", r.Value.TopicRef);
    }

    [Fact]
    public void TryParse_AnkerregelNaToelichting_OpEigenRegel_Herkend()
    {
        var note = "Dit is niet Retrieve maar iets anders.\nmechanic:Recall";

        var r = ReviewNoteAnchor.TryParse(note);

        Assert.NotNull(r);
        Assert.Equal("mechanic", r.Value.TopicType);
        Assert.Equal("Recall", r.Value.TopicRef);
    }

    [Fact]
    public void TryParse_MeerdereAnkerregels_LaatsteWint()
    {
        var note = "mechanic:Retrieve\nmechanic:Recall";

        var r = ReviewNoteAnchor.TryParse(note);

        Assert.Equal("Recall", r!.Value.TopicRef);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gewone opmerking zonder anker")]
    [InlineData("onbekend:Recall")] // niet in card|mechanic|section|concept
    public void TryParse_GeenAnkerregel_GeeftNull(string? note)
    {
        Assert.Null(ReviewNoteAnchor.TryParse(note));
    }
}
