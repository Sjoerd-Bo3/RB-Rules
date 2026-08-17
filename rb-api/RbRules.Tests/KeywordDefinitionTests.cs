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

    // ── #250-review: "opent met de term" ≠ "definieert de term" ─────────────────
    // De sectie-parser strippet de kop uit de body, dus chunks in een genummerd
    // regelboek beginnen routineus met een kale procedure-zin. Zonder marker-eis
    // passeerde elke zin die toevallig met het keyword begint — en omdat 'kortste
    // wint' geldt, versloeg zo'n korte procedure-zin de échte glossariumsectie.
    [Theory]
    [InlineData("Ready units can be exhausted to pay costs.", "Ready")]
    [InlineData("Tank counters are removed at the end of the showdown.", "Tank")]
    [InlineData("Action cards may be played during your turn.", "Action")]
    [InlineData("Deflect wordt aan het einde van de beurt verwijderd.", "Deflect")]
    public void Find_ProcedureZinDieMetDeTermOpent_IsGeenDefinitie(string text, string term) =>
        Assert.Null(KeywordDefinition.Find(term, [text]));

    [Fact]
    public void Find_KorteProcedureZin_VerslaatDeEchteDefinitieNiet()
    {
        // Precies het faalgeval: de procedure-zin is KORTER dan de definitie en won
        // daardoor de kortste-wint-regel.
        var texts = new[]
        {
            "Tank counters are removed at the end of the showdown.",
            "Tank N: reduce the incoming damage by N for the rest of the showdown.",
        };

        Assert.Equal(
            "Tank N: reduce the incoming damage by N for the rest of the showdown.",
            KeywordDefinition.Find("Tank", texts));
    }

    // ── #250-review: een LANGERE meerwoordsterm definieert de prefix niet ────────
    [Fact]
    public void Find_MeerwoordsTerm_DefinieertDeKorterePrefixNiet()
    {
        var texts = new[]
        {
            "Reaction Window: the period after a spell is played in which opponents "
            + "may respond.",
        };

        // De woordgrens (een spatie) liet dit door: mechanic:Reaction kreeg de
        // definitie van een ánder begrip, en die tekst ging als bewijs de
        // predicaat-mining in.
        Assert.Null(KeywordDefinition.Find("Reaction", texts));
        Assert.StartsWith("Reaction Window:", KeywordDefinition.Find("Reaction Window", texts));
    }

    [Theory]
    [InlineData("Deflect is a keyword that prevents the next damage.")]
    [InlineData("Deflect — prevent the next damage.")]
    [InlineData("Deflect: prevent the next damage.")]
    [InlineData("Deflect 2: prevent the next two damage.")]
    [InlineData("Deflect N — prevent the next N damage.")]
    public void Find_GlossariumVormen_TellenWel(string text) =>
        Assert.NotNull(KeywordDefinition.Find("Deflect", [text]));

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
