using RbRules.Domain;

namespace RbRules.Tests;

public class ClaimTopicMapperTests
{
    private static ClaimTopicMapper CreateMapper() => ClaimTopicMapper.Create(
        cards:
        [
            ("ogn-011-298", "Viktor", null),
            ("ogn-011-299", "Viktor (Alternate Art)", "ogn-011-298"),
            ("ogn-045-001", "Annie", null),
        ],
        mechanics: ["Accelerate", "Deathknell"],
        sections:
        [
            ("core-rules-pdf", "101"),
            ("core-rules-pdf", "101.2"),
            ("core-rules-pdf", "101.2.d"),
            ("tournament-rules-pdf", "101"), // zelfde code, lagere voorkeur
        ],
        concepts:
        [
            ("turn-structure", "De beurtstructuur"),
            ("combat", "Combat en showdowns"),
        ]);

    [Theory]
    [InlineData("Viktor", "card:ogn-011-298")]
    [InlineData("  viktor  ", "card:ogn-011-298")]          // hoofdletters/witruimte
    [InlineData("Viktor (Alternate Art)", "card:ogn-011-298")] // variant → canoniek (#57)
    public void Resolve_Card_MapsToCanonicalId(string topicRef, string expected) =>
        Assert.Equal(expected, CreateMapper().Resolve("card", topicRef)?.Format());

    [Fact]
    public void Resolve_Mechanic_IsCaseInsensitive_KeepsCanonicalSpelling() =>
        Assert.Equal("mechanic:Accelerate",
            CreateMapper().Resolve("mechanic", "accelerate")?.Format());

    [Theory]
    [InlineData("101.2", "section:core-rules-pdf/101.2")]
    [InlineData("§ 101.2", "section:core-rules-pdf/101.2")]   // vrije vorm
    [InlineData("rule 101.2", "section:core-rules-pdf/101.2")]
    [InlineData("101.2d", "section:core-rules-pdf/101.2.d")]  // compacte vorm genormaliseerd
    public void Resolve_Section_ParsesFreeFormReferences(string topicRef, string expected) =>
        Assert.Equal(expected, CreateMapper().Resolve("section", topicRef)?.Format());

    [Fact]
    public void Resolve_Section_SharedCode_PrefersFirstListedSource() =>
        // "101" bestaat in beide bronnen; de aanroeper bepaalt de volgorde
        // (bron-rank) en de eerste wint — deterministisch.
        Assert.Equal("section:core-rules-pdf/101",
            CreateMapper().Resolve("section", "101")?.Format());

    [Theory]
    [InlineData("turn-structure")]      // topic-key
    [InlineData("turn structure")]      // key als lopende tekst
    [InlineData("De beurtstructuur")]   // NL-titel
    public void Resolve_Concept_MatchesKeyAndTitle(string topicRef) =>
        Assert.Equal("concept:turn-structure",
            CreateMapper().Resolve("concept", topicRef)?.Format());

    [Theory]
    [InlineData("card", "Onbekende Kaart")]
    [InlineData("mechanic", "Vliegen")]
    [InlineData("section", "999.9")]
    [InlineData("section", "geen nummer te bekennen")]
    [InlineData("section", "!!!")]
    [InlineData("concept", "mulligan")] // vrije-tekst-concept zonder primer-doc
    [InlineData("weird-type", "Viktor")]
    [InlineData("card", "")]
    [InlineData("card", null)]
    [InlineData(null, "Viktor")]
    public void Resolve_NoMatch_ReturnsNull_NeverThrows(string? topicType, string? topicRef) =>
        // Het contract uit issue #104: een niet-matchende topic_ref betekent
        // een claim-knoop zónder ABOUT-edge — nooit een crash.
        Assert.Null(CreateMapper().Resolve(topicType, topicRef));

    [Fact]
    public void ResolveSection_UsedForKnowledgeDocRefs_ResolvesPlainCode() =>
        Assert.Equal("section:core-rules-pdf/101.2.d",
            CreateMapper().ResolveSection("101.2.d")?.Format());
}

