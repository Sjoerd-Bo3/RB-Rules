using RbRules.Domain;

namespace RbRules.Tests;

public class QuestionRouterTests
{
    [Theory]
    [InlineData("Mag ik reageren tijdens een showdown?", false, QuestionType.Ruling)]
    [InlineData("Wat gebeurt er als een Hidden unit getarget wordt?", false, QuestionType.Ruling)]
    [InlineData("Wat is Deflect?", false, QuestionType.Definitie)]
    [InlineData("What does Accelerate mean?", false, QuestionType.Definitie)]
    [InlineData("Is Teemo - Swift Scout banned?", true, QuestionType.Legaliteit)]
    [InlineData("Mag ik 4x dezelfde kaart in mijn deck?", false, QuestionType.Legaliteit)]
    [InlineData("Hoeveel tijd is er per ronde in een toernooi?", false, QuestionType.Toernooi)]
    [InlineData("Wanneer mag een judge ingrijpen?", false, QuestionType.Toernooi)]
    [InlineData("Wat doet Teemo - Swift Scout?", true, QuestionType.Kaart)]
    [InlineData("Welke gearkillers zijn er?", false, QuestionType.Lijst)]
    [InlineData("Welke kaarten kunnen een gear vernietigen?", false, QuestionType.Lijst)]
    [InlineData("Which cards can destroy a gear?", false, QuestionType.Lijst)]
    [InlineData("Geef een overzicht van alle removal spells", false, QuestionType.Lijst)]
    [InlineData("Wat speelt iedereen in de meta?", false, QuestionType.Lijst)]
    public void Classify_RoutesByQuestionKind(string q, bool mentionsCard, QuestionType expected) =>
        Assert.Equal(expected, QuestionRouter.Classify(q, mentionsCard));

    [Fact]
    public void ListQuestion_AboutBans_StaysLegality()
    {
        // Volgorde-grens (#67): een lijstvraag over de banlijst hoort bij
        // Legaliteit — daar is de banlijst het gezaghebbende contextblok.
        Assert.Equal(QuestionType.Legaliteit,
            QuestionRouter.Classify("Welke kaarten zijn banned?"));
    }

    [Fact]
    public void Definition_WithCardName_FallsBackToRuling()
    {
        // "Wat is <kaartnaam>?" is een kaartvraag-grens: zonder 'wat doet'
        // valt hij terug op ruling — beter te streng dan verkeerd format.
        Assert.Equal(QuestionType.Ruling,
            QuestionRouter.Classify("Wat is er aan de hand met Teemo in combat?", mentionsCard: true));
    }

    [Fact]
    public void StructureFor_EveryType_HasContent()
    {
        foreach (var t in Enum.GetValues<QuestionType>())
            Assert.False(string.IsNullOrWhiteSpace(QuestionRouter.StructureFor(t)));
    }
}
