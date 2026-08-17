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
            Here are the relations [1]:
            {"relations": [
              {"from": "mechanic:deflect", "to": "SECTION:core-rules-pdf/7.4",
               "kind": "Is limited by", "explanation": "Deflect only reduces combat damage."},
              {"from": "mechanic:Tank", "to": "concept:combat",
               "kind": "strengthens", "explanation": "Tank directs the block in combat."}
            ]}
            """;

        var parsed = RelationMiner.ParseRelations(raw, Offered);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        // Refs komen terug in de aangeboden (canonieke) spelling; het kind
        // is genormaliseerd naar kleine letters.
        Assert.Equal(new ExtractedRelation(
            "mechanic:Deflect", "section:core-rules-pdf/7.4",
            "is limited by", "Deflect only reduces combat damage."), parsed[0]);
        Assert.Equal("strengthens", parsed[1].Kind);
    }

    [Fact]
    public void ParseRelations_NietAangebodenRefs_VallenWeg()
    {
        // Kern-invariant: gehallucineerde knopen komen de database nooit in.
        var raw = """
            {"relations": [
              {"from": "mechanic:Verzonnen", "to": "mechanic:Deflect", "kind": "counters", "explanation": "x"},
              {"from": "mechanic:Deflect", "to": "card:niet-bestaand", "kind": "counters", "explanation": "x"},
              {"from": "mechanic:Deflect", "to": "mechanic:Tank", "kind": "counters", "explanation": "Deflect negates Tank damage."}
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
        Assert.Null(RelationMiner.ParseRelations("I don't see any relations, sorry!", Offered));
        // "[1]" in prose is géén item-lijst (scout-les #87, gedeeld in LlmJson).
        Assert.Null(RelationMiner.ParseRelations("See source [1] for context.", Offered));
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
            Proposals:
            {"relations": [
              {"from": "mechanic:Deflect", "to": "concept:combat", "kind": "clarifies", "explanation": "x"},
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
        Assert.Null(RelationMiner.CandidateRefs("I don't see any relations, sorry!"));
        Assert.Empty(RelationMiner.CandidateRefs("""{"relations": []}""")!);
    }

    [Theory]
    [InlineData("Counters", "counters")]
    [InlineData("  is   limited by.", "is limited by")]
    [InlineData("IS_LIMITED_BY", "is limited by")]
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
        var vocab = RelationMiner.KindVocabulary(["Counters", "UNLOCKS", "unlocks", " "]);
        // "counters" zit al in de seed; "unlocks" komt er één keer bij.
        Assert.Equal(RelationMiner.SeedKinds.Length + 1, vocab.Count);
        Assert.Contains("unlocks", vocab);
    }

    [Fact]
    public void GetSystemPrompt_BevatHetEffectieveVocabulaire()
    {
        var prompt = RelationMiner.GetSystemPrompt(["unlocks"]);
        Assert.Contains("counters", prompt);
        Assert.Contains("unlocks", prompt);
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
        var kinds = RelationProjection.AcceptedKindSet(["unlocks"]);

        // Seed-kind en geaccepteerd kind doen mee, beide statussen.
        Assert.True(RelationProjection.ShouldProject("accepted", "counters", kinds));
        Assert.True(RelationProjection.ShouldProject("unreviewed", "unlocks", kinds));

        // Kandidaat-kind (niet geaccepteerd) blijft buiten de graph, ook als
        // de relatie zelf accepted is — het vocabulaire is de tweede poort.
        Assert.False(RelationProjection.ShouldProject("accepted", "kandidaat-kind", kinds));
        Assert.False(RelationProjection.ShouldProject("unreviewed", "kandidaat-kind", kinds));
    }

    [Fact]
    public void AcceptedKindSet_IsCaseOngevoelig()
    {
        var kinds = RelationProjection.AcceptedKindSet(["Unlocks"]);
        Assert.True(RelationProjection.ShouldProject("accepted", "unlocks", kinds));
    }

    // ── RELATES_TO-eindpunt-poort (#321) ───────────────────────────────

    [Fact]
    public void CanBeEndpoint_PreciesDeVijfProjecteerbareSoorten()
    {
        // LITERALS, geen afleiding uit de catalogus die de poort zelf leest
        // (#286d/#293b: een assertie tegen de constante die ze bewaakt schuift
        // mee). Verandert de gemeten projectie-breedte, dan hoort deze test
        // bewust rood te gaan — samen met declaratie én disjunctie (#317).
        BrainRefKind[] projecteerbaar =
        [
            BrainRefKind.Card, BrainRefKind.Mechanic, BrainRefKind.Concept,
            BrainRefKind.Section, BrainRefKind.Claim,
        ];

        foreach (var kind in Enum.GetValues<BrainRefKind>())
            Assert.Equal(projecteerbaar.Contains(kind), RelationProjection.CanBeEndpoint(kind));
    }

    [Fact]
    public void CanBeEndpoint_LeestDeCatalogus_GeenTweedeKopie()
    {
        // De kern van #321: poort en projectie lezen dezelfde bron
        // (ProjectionEdgeShapeCatalog, die ProjectionLabelGuardTests in beide
        // richtingen tegen de uitgevoerde Cypher houdt). Zou iemand de poort
        // ooit op een eigen lijst zetten, dan gaat deze spiegel rood zodra de
        // catalogus beweegt — precies de drift die een kopie stil maakt.
        var shapes = RbRules.Domain.Ontology.ProjectionEdgeShapeCatalog
            .For(RelationProjection.RelatesToEdgeName)
            .ToList();
        Assert.NotEmpty(shapes);

        foreach (var kind in Enum.GetValues<BrainRefKind>())
        {
            var label = BrainQuery.GraphLabel(kind);
            var verwacht = label is not null && shapes.Any(s =>
                s.FromLabels.Contains(label, StringComparer.Ordinal) ||
                s.ToLabels.Contains(label, StringComparer.Ordinal));
            Assert.Equal(verwacht, RelationProjection.CanBeEndpoint(kind));
        }
    }

    [Fact]
    public void RelatesToVorm_IsSymmetrisch_AndersPoortZijdeBewustMaken()
    {
        // De agentic poort toetst kandidaat-refs zijde-loos (een kandidaat is
        // nog niet aan een van/naar-kant gebonden). Dat is alleen correct
        // zolang beide kanten dezelfde soorten dragen — lopen ze uiteen, dan
        // moet de poort per voorstel-kant gaan toetsen i.p.v. per kandidaat.
        foreach (var shape in RbRules.Domain.Ontology.ProjectionEdgeShapeCatalog
                     .For(RelationProjection.RelatesToEdgeName))
            Assert.True(
                shape.FromLabels.Order(StringComparer.Ordinal)
                    .SequenceEqual(shape.ToLabels.Order(StringComparer.Ordinal)),
                $"RELATES_TO-vorm {shape.Format()} is asymmetrisch geworden — maak "
                + "RelationProjection.CanBeEndpoint(kind) en de agentic poort zijde-bewust");
    }

    [Fact]
    public void CanBeEndpoint_RefTekst_OnparseerbaarOfBreinNamespace_TeltAlsBuiten()
    {
        // entity:/predicate:-refs (brein-namespace, geen BrainRef-alfabet)
        // kunnen per constructie nooit als eindpunt landen.
        Assert.False(RelationProjection.CanBeEndpoint(
            "entity:12", RbRules.Domain.Ontology.EdgeEndpoint.From));
        Assert.False(RelationProjection.CanBeEndpoint(
            (string?)null, RbRules.Domain.Ontology.EdgeEndpoint.From));
        Assert.True(RelationProjection.CanBeEndpoint(
            "card:ogn-011-298", RbRules.Domain.Ontology.EdgeEndpoint.To));
    }

    // ── Eerlijke RELATES_TO-telling (#321, ADR-20) ─────────────────────

    [Fact]
    public void RelatesToWriteTally_SplitsHetGatPerOorzaak()
    {
        var tally = RelatesToWriteTally.Create(offered: 5, written: 2, outsideProjection: 2);

        Assert.Equal(5, tally.Offered);
        Assert.Equal(2, tally.Written);
        Assert.Equal(3, tally.Dropped);
        Assert.Equal(2, tally.OutsideProjection);
        Assert.Equal(1, tally.MissingNode);
        Assert.Equal("2 eindpunt-soort buiten de projectie, 1 ref zonder knoop",
            tally.OorzaakTekst());
    }

    [Fact]
    public void RelatesToWriteTally_ZonderMeting_RekentAlleenHetDeterministischeDeelAf()
    {
        // Geen count(r)-rij (opnemende test-driver): het buiten-de-projectie-
        // deel is tóch bekend — de WHERE weigert het per constructie — en telt
        // dus mee; over de rest is niets gemeten en die geldt als geschreven.
        var tally = RelatesToWriteTally.Create(offered: 4, written: null, outsideProjection: 1);

        Assert.Equal(3, tally.Written);
        Assert.Equal(1, tally.OutsideProjection);
        Assert.Equal(0, tally.MissingNode);
        Assert.Equal(1, tally.Dropped);
    }

    [Fact]
    public void RelatesToWriteTally_AllesGeland_GeenGat()
    {
        var tally = RelatesToWriteTally.Create(offered: 3, written: 3, outsideProjection: 0);
        Assert.Equal(0, tally.Dropped);
    }
}