public class ChangeAffectsMapperTests
{
    private static ChangeAffectsMapper CreateMapper() => ChangeAffectsMapper.Create(
        canonicalCards:
        [
            ("ogn-011-298", "Viktor"),
            ("ogn-011-300", "Viktor, Machine Herald"),
            ("ogn-101-001", "Sett"),
        ],
        sections:
        [
            ("core-rules-pdf", "101"),
            ("core-rules-pdf", "101.2"),
            ("tournament-rules-pdf", "1.3"),
        ]);

    [Fact]
    public void Resolve_Errata_MatchesCardNames()
    {
        var targets = CreateMapper().Resolve("errata",
            "Updated card text for Viktor: gains Accelerate.");
        Assert.Equal(["card:ogn-011-298"], targets.Select(t => t.Format()));
    }

    [Fact]
    public void Resolve_Errata_LongestNameWinsAtSamePosition()
    {
        var targets = CreateMapper().Resolve("ban",
            "Viktor, Machine Herald is banned in constructed.");
        Assert.Equal(["card:ogn-011-300"], targets.Select(t => t.Format()));
    }

    [Fact]
    public void Resolve_Errata_NoSubstringFalsePositives() =>
        // "Sett" mag niet matchen binnen "Setting" — naam-match is exact.
        Assert.Empty(CreateMapper().Resolve("errata", "Setting up the board."));

    [Fact]
    public void Resolve_Errata_DuplicateMentions_YieldOneTarget()
    {
        var targets = CreateMapper().Resolve("errata", "Viktor... and again Viktor.");
        Assert.Single(targets);
    }

    [Fact]
    public void Resolve_CoreRule_MatchesKnownSectionCodes()
    {
        var targets = CreateMapper().Resolve("core-rule",
            "Zie § 101.2 en 999.9 (onbekend); ook 101 telt mee.");
        Assert.Equal(
            ["section:core-rules-pdf/101.2", "section:core-rules-pdf/101"],
            targets.Select(t => t.Format()));
    }

    [Fact]
    public void Resolve_CoreRule_ShortBareNumbersNeverMatch() =>
        // "40 cards" mag nooit §40 raken: losse nummers vereisen 3-4 cijfers
        // én moeten als code bestaan.
        Assert.Empty(ChangeAffectsMapper.Create(
                [("x", "Kaart")], [("core-rules-pdf", "40")])
            .Resolve("core-rule", "decks contain 40 cards"));

    [Fact]
    public void Resolve_TournamentRule_MatchesDottedCodes()
    {
        var targets = CreateMapper().Resolve("tournament-rule", "penalty per 1.3 applies");
        Assert.Equal(["section:tournament-rules-pdf/1.3"], targets.Select(t => t.Format()));
    }

    [Theory]
    [InlineData("set-release", "Viktor en § 101.2 in één zin")]
    [InlineData("editorial", "Viktor")]
    [InlineData("unknown", "Viktor")]
    [InlineData(null, "Viktor")]
    public void Resolve_NonRuleNonCardTypes_YieldNoTargets(string? changeType, string text) =>
        Assert.Empty(CreateMapper().Resolve(changeType, text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyText_YieldsNoTargets_NeverThrows(string? text) =>
        Assert.Empty(CreateMapper().Resolve("errata", text));

    [Fact]
    public void Resolve_CapsTargets_AtMaxTargets()
    {
        var sections = Enumerable.Range(100, 40)
            .Select(n => ("core-rules-pdf", $"{n}.1")).ToList();
        var mapper = ChangeAffectsMapper.Create([("x", "Kaart")], sections);
        var text = string.Join(", ", sections.Select(s => s.Item2));
        Assert.Equal(ChangeAffectsMapper.MaxTargets,
            mapper.Resolve("core-rule", text).Count);
    }

    [Fact]
    public void Resolve_WithoutKnownCards_YieldsNoTargets_NeverThrows() =>
        Assert.Empty(ChangeAffectsMapper.Create([], []).Resolve("errata", "Viktor"));
}
