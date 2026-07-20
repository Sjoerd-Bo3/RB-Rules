using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Woordgrens-bewuste term-matching (#249-review). De kale substring-match
/// was verdedigbaar op korte kaartteksten, maar sinds #249 is het HELE regelcorpus
/// bewijsbron én ankertoets voor de promotie-poort — en de helft van het
/// Riftbound-keywordvocabulaire bestaat uit gewone Engelse woorden.</summary>
public class TermMatchTests
{
    [Theory]
    [InlineData("Deflection is not a keyword in this game.", "Deflect")]
    [InlineData("A unit that keeps tanking damage stays engaged.", "Tank")]
    [InlineData("Equipment may not be moved.", "Equip")]
    [InlineData("Players may not look at hidden zones.", "Hide")]
    public void ContainsWord_TermAlleenAlsWoorddeel_IsGeenTreffer(string text, string term) =>
        Assert.False(TermMatch.ContainsWord(text, term));

    [Theory]
    [InlineData("[Assault 2] resolves first.", "Assault")]          // gebracket
    [InlineData("Tank reduces incoming damage.", "Tank")]           // kaal
    [InlineData("This unit gains Tank.", "Tank")]                   // punt erachter
    [InlineData("Deflect: prevent the next damage.", "Deflect")]    // dubbele punt
    [InlineData("Resolve during the Reaction Window, then continue.", "Reaction Window")]
    [InlineData("tank reduces damage", "Tank")]                     // hoofdletter-ongevoelig
    public void ContainsWord_HeleWoordvormen_ZijnEenTreffer(string text, string term) =>
        Assert.True(TermMatch.ContainsWord(text, term));

    [Fact]
    public void ContainsWord_ZoektDoorNaEenWoorddeel_TotDeEchteTreffer()
    {
        // De eerste voorkomst zit ín "Deflection"; de tweede is het hele woord. Een
        // naïeve implementatie die na de eerste IndexOf stopt, mist die.
        Assert.True(TermMatch.ContainsWord(
            "Deflection is slang; Deflect prevents the next damage.", "Deflect"));
    }

    [Fact]
    public void ContainsWord_LegeInvoer_IsGeenTreffer()
    {
        Assert.False(TermMatch.ContainsWord("Tank reduces damage.", ""));
        Assert.False(TermMatch.ContainsWord("Tank reduces damage.", null));
        Assert.False(TermMatch.ContainsWord("", "Tank"));
        Assert.False(TermMatch.ContainsWord(null, "Tank"));
    }
}
