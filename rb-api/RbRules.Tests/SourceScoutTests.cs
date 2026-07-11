using RbRules.Domain;

namespace RbRules.Tests;

public class SourceScoutTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsProposals()
    {
        var raw = """
            {"proposals": [
              {"url": "https://example.com/riftbound-judge-faq",
               "name": "Judge FAQ (example.com)",
               "type": "community",
               "motivation": "Verzameling judge-antwoorden op timing-vragen."},
              {"url": "https://uvsgames.com/riftbound/op-guide",
               "name": "Organized Play Guide (UVS)",
               "type": "partner",
               "motivation": "Toernooiprocedures van de OP-partner."}
            ]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(2, r.Count);
        Assert.Equal("https://example.com/riftbound-judge-faq", r[0].Url);
        Assert.Equal("Judge FAQ (example.com)", r[0].Name);
        Assert.Equal("community", r[0].Type);
        Assert.Equal("partner", r[1].Type);
    }

    [Fact]
    public void Parse_ResearchAnswerWithBronnenBlock_IgnoresTrailingText()
    {
        // Echte antwoordvorm: rb-ai's research-contract dwingt een
        // "Bronnen:"-sectie ná de JSON af (rb-ai/src/ai.ts). Die tekst mag de
        // JSON-extractie niet breken en levert zelf geen voorstellen op.
        var raw = """
            Hier zijn de gevonden bronnen:
            {"proposals": [{"url": "https://example.com/wiki/riftbound", "name": "Riftbound Wiki", "type": "community", "motivation": "Community-wiki met keyword-uitleg."}]}

            Bronnen:
            https://example.com/wiki/riftbound (geraadpleegd 2026-07-11)
            https://andere-site.com/zoekresultaten
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("https://example.com/wiki/riftbound", p.Url);
    }

    [Fact]
    public void Parse_JsonCodeFence_IsHandled()
    {
        // De aangescherpte prompt staat een ```json-fence expliciet toe; de
        // parser moet die dus aankunnen, ook met het Bronnen-blok erachter.
        var raw = """
            ```json
            {"proposals": [{"url": "https://example.com/judge-guide", "name": "Judge Guide", "type": "community", "motivation": "Uitleg van judge-procedures."}]}
            ```

            Bronnen:
            https://example.com/judge-guide (geraadpleegd 2026-07-11)
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("https://example.com/judge-guide", Assert.Single(r).Url);
    }

    [Fact]
    public void Parse_ProseWithBracketsBeforeJson_StillFindsProposals()
    {
        // Regressie eerste live run (#63): prose vóór de JSON met een "[" erin
        // (bronmarkers, markdown-links) liet de oude first/last-index-extractie
        // een kapot array-blok kiezen in plaats van het JSON-object.
        var raw = """
            Ik heb [1] nieuwe bron gevonden, zie [de wiki](https://example.com/wiki):
            {"proposals": [{"url": "https://example.com/wiki/rules", "name": "Wiki Rules", "type": "community", "motivation": "Regels-uitleg per keyword."}]}

            Bronnen:
            [1] https://example.com/wiki/rules
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("https://example.com/wiki/rules", Assert.Single(r).Url);
    }

    [Theory]
    [InlineData("""
        Bronnen:
        https://example.com/zoekresultaat (geraadpleegd 2026-07-11)
        https://andere-site.com/riftbound
        """)]
    [InlineData("""
        Bronnen:
        [1] https://example.com/zoekresultaat
        [2] https://andere-site.com/riftbound
        """)]
    public void Parse_OnlyBronnenBlock_ReturnsNull(string raw) =>
        // Alleen het (door rb-ai afgedwongen) Bronnen-blok, zonder JSON-object:
        // onbruikbaar antwoord, geen lege-lijst-verwarring — ook niet wanneer
        // de "[1]"-markers technisch als JSON-array parsen.
        Assert.Null(SourceScout.Parse(raw));

    [Theory]
    [InlineData("")]
    [InlineData("Ik heb geen nieuwe bronnen kunnen vinden.")]
    [InlineData("{not valid json}")]
    [InlineData("""{"iets_anders": true}""")]
    public void Parse_GarbageOutput_ReturnsNull(string raw) =>
        // null = onbruikbaar antwoord (degradatiepad); dat is iets anders dan
        // een geldige lege lijst ("niets nieuws gevonden").
        Assert.Null(SourceScout.Parse(raw));

    [Fact]
    public void Parse_EmptyProposals_ReturnsEmptyList()
    {
        var r = SourceScout.Parse("""{"proposals": []}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_BareArray_IsTolerated()
    {
        var r = SourceScout.Parse(
            """[{"url": "https://example.com/guide", "name": "Guide", "type": "community", "motivation": "x"}]""");
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("http://example.com/guide")]
    [InlineData("ftp://example.com/guide")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com/guide")]
    [InlineData("")]
    public void Parse_NonHttpsUrl_IsDropped(string url)
    {
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "{{url}}", "name": "x", "type": "community", "motivation": "x"}]}""");
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    [Fact]
    public void Parse_KnownUrls_AreExcluded_CaseAndSlashInsensitive()
    {
        var raw = """
            {"proposals": [
              {"url": "https://Example.com/Guide/", "name": "al bekend", "type": "community", "motivation": "x"},
              {"url": "https://example.com/nieuw", "name": "nieuw", "type": "community", "motivation": "x"}
            ]}
            """;
        var r = SourceScout.Parse(raw, ["https://example.com/guide"]);
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("https://example.com/nieuw", p.Url);
    }

    [Fact]
    public void Parse_DuplicatesWithinAnswer_AreDeduped()
    {
        var raw = """
            {"proposals": [
              {"url": "https://example.com/guide", "name": "a", "type": "community", "motivation": "x"},
              {"url": "https://example.com/guide/", "name": "b", "type": "community", "motivation": "x"}
            ]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Single(r);
    }

    [Fact]
    public void Parse_CapsAtMaxProposals()
    {
        var items = string.Join(",", Enumerable.Range(1, 15).Select(i =>
            $$"""{"url": "https://example.com/p{{i}}", "name": "p{{i}}", "type": "community", "motivation": "x"}"""));
        var r = SourceScout.Parse($$"""{"proposals": [{{items}}]}""");
        Assert.NotNull(r);
        Assert.Equal(SourceScout.MaxProposals, r.Count);
    }

    [Theory]
    [InlineData("official", "official")]
    [InlineData("Partner", "partner")]
    [InlineData("fansite", "community")]   // onbekend label degradeert
    [InlineData(null, "community")]        // ontbrekend type ook
    public void Parse_TypeClampsToKnownVocabulary(string? type, string expected)
    {
        var typeJson = type is null ? "" : $""" "type": "{type}", """;
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "https://example.com/guide", "name": "x", {{typeJson}} "motivation": "x"}]}""");
        Assert.NotNull(r);
        Assert.Equal(expected, Assert.Single(r).Type);
    }

    [Fact]
    public void Parse_MissingNameAndMotivation_FallsBackGracefully()
    {
        // Zonder naam blijft de vondst bruikbaar (host als naam); motivatie
        // mag leeg zijn — de beheerder ziet de URL en beslist zelf.
        var r = SourceScout.Parse("""{"proposals": [{"url": "https://example.com/guide"}]}""");
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("example.com", p.Name);
        Assert.Equal("", p.Motivation);
    }

    [Fact]
    public void Parse_TruncatesRunawayFields()
    {
        var r = SourceScout.Parse(
            $$"""{"proposals": [{"url": "https://example.com/guide", "name": "{{new string('n', 500)}}", "type": "community", "motivation": "{{new string('m', 900)}}"}]}""");
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal(120, p.Name.Length);
        Assert.Equal(300, p.Motivation.Length);
    }

    [Fact]
    public void Parse_NonObjectItems_AreSkipped()
    {
        var raw = """
            {"proposals": ["https://example.com/kaal", 42,
              {"url": "https://example.com/goed", "name": "x", "type": "community", "motivation": "x"}]}
            """;
        var r = SourceScout.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("https://example.com/goed", Assert.Single(r).Url);
    }

    [Fact]
    public void Parse_NewProposals_StartUnreviewed()
    {
        // De vondst ís het reviewqueue-item: status "proposed", nog niet
        // beoordeeld — accepteren/verwerpen is aan de beheerder.
        var r = SourceScout.Parse(
            """{"proposals": [{"url": "https://example.com/guide", "name": "x", "type": "community", "motivation": "x"}]}""");
        Assert.NotNull(r);
        var p = Assert.Single(r);
        Assert.Equal("proposed", p.Status);
        Assert.Null(p.ReviewedAt);
    }

    // ── Backfill (#63): run_log-regels van vóór de reviewqueue ────────────

    [Fact]
    public void FromRunLog_WellFormedDetail_ReconstructsAllFields()
    {
        var foundAt = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
        var p = SourceScout.FromRunLog(
            "https://example.com/judge-faq",
            "https://example.com/judge-faq — Judge FAQ (example.com) (community): Verzameling judge-antwoorden.",
            foundAt);
        Assert.Equal("https://example.com/judge-faq", p.Url);
        // Greedy: haakjes in de naam zelf verwarren de type-extractie niet.
        Assert.Equal("Judge FAQ (example.com)", p.Name);
        Assert.Equal("community", p.Type);
        Assert.Equal("Verzameling judge-antwoorden.", p.Motivation);
        Assert.Equal(foundAt, p.FoundAt);
        Assert.Equal("proposed", p.Status);
    }

    [Fact]
    public void FromRunLog_PartnerType_IsPreserved()
    {
        var p = SourceScout.FromRunLog(
            "https://uvsgames.com/op-guide",
            "https://uvsgames.com/op-guide — OP Guide (partner): Toernooiprocedures.",
            DateTimeOffset.UtcNow);
        Assert.Equal("partner", p.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("iets zonder het verwachte formaat")]
    public void FromRunLog_UnparsableDetail_DegradesToHostAndCommunity(string? detail)
    {
        // Nooit een trust-upgrade door een parse-gok: onherkenbaar detail
        // wordt community, met de host als naam.
        var p = SourceScout.FromRunLog("https://example.com/guide", detail, DateTimeOffset.UtcNow);
        Assert.Equal("example.com", p.Name);
        Assert.Equal("community", p.Type);
        Assert.Equal(detail ?? "", p.Motivation);
    }

    [Fact]
    public void FromRunLog_UnknownTypeLabel_DegradesToCommunity()
    {
        var p = SourceScout.FromRunLog(
            "https://example.com/guide",
            "https://example.com/guide — Gids (fansite): Uitleg.",
            DateTimeOffset.UtcNow);
        Assert.Equal("community", p.Type);
    }

    // ── Accepteren (#63): veilige register-defaults ───────────────────────

    private static SourceProposal Proposal(string url, string type = "community") => new()
    {
        Url = url, Name = "Testbron", Type = type, Motivation = "x",
    };

    [Fact]
    public void ToSource_SafeDefaults_NeverEnabled()
    {
        var src = SourceScout.ToSource(Proposal("https://example.com/rules-guide"));
        // De kern van de kennislagen-regel: niets gaat automatisch aan.
        Assert.False(src.Enabled);
        Assert.Equal("weekly", src.Cadence);
        Assert.Equal("html", src.Parser);
        Assert.Equal("https://example.com/rules-guide", src.Url);
        Assert.Equal("Testbron", src.Name);
    }

    [Theory]
    [InlineData("https://uvsgames.com/uploads/how-to-play.pdf", "pdf")]
    [InlineData("https://uvsgames.com/uploads/HOW-TO-PLAY.PDF", "pdf")]
    [InlineData("https://example.com/download.pdf?v=2", "pdf")]
    [InlineData("https://example.com/pdf-uitleg", "html")]     // ".pdf" alleen als pad-einde
    [InlineData("https://example.com/guide", "html")]
    public void ToSource_ParserFollowsFileType(string url, string expected) =>
        Assert.Equal(expected, SourceScout.ToSource(Proposal(url)).Parser);

    [Theory]
    [InlineData("official", 1, "official")]
    [InlineData("partner", 2, "partner")]
    [InlineData("community", 3, "community")]
    [InlineData("fansite", 3, "community")]    // onbekend label degradeert
    public void ToSource_TrustFollowsTypeEstimate_NeverTier1ForNonOfficial(
        string type, int expectedTier, string expectedType)
    {
        var src = SourceScout.ToSource(Proposal("https://example.com/guide", type));
        Assert.Equal((short)expectedTier, src.TrustTier);
        Assert.Equal(expectedType, src.Type);
    }

    [Theory]
    [InlineData("https://www.riftbound.gg/judge-faq/", "riftbound-gg-judge-faq")]
    [InlineData("https://example.com/uploads/How_to-Play.pdf", "example-com-how-to-play")]
    [InlineData("https://example.com/", "example-com")]
    public void SlugForUrl_IsReadableAndSanitized(string url, string expected) =>
        Assert.Equal(expected, SourceScout.SlugForUrl(url));

    [Fact]
    public void SlugForUrl_CapsLength()
    {
        var slug = SourceScout.SlugForUrl(
            $"https://example.com/{new string('a', 120)}");
        Assert.True(slug.Length <= 60);
    }
}
