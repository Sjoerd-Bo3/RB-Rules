using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De deterministische definitie-poort (#250): welke regelsectie mag de
/// <c>Definition</c> van een canonieke keyword-entiteit vullen? Bewust smal — een
/// verzonnen of half-passende definitie is erger dan een lege hover.</summary>
public class KeywordDefinitionTests
{
    [Fact]
    public void Find_SectieDieMetDeTermOpent_IsDeDefinitie()
    {
        var texts = new[]
        {
            "Units may be assigned to a battlefield during the action phase.",
            "Deflect: prevent the next damage that would be dealt to this unit.",
        };

        Assert.Equal(
            "Deflect: prevent the next damage that would be dealt to this unit.",
            KeywordDefinition.Find("Deflect", texts));
    }

    [Fact]
    public void Find_TermMiddenInDeTekst_IsGeenDefinitie()
    {
        // Co-occurrence is geen definitie: alleen een sectie die de term INTRODUCEERT
        // telt mee. Anders wordt de eerste de beste procedure-zin een "definitie".
        var texts = new[] { "A unit with Deflect ignores the first damage instance." };

        Assert.Null(KeywordDefinition.Find("Deflect", texts));
    }

    [Fact]
    public void Find_LangerWoordMetDezelfdePrefix_TeltNiet()
    {
        // "Deflection …" mag niet als definitie van "Deflect" doorgaan.
        var texts = new[] { "Deflection is not a keyword in this game." };

        Assert.Null(KeywordDefinition.Find("Deflect", texts));
    }

    [Fact]
    public void Find_MeerdereKandidaten_KiestDeKortste_EnIsDeterministisch()
    {
        var texts = new[]
        {
            "Tank N — this unit takes N less damage, and the effect persists across the "
            + "entire showdown, including any additional combat steps that follow.",
            "Tank N: reduce incoming damage by N.",
        };

        var first = KeywordDefinition.Find("Tank", texts);
        var second = KeywordDefinition.Find("Tank", texts.Reverse());

        Assert.Equal("Tank N: reduce incoming damage by N.", first);
        Assert.Equal(first, second);   // volgorde-onafhankelijk ⇒ idempotent over runs
    }

    [Fact]
    public void Find_GeenBron_OfLeegLabel_GeeftNull()
    {
        Assert.Null(KeywordDefinition.Find("Deflect", []));
        Assert.Null(KeywordDefinition.Find("", ["Deflect: prevent damage."]));
        Assert.Null(KeywordDefinition.Find(null, ["Deflect: prevent damage."]));
    }

    [Fact]
    public void Find_KaalLabelZonderUitleg_IsGeenDefinitie()
    {
        Assert.Null(KeywordDefinition.Find("Deflect", ["Deflect"]));
    }

    [Fact]
    public void Find_LangeSectie_WordtOpEenWoordgrensAfgekapt()
    {
        var body = "Deflect: " + string.Join(" ", Enumerable.Repeat("damage", 200));

        var found = KeywordDefinition.Find("Deflect", [body]);

        Assert.NotNull(found);
        Assert.True(found!.Length <= KeywordDefinition.MaxLength + 1); // + ellipsis
        Assert.EndsWith("…", found);
        Assert.DoesNotContain("dam…", found);   // afgekapt op een woordgrens
    }
}
