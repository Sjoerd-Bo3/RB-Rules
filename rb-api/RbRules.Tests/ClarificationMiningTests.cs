using RbRules.Domain;

namespace RbRules.Tests;

public class ClarificationSourcesTests
{
    [Theory]
    [InlineData("playriftbound-com-unleashed-rules-faq-and-clarifications", "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/", "Unleashed Rules FAQ and Clarifications")]
    [InlineData("some-id", "https://example.com/whatever", "Rules Clarification: Legion")]
    public void IsMatch_FaqOrClarificationSignal_ReturnsTrue(string id, string url, string name) =>
        Assert.True(ClarificationSources.IsMatch(id, url, name));

    [Theory]
    [InlineData("playriftbound-com-core-rules", "https://playriftbound.com/core-rules.pdf", "Core Rules")]
    [InlineData("errata-ogn", "https://playriftbound.com/en-us/news/rules-and-releases/ogn-errata/", "OGN Errata")]
    // #185: patch notes zijn UIT de clarify-mining-heuristiek gehaald — een
    // patch-notes-bron matcht niet meer voor IsMatch, ook al matchte hij vóór
    // deze scheiding nog wél mee (zie IsPatchNotesSignal_… hieronder).
    [InlineData("some-id", "https://playriftbound.com/en-us/news/rules-and-releases/some-patch-notes/", "Some Patch Notes")]
    [InlineData("core-rules-patch-notes", "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/", "Core Rules Patch Notes (officieel)")]
    public void IsMatch_RegularSource_ReturnsFalse(string id, string url, string name) =>
        Assert.False(ClarificationSources.IsMatch(id, url, name));

    [Fact]
    public void IsMatch_CaseInsensitive() =>
        Assert.True(ClarificationSources.IsMatch("id", "https://example.com/UNLEASHED-FAQ", null));

    // #185: IsPatchNotesSignal is het spiegelbeeld van IsMatch — het
    // predicaat dat de opruimstap gebruikt om oude, vóór deze scheiding
    // gemínede patch-notes-Corrections terug te vinden.
    [Theory]
    [InlineData("core-rules-patch-notes", "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/", "Core Rules Patch Notes (officieel)")]
    [InlineData("unleashed-patch-notes", "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-unleashed-patch-notes/", "Unleashed Patch Notes (officieel)")]
    [InlineData("some-id", "https://example.com/whatever", "Some Patch Notes")]
    public void IsPatchNotesSignal_PatchNotesBron_ReturnsTrue(string id, string url, string name) =>
        Assert.True(ClarificationSources.IsPatchNotesSignal(id, url, name));

    [Theory]
    [InlineData("playriftbound-com-unleashed-rules-faq-and-clarifications", "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-rules-faq-and-clarifications/", "Unleashed Rules FAQ and Clarifications")]
    [InlineData("playriftbound-com-core-rules", "https://playriftbound.com/core-rules.pdf", "Core Rules")]
    public void IsPatchNotesSignal_NietPatchNotesBron_ReturnsFalse(string id, string url, string name) =>
        Assert.False(ClarificationSources.IsPatchNotesSignal(id, url, name));
}

public class ClarificationGroundingTests
{
    private const string Content =
        "In deze FAQ verduidelijken we mechanieken. Legion means you finalize an "
        + "item on the chain. Reflection tokens tellen niet mee voor het handlimiet.";

    [Fact]
    public void IsGrounded_QuoteInSource_True() =>
        Assert.True(ClarificationGrounding.IsGrounded(
            "Legion means you finalize an item on the chain", Content));

    [Fact]
    public void IsGrounded_WhitespaceAndCaseTolerant() =>
        // Extra witruimte en andere casing mogen een echt citaat niet laten mislukken.
        Assert.True(ClarificationGrounding.IsGrounded(
            "  LEGION means   you finalize an item  on the chain ", Content));

    [Fact]
    public void IsGrounded_CurlyQuotesAndDashNormalized()
    {
        var content = "De regel luidt: “finalize an item”—op de chain.";
        Assert.True(ClarificationGrounding.IsGrounded("\"finalize an item\"-op de chain", content));
    }

    [Fact]
    public void IsGrounded_FabricatedQuote_False() =>
        // De kernzorg: een verzonnen citaat dat niet in de bron staat wordt geweigerd.
        Assert.False(ClarificationGrounding.IsGrounded(
            "Legion lets you draw three extra cards", Content));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsGrounded_MissingQuote_False(string? quote) =>
        Assert.False(ClarificationGrounding.IsGrounded(quote, Content));

    [Fact]
    public void IsGrounded_EmptyContent_False() =>
        Assert.False(ClarificationGrounding.IsGrounded("iets", ""));
}

