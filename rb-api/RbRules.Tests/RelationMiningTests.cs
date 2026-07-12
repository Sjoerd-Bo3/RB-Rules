using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Pure delen van de relatie-mining (#116): tolerante parser (met de
/// gedeelde LlmJson-vondst, #93-les), ref-validatie tegen de aangeboden lijst,
/// kind-normalisatie en het projectie-filter voor de graph-rebuild.</summary>
public class RelationMiningTests
{
    private static readonly string[] Offered =
    [
        "mechanic:Deflect", "mechanic:Tank", "section:core-rules-pdf/7.4",
        "concept:combat", "card:ogn-011-298", "claim:17",
    ];

    [Fact]
    public void ParseRelations_LeestObjectVorm_EnCanonicaliseertRefs()
    {
        var raw = """
            Hier zijn de relaties [1]:
            {"relations": [
              {"from": "mechanic:deflect", "to": "SECTION:core-rules-pdf/7.4",
               "kind": "Wordt beperkt door", "explanation": "Deflect vermindert alleen combat-schade."},
              {"from": "mechanic:Tank", "to": "concept:combat",
               "kind": "versterkt", "explanation": "Tank stuurt de blokkade in combat."}
            ]}
            """;

        var parsed = RelationMiner.ParseRelations(raw, Offered);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        // Refs komen terug in de aangeboden (canonieke) spelling; het kind
        // is genormaliseerd naar kleine letters.
        Assert.Equal(new ExtractedRelation(
            "mechanic:Deflect", "section:core-rules-pdf/7.4",
            "wordt beperkt door", "Deflect vermindert alleen combat-schade."), parsed[0]);
        Assert.Equal("versterkt", parsed[1].Kind);
    }

    [Fact]
    public void ParseRelations_NietAangebodenRefs_VallenWeg()
    {
        // Kern-invariant: gehallucineerde knopen komen de database nooit in.
        var raw = """
            {"relations": [
              {"from": "mechanic:Verzonnen", "to": "mechanic:Deflect", "kind": "counters", "explanation": "x"},
              {"from": "mechanic:Deflect", "to": "card:niet-bestaand", "kind": "counters", "explanation": "x"},
              {"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "counters", "explanation": "Deflect ontkracht Tank-schade."}
            ]}
            """;

        var parsed = RelationMiner.ParseRelations(raw, Offered);

        var rel = Assert.Single(parsed!);
        Assert.Equal("mechanic:Deflect", rel.FromRef);
        Assert.Equal("mechanic:Tank", rel.ToRef);
    }

    [Fact]
    public void ParseRelations_ZelfRelatieEnDuplicaat_VallenWeg()
    {
        var raw = """
            {"relations": [
              {"from": "mechanic:Deflect", "to": "mechanic:deflect", "kind": "counters", "explanation": "zelf"},
              {"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "counters", "explanation": "a"},
              {"from": "mechanic:deflect", "to": "mechanic:tank", "kind": "COUNTERS", "explanation": "b (duplicaat)"}
            ]}
            """;

        var parsed = RelationMiner.ParseRelations(raw, Offered);

        var rel = Assert.Single(parsed!);
        Assert.Equal("a", rel.Explanation);
    }

    [Fact]
    public void ParseRelations_ZonderKindOfUitleg_ValtWeg()
    {
        var raw = """
            {"relations": [
              {"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "", "explanation": "x"},
              {"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "counters"},
              {"from": "mechanic:Deflect", "to": "concept:combat", "kind": "counters", "explanation": "ok"}
            ]}
            """;

        var parsed = RelationMiner.ParseRelations(raw, Offered);

        var rel = Assert.Single(parsed!);
        Assert.Equal("concept:combat", rel.ToRef);
    }

    [Fact]
    public void ParseRelations_KaleArray_IsTolerantieVoorPromptAfwijking()
    {
        var raw = """[{"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "counters", "explanation": "x"}]""";
        var parsed = RelationMiner.ParseRelations(raw, Offered);
        Assert.Single(parsed!);
    }

    [Fact]
    public void ParseRelations_LegeOogst_IsGeldigResultaat()
    {
        var parsed = RelationMiner.ParseRelations("""{"relations": []}""", Offered);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!);
    }

    [Fact]
    public void ParseRelations_OnbruikbareOutput_GeeftNull()
    {
        // null ⇒ de service logt de rauwe respons in run_log (#93) en laat
        // het anker ongemarkeerd staan.
        Assert.Null(RelationMiner.ParseRelations("Ik zie geen relaties, sorry!", Offered));
        // "[1]" in prose is géén item-lijst (scout-les #87, gedeeld in LlmJson).
        Assert.Null(RelationMiner.ParseRelations("Zie bron [1] voor context.", Offered));
    }

    [Fact]
    public void ParseRelations_CaptOpMaxRelations()
    {
        var items = string.Join(",", Enumerable.Range(0, 40).Select(i =>
            $$"""{"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "kind-{{i}}", "explanation": "x"}"""));
        var parsed = RelationMiner.ParseRelations($$"""{"relations": [{{items}}]}""", Offered);
        Assert.Equal(RelationMiner.MaxRelations, parsed!.Count);
    }

    [Fact]
    public void CandidateRefs_GeeftDistinctFromEnToRefs_OngeachtValidatie()
    {
        // #120: de agentic ask bood geen ref-lijst aan — de aanroeper haalt
        // eerst de kandidaten op om ze tegen het brein te toetsen, ook de
        // verzonnen refs (die moeten juist geteld en geweerd worden).
        var raw = """
            Voorstellen:
            {"relations": [
              {"from": "mechanic:Deflect", "to": "concept:combat", "kind": "verduidelijkt", "explanation": "x"},
              {"from": "MECHANIC:deflect", "to": "card:verzonnen-999", "kind": "counters", "explanation": "x"},
              "geen object — genegeerd"
            ]}
            """;

        var refs = RelationMiner.CandidateRefs(raw);

        // Distinct is case-ongevoelig (eerste spelling wint), volgorde van
        // eerste voorkomen; ontbrekende/onzin-items vallen stil weg.
        Assert.Equal(
            ["mechanic:Deflect", "concept:combat", "card:verzonnen-999"],
            refs);
    }

    [Fact]
    public void CandidateRefs_OnbruikbareOutput_GeeftNull_EnLegeOogstIsGeldig()
    {
        // Zelfde betekenis als ParseRelations: null ⇒ run_log-diagnose.
        Assert.Null(RelationMiner.CandidateRefs("Ik zie geen relaties, sorry!"));
        Assert.Empty(RelationMiner.CandidateRefs("""{"relations": []}""")!);
    }

    [Theory]
    [InlineData("Counters", "counters")]
    [InlineData("  wordt   beperkt door.", "wordt beperkt door")]
    [InlineData("WORDT_BEPERKT_DOOR", "wordt beperkt door")]
    [InlineData("\"enables\"", "enables")]
    public void NormalizeKind_NormaliseertCasingWhitespaceEnRandtekens(string input, string expected)
    {
        Assert.Equal(expected, RelationMiner.NormalizeKind(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("dit is geen kort herbruikbaar relatielabel maar een hele zin over hoe kaarten werken")]
    public void NormalizeKind_OnbruikbaarGeeftNull(string? input)
    {
        Assert.Null(RelationMiner.NormalizeKind(input));
    }

    [Fact]
    public void KindVocabulary_SeedPlusGeaccepteerd_GededupedEnGenormaliseerd()
    {
        var vocab = RelationMiner.KindVocabulary(["Counters", "ONTGRENDELT", "ontgrendelt", " "]);
        // "counters" zit al in de seed; "ontgrendelt" komt er één keer bij.
        Assert.Equal(RelationMiner.SeedKinds.Length + 1, vocab.Count);
        Assert.Contains("ontgrendelt", vocab);
    }

    [Fact]
    public void GetSystemPrompt_BevatHetEffectieveVocabulaire()
    {
        var prompt = RelationMiner.GetSystemPrompt(["ontgrendelt"]);
        Assert.Contains("counters", prompt);
        Assert.Contains("ontgrendelt", prompt);
        Assert.DoesNotContain("{KINDS}", prompt);
    }

    [Fact]
    public void DedupeKey_IsCaseOngevoeligOpRefs_EnGericht()
    {
        Assert.Equal(
            RelationMiner.DedupeKey("mechanic:Deflect", "mechanic:Tank", "counters"),
            RelationMiner.DedupeKey("MECHANIC:DEFLECT", "mechanic:tank", "counters"));
        Assert.NotEqual(
            RelationMiner.DedupeKey("mechanic:Deflect", "mechanic:Tank", "counters"),
            RelationMiner.DedupeKey("mechanic:Tank", "mechanic:Deflect", "counters"));
    }

    // ── Projectie-filter: de poort tussen review en graph ────────────────

    [Fact]
    public void ShouldProject_RejectedNooit_OokMetGeaccepteerdKind()
    {
        var kinds = RelationProjection.AcceptedKindSet([]);
        Assert.False(RelationProjection.ShouldProject("rejected", "counters", kinds));
    }

    [Fact]
    public void ShouldProject_AcceptedEnUnreviewed_AlleenMetGeaccepteerdKind()
    {
        var kinds = RelationProjection.AcceptedKindSet(["ontgrendelt"]);

        // Seed-kind en geaccepteerd kind doen mee, beide statussen.
        Assert.True(RelationProjection.ShouldProject("accepted", "counters", kinds));
        Assert.True(RelationProjection.ShouldProject("unreviewed", "ontgrendelt", kinds));

        // Kandidaat-kind (niet geaccepteerd) blijft buiten de graph, ook als
        // de relatie zelf accepted is — het vocabulaire is de tweede poort.
        Assert.False(RelationProjection.ShouldProject("accepted", "kandidaat-kind", kinds));
        Assert.False(RelationProjection.ShouldProject("unreviewed", "kandidaat-kind", kinds));
    }

    [Fact]
    public void AcceptedKindSet_IsCaseOngevoelig()
    {
        var kinds = RelationProjection.AcceptedKindSet(["Ontgrendelt"]);
        Assert.True(RelationProjection.ShouldProject("accepted", "ontgrendelt", kinds));
    }
}
