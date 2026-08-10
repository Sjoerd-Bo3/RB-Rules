using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De pure kant van de §-verwijzings-bijlading (#364): welke vormen
/// tellen als sectie-verwijzing, en welke genoemde codes er écht bijgeladen
/// worden (bestaat-in-index, subcode-terugval, niet dubbel, cap).</summary>
public class SectionReferenceParserTests
{
    // --- ExtractCodes: de herkende vormen --------------------------------

    [Theory]
    [InlineData("de volledige tekst van §811 is niet meegeleverd", "811")]
    [InlineData("volgens § 355.6 mag je geen target kiezen", "355.6")]
    [InlineData("see section 421 for Hide", "421")]
    [InlineData("see Sections 421 for details", "421")]
    [InlineData("een unit blijft Hidden (§811.1) tot ze aanvalt", "811.1")]
    [InlineData("zie sectie 355.6 hiervoor", "355.6")]
    public void ExtractCodes_HerkentDeVormen(string text, string expected) =>
        Assert.Equal([expected], SectionReferenceParser.ExtractCodes(text));

    [Fact]
    public void ExtractCodes_NormaliseertLetterSubcodes() =>
        // Zelfde normalisatie als de index-kant (RuleSectionParser):
        // "466.2c" en "466.2.c" zijn dezelfde sectie.
        Assert.Equal(["466.2.c"], SectionReferenceParser.ExtractCodes("zie §466.2c"));

    [Fact]
    public void ExtractCodes_DedupeertInVolgordeVanEersteVermelding() =>
        Assert.Equal(["811", "421", "355.6"], SectionReferenceParser.ExtractCodes(
            "eerst §811, dan section 421, dan § 811 nogmaals en § 355.6"));

    [Theory]
    [InlineData("Betaal [1] en exhaust deze unit")]            // kaartkosten
    [InlineData("kost :rb_energy: 3 en heeft 2 might")]        // energie/stats
    [InlineData("[Assault 2] geeft een bonus bij de aanval")]  // keyword-magnitude
    [InlineData("regel 811 zonder teken telt niet")]           // kaal nummer
    [InlineData("subsection 421 is geen section-verwijzing")]  // woordgrens
    [InlineData("§421ab is geen sectiecode")]                  // lookahead-guard
    public void ExtractCodes_GeenValsPositieven(string text) =>
        Assert.Empty(SectionReferenceParser.ExtractCodes(text));

    // --- SelectForBackfill: wat er echt bijgeladen wordt -----------------

    private static readonly HashSet<string> GeenContext = [];

    private static HashSet<string> Set(params string[] codes) => [.. codes];

    [Fact]
    public void SelectForBackfill_OntbrekendeBestaandeCode_WordtGeselecteerd() =>
        Assert.Equal(["811"], SectionReferenceParser.SelectForBackfill(
            ["811"], GeenContext, existingInIndex: Set("811", "421")));

    [Fact]
    public void SelectForBackfill_AlInContext_NietDubbel() =>
        Assert.Empty(SectionReferenceParser.SelectForBackfill(
            ["811"], presentInContext: Set("811"), existingInIndex: Set("811")));

    [Fact]
    public void SelectForBackfill_OnbestaandeCode_Overgeslagen() =>
        Assert.Empty(SectionReferenceParser.SelectForBackfill(
            ["999"], GeenContext, existingInIndex: Set("811")));

    [Fact]
    public void SelectForBackfill_SubcodeValtTerugOpDiepsteBestaandeOuder() =>
        // "466.2.c" bestaat niet; "466.2" wél — die wint van "466".
        Assert.Equal(["466.2"], SectionReferenceParser.SelectForBackfill(
            ["466.2.c"], GeenContext, existingInIndex: Set("466", "466.2")));

    [Fact]
    public void SelectForBackfill_TerugvalOpAlAanwezigeOuder_LevertNiets() =>
        // "811.1" bestaat niet en de ouder "811" zit al in de context.
        Assert.Empty(SectionReferenceParser.SelectForBackfill(
            ["811.1"], presentInContext: Set("811"), existingInIndex: Set("811")));

    [Fact]
    public void SelectForBackfill_TweeVerwijzingenNaarZelfdeOuder_EenmaalGeladen() =>
        Assert.Equal(["811"], SectionReferenceParser.SelectForBackfill(
            ["811.1", "811.2"], GeenContext, existingInIndex: Set("811")));

    [Fact]
    public void SelectForBackfill_CapGehandhaafd()
    {
        // Acht bestaande, ontbrekende codes — méér dan de cap, anders kan
        // deze test de grens niet eens raken (#286-les: een fixture die de
        // grens niet kán overschrijden bewaakt niets).
        var mentioned = new[] { "801", "802", "803", "804", "805", "806", "807", "808" };
        Assert.Equal(8, mentioned.Length);

        var selected = SectionReferenceParser.SelectForBackfill(
            mentioned, GeenContext, existingInIndex: Set(mentioned));

        // Uitgeschreven literal, bewust niet de constante zelf (#286: een
        // assertie tegen de constante die ze bewaakt schuift mee).
        Assert.Equal(6, selected.Count);
        Assert.Equal(["801", "802", "803", "804", "805", "806"], selected);
    }

    [Fact]
    public void MaxBackfillSections_IsZes() =>
        Assert.Equal(6, SectionReferenceParser.MaxBackfillSections);
}