public class ClarificationMinerTests
{
    // Realistische fixture (#177): één multi-concept-alinea zoals de echte
    // Unleashed Rules FAQ (Reflection tokens + Arcane Shift + [C]-symbool +
    // Legion door elkaar in dezelfde ~2200-tekens-slab) — de parser moet
    // hier meerdere DISCRETE items uit halen, elk met zijn eigen onderwerp.
    private const string LegionAndOthersAnswer = """
        Hier zijn de concepten:
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion",
           "clarification": "Legion verwijst naar het moment waarop je een item op de chain finalizet — de kaart wordt pas dan daadwerkelijk gespeeld.",
           "sectionRef": "402.3",
           "quote": "Legion means you finalize an item on the chain"},
          {"topicType": "concept", "topicRef": "Reflection tokens",
           "clarification": "Reflection tokens tellen niet mee voor het handlimiet aan het einde van de beurt.",
           "quote": "Reflection tokens do not count toward your hand size limit"},
          {"topicType": "mechanic", "topicRef": "Arcane Shift",
           "clarification": "Arcane Shift mag ook getarget worden op units die al getapt zijn.",
           "quote": "Arcane Shift may target tapped units"}
        ]}
        Dat was alles.
        """;

    [Fact]
    public void Parse_MixedFaqParagraph_SplitsIntoDiscreteConcepts()
    {
        var r = ClarificationMiner.Parse(LegionAndOthersAnswer);

        Assert.NotNull(r);
        Assert.Equal(3, r.Count);

        var legion = Assert.Single(r, c => c.TopicRef == "Legion");
        Assert.Equal("mechanic", legion.TopicType);
        Assert.Contains("finalize", legion.Clarification);
        Assert.Equal("402.3", legion.SectionRef);
        Assert.Equal("Legion means you finalize an item on the chain", legion.Quote);

        var reflection = Assert.Single(r, c => c.TopicRef == "Reflection tokens");
        Assert.Equal("concept", reflection.TopicType);
        Assert.Null(reflection.SectionRef);

        var arcaneShift = Assert.Single(r, c => c.TopicRef == "Arcane Shift");
        Assert.Equal("mechanic", arcaneShift.TopicType);
    }

    [Fact]
    public void Parse_UnknownTopicType_DegradesToConcept()
    {
        var raw = """{"clarifications": [{"topicType": "iets-onbekends", "topicRef": "X", "clarification": "Uitleg over X."}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("concept", Assert.Single(r).TopicType);
    }

    [Fact]
    public void Parse_MissingClarificationOrTopicRef_SkipsItem()
    {
        var raw = """
            {"clarifications": [
              {"topicType": "mechanic", "topicRef": "Legion"},
              {"topicType": "mechanic", "clarification": "Geen onderwerp hier."},
              {"topicType": "mechanic", "topicRef": "Shield", "clarification": "Shield absorbeert de volgende bron schade."}
            ]}
            """;
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        var item = Assert.Single(r);
        Assert.Equal("Shield", item.TopicRef);
    }

    [Fact]
    public void Parse_DuplicateTopicAndText_Dedupes()
    {
        var raw = """
            {"clarifications": [
              {"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion  betekent finalizen."},
              {"topicType": "mechanic", "topicRef": "legion", "clarification": "Legion betekent finalizen. "}
            ]}
            """;
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ik zie hier geen bruikbare concepten.")]
    [InlineData("{kapotte json}")]
    [InlineData("""{"iets_anders": true}""")]
    public void Parse_GarbageOutput_ReturnsNull(string raw) =>
        Assert.Null(ClarificationMiner.Parse(raw));

    [Fact]
    public void Parse_EmptyResult_ReturnsEmptyList_NotNull()
    {
        var r = ClarificationMiner.Parse("""{"clarifications": []}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_LongClarification_IsTruncated()
    {
        var longText = new string('x', ClarificationMiner.MaxClarificationLength + 50);
        var raw = $$"""{"clarifications": [{"topicType": "concept", "topicRef": "Test", "clarification": "{{longText}}"}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(ClarificationMiner.MaxClarificationLength, Assert.Single(r).Clarification.Length);
    }

    // --- operative-veld (#188): het LLM-oordeel per item -----------------

    [Fact]
    public void Parse_OperativeTrue_SetsOperativeTrue()
    {
        var raw = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen.", "operative": true}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.True(Assert.Single(r).Operative);
    }

    [Fact]
    public void Parse_OperativeFalse_SetsOperativeFalse()
    {
        var raw = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion was clarified.", "operative": false}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.False(Assert.Single(r).Operative);
    }

    [Fact]
    public void Parse_OperativeFieldAbsent_OperativeIsNull()
    {
        // Oude prompt-variant/parse-gat: geen "operative"-veld ⇒ null, niet
        // false — de aanroeper (ClarificationMiningService.StoreAsync)
        // herkent null als "geen LLM-oordeel" en valt terug op IsMetaOnly.
        var raw = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen."}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Null(Assert.Single(r).Operative);
    }

    [Fact]
    public void Parse_OperativeNonBoolean_TreatedAsAbsent_OperativeIsNull()
    {
        var raw = """{"clarifications": [{"topicType": "mechanic", "topicRef": "Legion", "clarification": "Legion betekent finalizen.", "operative": "yes"}]}""";
        var r = ClarificationMiner.Parse(raw);
        Assert.NotNull(r);
        Assert.Null(Assert.Single(r).Operative);
    }
}

/// <summary>Informativiteitscheck (#185) — pure Domain-poort die een
/// geëxtraheerde "verduidelijking" weert die zelf niets meer is dan een kale
/// aankondiging ("X is verduidelijkt/gewijzigd/clarified/changed") zonder de
/// regel/definitie/interactie te noemen. Dit is precies de vorm van de
/// #185-bug: de Legion-"ruling" die uit core-rules-patch-notes werd gemined
/// zei alleen DÁT Legion was verduidelijkt, niet WAT er nu geldt.</summary>
public class ClarificationInformativenessTests
{
    [Theory]
    // De #185-bugvorm zelf: alleen de aankondiging, geen regelinhoud.
    [InlineData("Legion is verduidelijkt.")]
    [InlineData("Legion's gedrag is in deze update verduidelijkt.")]
    [InlineData("De verschillen tussen deze twee interpretaties zijn verduidelijkt.")]
    [InlineData("The differences between these two interpretations have been clarified.")]
    [InlineData("Legion is gewijzigd in deze patch.")]
    [InlineData("Reflection tokens were changed.")]
    // #188-adversariële review: de vals-positief-kant van de regex — een
    // KORTE operatieve zin met een wijzig-werkwoord wordt door IsMetaOnly
    // ten onrechte meta-only bevonden. Dit is exact waarom #188 het LLM-
    // oordeel primair maakt (ClarificationMiner.Operative/JudgeSystemPrompt)
    // en deze regex degradeert tot deterministisch vangnet.
    [InlineData("The rule was clarified so that activated abilities with Legion trigger only once per turn.")]
    public void IsMetaOnly_KaleAankondiging_ReturnsTrue(string clarification) =>
        Assert.True(ClarificationInformativeness.IsMetaOnly(clarification));

    [Theory]
    // Bevat de operatieve kern (de regel zelf) — ook als het woord
    // "verduidelijkt"/"clarified" toevallig voorkomt, telt het als informatief
    // zodra er een vervolg is dat de regel/definitie noemt.
    [InlineData("Legion betekent dat je een item op de chain finalizet.")]
    [InlineData("Reflection tokens tellen niet mee voor het handlimiet.")]
    [InlineData("Legion is verduidelijkt: het betekent dat je een item op de chain finalizet, zoals bij Battering Ram.")]
    [InlineData("Wat hierboven al is verduidelijkt met het volgende voorbeeld: Battering Ram checkt of Legion al gefinalized is voordat de bonus toepast, dus de volgorde van triggers is hier bepalend voor het resultaat.")]
    [InlineData("Shield absorbeert de volgende bron schade.")]
    // #188-adversariële review: de vals-negatief-kant — een lege aankondiging
    // mét dubbele punt glipt via de dubbele-punt-uitweg door de regex heen
    // (het vervolg na de punt is lang genoeg, ook al zegt het zelf niets over
    // WAT er nu geldt). Zelfde reden als hierboven: het LLM-oordeel wint,
    // deze regex is alleen nog het vangnet.
    [InlineData("Legion was clarified: refer to the updated core rules.")]
    public void IsMetaOnly_OperatieveInhoud_ReturnsFalse(string clarification) =>
        Assert.False(ClarificationInformativeness.IsMetaOnly(clarification));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMetaOnly_LegeClarification_ReturnsTrue(string? clarification) =>
        Assert.True(ClarificationInformativeness.IsMetaOnly(clarification));
}

/// <summary>De lichte her-toets-prompt voor CorrectionReevaluationService
/// (#188): parser voor het {"operative": true|false}-antwoord. Puur/getest,
/// zelfde tolerantie-patroon als ClaimJudge/OfficialCheck.</summary>
public class ClarificationInformativenessJudgeTests
{
    [Fact]
    public void ParseOperative_TrueJson_ReturnsTrue() =>
        Assert.True(ClarificationInformativeness.ParseOperative("""{"operative": true}"""));

    [Fact]
    public void ParseOperative_FalseJson_ReturnsFalse() =>
        Assert.False(ClarificationInformativeness.ParseOperative("""{"operative": false}""")!.Value);

    [Fact]
    public void ParseOperative_ToleratesSurroundingProse()
    {
        var raw = "Hier is mijn oordeel:\n{\"operative\": true}\nKlaar.";
        Assert.True(ClarificationInformativeness.ParseOperative(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("geen bruikbare JSON hier")]
    [InlineData("{kapotte json}")]
    [InlineData("""{"iets_anders": true}""")]
    [InlineData("""{"operative": "yes"}""")]
    // Review-regressie (#188): array-vormige kandidaten mogen niet crashen —
    // GetBool → TryGetProperty gooit op een niet-object een InvalidOperation-
    // Exception (geen JsonException). Zonder de objectvorm-guard 500't de
    // her-evaluatie i.p.v. te degraderen naar IsMetaOnly.
    [InlineData("[true]")]
    [InlineData("[1, 2]")]
    [InlineData("This clarification refers to section [402.3] of the updated core rules.")]
    [InlineData("Operative: yes, it applies [1].")]
    public void ParseOperative_OnbruikbaarAntwoord_ReturnsNull(string raw) =>
        Assert.Null(ClarificationInformativeness.ParseOperative(raw));

    [Fact]
    public void BuildJudgePrompt_BevatDeVerduidelijking() =>
        Assert.Contains("Legion betekent finalizen.",
            ClarificationInformativeness.BuildJudgePrompt("Legion betekent finalizen."));
}

/// <summary>Fixture gebaseerd op de échte Unleashed Rules FAQ (playriftbound.com,
/// niet de patch notes): het artikel legt uit dat "play" in de bijgewerkte
/// regels drie technische betekenissen heeft, en dat de tweede betekenis —
/// "finalize an item on the chain" — de variant is die Legion en voorwaardelijke
/// effecten (zoals Battering Ram, Crescent Guardian) gebruiken. Dit is exact
/// het soort operatieve kern die de #185-promptinstructie ("vat de werkende
/// uitspraak inclusief het verbindende voorbeeld") moet vastleggen, in het
/// ENGELS (#185, Sjoerd-eis: dicht bij de brontekst, geen Nederlandse
/// parafrase in de corpus) — in plaats van de kale aankondigingszin die de
/// #177-bug uit de patch notes mined ("Legion's behavior was clarified").
/// De LLM-aanroep zelf is niet deterministisch testbaar (rb-ai is extern) —
/// deze fixture toont dat de PARSER + poort het juiste onderscheid maken
/// gegeven elk van de twee mogelijke extracties.</summary>
public class ClarificationMinerLegionFixtureTests
{
    private const string GroundedQuote = "Legion means you finalize an item on the chain";

    // Zoals de #185-promptinstructie vraagt: de werkende uitspraak (play =
    // finalize) MÉT het verbindende voorbeeld uit de tekst (Battering Ram) —
    // in het ENGELS opgeslagen (#185, Sjoerd-eis): dicht bij de bewoording
    // van de brontekst, geen Nederlandse parafrase in de corpus.
    private const string OperativeAnswer =
        $$"""
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion", "sectionRef": "402.3",
           "clarification": "Play has three technical meanings; for Legion the relevant one is 'finalize' — the moment an item is actually resolved on the chain. Conditional effects that check whether a card has been played, such as on Battering Ram, check this finalize status.",
           "quote": "{{GroundedQuote}}"}
        ]}
        """;

    // De #177-bugvorm: alleen de aankondiging dat er iets veranderd is,
    // zonder te zeggen wat de regel nu inhoudt — ook in het Engels, zoals de
    // #185-opslagtaal voorschrijft.
    private const string MetaOnlyAnswer =
        $$"""
        {"clarifications": [
          {"topicType": "mechanic", "topicRef": "Legion",
           "clarification": "Legion's behavior was clarified in this update.",
           "quote": "{{GroundedQuote}}"}
        ]}
        """;

    [Fact]
    public void Parse_OperativeLegionAnswer_BevatDeWerkendeUitspraakEnHetVoorbeeld()
    {
        var r = ClarificationMiner.Parse(OperativeAnswer);

        Assert.NotNull(r);
        var legion = Assert.Single(r);
        Assert.Equal("Legion", legion.TopicRef);
        Assert.Contains("finalize", legion.Clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Battering Ram", legion.Clarification); // het verbindende voorbeeld
        Assert.False(ClarificationInformativeness.IsMetaOnly(legion.Clarification));
    }

    [Fact]
    public void Parse_MetaOnlyLegionAnswer_ParsetWelMaarIsNietInformatief()
    {
        // De parser zelf weigert een kale aankondiging niet (dat is geen
        // JSON-vormfout) — de informativiteits-poort in
        // ClarificationMiningService.StoreAsync moet dit onderscheppen,
        // exact de #185-fix voor de #177-bug.
        var r = ClarificationMiner.Parse(MetaOnlyAnswer);

        Assert.NotNull(r);
        var legion = Assert.Single(r);
        Assert.True(ClarificationInformativeness.IsMetaOnly(legion.Clarification));
    }
}
