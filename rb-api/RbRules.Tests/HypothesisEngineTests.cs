using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Fase 5 (#229, §5) — de abductieve hypothese-motor + de getypeerde mechanic-
/// predicaten. Dekt: de predicaat-vorm/extractie-poort, de drie abductieve regels (join
/// op dezelfde as, constanten, domein-compat-prune), de MEETBARE precisie-/kostenwinst
/// (geen verzonnen factor), de koppeling hypothese → fase-2-poort → model_hypothesized_
/// unruled (cold-start), en het BEGRENSDE residuele embedding-kanaal.</summary>
public class HypothesisEngineTests
{
    private static PredicateHolder Card(string id, IReadOnlyCollection<string> domains,
        params (string pred, string obj)[] preds) =>
        new($"card:{id}", EntityType.Unit, domains,
            [.. preds.Select(p => new PredicateFact(p.pred, p.obj))]);

    // ── Predicaat-vorm ────────────────────────────────────────────────────────

    [Fact]
    public void PredicateKinds_Canonicalize_ToleranteMaarStrikteVorm()
    {
        Assert.Equal("triggers_on", MechanicPredicateKinds.Canonicalize("Triggers On"));
        Assert.Equal("requires_target", MechanicPredicateKinds.Canonicalize("requires-target"));
        Assert.Null(MechanicPredicateKinds.Canonicalize("summons"));
        Assert.Null(MechanicPredicateKinds.Canonicalize(""));
    }

    [Fact]
    public void PredicateLexicon_Normalize_JointCasingEnWitruimte()
    {
        Assert.Equal("exhaust", MechanicPredicateLexicon.Normalize("  Exhaust "));
        Assert.Equal("on exhaust", MechanicPredicateLexicon.Normalize("On   Exhaust"));
    }

    [Fact]
    public void PredicateDedupe_Genormaliseerd_ZelfdePredicaatEnToken()
    {
        Assert.Equal(
            MechanicPredicateDedupe.Key(7, "Triggers On", "Exhaust"),
            MechanicPredicateDedupe.Key(7, "triggers_on", "exhaust"));
    }

    // ── Extractie-vorm (tweede muur) ───────────────────────────────────────────

    [Fact]
    public void Extraction_Parse_WeigertOnbekendPredicaatEnLeegToken()
    {
        var raw = """
            {"predicates":[
              {"predicate":"triggers_on","object":"exhaust"},
              {"predicate":"summons","object":"unit"},
              {"predicate":"grants","object":"  "},
              {"predicate":"grants","object":"Tank"}
            ]}
            """;
        var parsed = MechanicPredicateExtraction.Parse(raw);
        Assert.Equal(2, parsed.Count);
        Assert.Contains(parsed, p => p.Predicate == "triggers_on" && p.ObjectToken == "exhaust");
        Assert.Contains(parsed, p => p.Predicate == "grants" && p.ObjectToken == "tank");
    }

    [Fact]
    public void Extraction_KnownTokensOnly_WeigertBuitenLexicon()
    {
        var raw = """{"predicates":[{"predicate":"requires_target","object":"planeswalker"}]}""";
        Assert.Empty(MechanicPredicateExtraction.Parse(raw, knownTokensOnly: true));
        Assert.Single(MechanicPredicateExtraction.Parse(raw, knownTokensOnly: false));
    }

    // ── Abductieve regels ──────────────────────────────────────────────────────

    [Fact]
    public void Nonbo_JoinOpZelfdeAs_VuurtOpExhaustAntagonisme()
    {
        var x = Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust"));
        var y = Card("y", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust"));
        var hyp = HypothesisEngine.Generate([x, y]);

        var nonbo = Assert.Single(hyp);
        Assert.Equal(HypothesisKinds.Nonbo, nonbo.Kind);
        Assert.Equal("nonbo:exhaust-payoff-vs-ready", nonbo.RuleId);
        Assert.Equal("card:x", nonbo.AgentRef);
        Assert.Equal("card:y", nonbo.PatientRef);
        // Het deterministische bewijs = regel-id + antecedent-tuples.
        Assert.Contains("triggers_on(card:x, exhaust)", nonbo.Reason);
        Assert.Contains("prevents(card:y, exhaust)", nonbo.Reason);
    }

    [Fact]
    public void Nonbo_OnverenigbaarDomein_WordtGeprunet()
    {
        var x = Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust"));
        var y = Card("y", ["order"], (MechanicPredicateKinds.Prevents, "exhaust"));
        Assert.Empty(HypothesisEngine.Generate([x, y]));
    }

    [Fact]
    public void Counter_Constanten_VuurtCrossDeck_ZonderDomeinEis()
    {
        // requires_target(unit) vs grants(hidden), verschillende domeinen — de
        // counter-regel eist GEEN domein-compat (tegenoverliggende bord-kanten).
        var attacker = Card("snipe", ["order"], (MechanicPredicateKinds.RequiresTarget, "unit"));
        var elusive = Card("ghost", ["fury"], (MechanicPredicateKinds.Grants, "hidden"));
        var hyp = HypothesisEngine.Generate([attacker, elusive]);

        var counter = Assert.Single(hyp);
        Assert.Equal(HypothesisKinds.Counter, counter.Kind);
        Assert.Equal("card:snipe", counter.AgentRef);
        Assert.Equal("card:ghost", counter.PatientRef);
    }

    [Fact]
    public void Synergy_TankPlusDeflect_VuurtBinnenDomein()
    {
        var tank = Card("bastion", ["fury"], (MechanicPredicateKinds.Grants, "tank"));
        var deflect = Card("mirror", ["fury"], (MechanicPredicateKinds.Grants, "deflect"));
        var hyp = HypothesisEngine.Generate([tank, deflect]);

        Assert.Single(hyp, h => h.Kind == HypothesisKinds.Synergy);
    }

    [Fact]
    public void Generate_GeenSelfPair_EnGeenBlindeRuis()
    {
        // Eén kaart die beide kanten van het exhaust-antagonisme draagt: geen self-pair.
        var both = Card("both", ["fury"],
            (MechanicPredicateKinds.TriggersOn, "exhaust"),
            (MechanicPredicateKinds.Prevents, "exhaust"));
        // Ruis-kaarten zonder complementair predicaat vormen GEEN paar.
        var noise1 = Card("n1", ["fury"], (MechanicPredicateKinds.Grants, "shield"));
        var noise2 = Card("n2", ["fury"], (MechanicPredicateKinds.Grants, "shield"));
        Assert.Empty(HypothesisEngine.Generate([both, noise1, noise2]));
    }

    // ── Meetbare precisie-/kostenwinst (kritiek B7) ────────────────────────────

    [Fact]
    public void Yield_MeetKostenEnPrecisie_UitDeData_GeenVasteFactor()
    {
        // 2 payoffs + 2 preventers (complementair) + 96 ruis-kaarten.
        var holders = new List<PredicateHolder>
        {
            Card("p1", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("p2", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("r1", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
            Card("r2", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
        };
        for (var i = 0; i < 96; i++)
            holders.Add(Card($"noise{i}", ["fury"], (MechanicPredicateKinds.Grants, "shield")));

        var hyp = HypothesisEngine.Generate(holders);
        Assert.Equal(4, hyp.Count); // p1×r1, p1×r2, p2×r1, p2×r2

        var gold = hyp.Select(h => h.UnorderedPairKey).ToHashSet();
        var yield = HypothesisYield.Measure(holders.Count, hyp, gold);

        Assert.Equal(100, yield.EntityCount);
        Assert.Equal(4950, yield.BlindPairCount);          // 100·99/2 — de blinde baseline
        Assert.Equal(4, yield.HypothesisPairCount);        // wat écht naar de LLM gaat
        Assert.True(yield.ReductionFactor > 1000);         // gemeten, niet verzonnen
        Assert.Equal(1.0, yield.Precision);                // alle 4 zijn de gouden paren
        Assert.Equal(4.0 / 4950, yield.BlindBaseRate!.Value, 6);
        Assert.NotNull(yield.PrecisionLift);
        Assert.True(yield.PrecisionLift > 100);            // veel scherper dan blind gokken
    }

    [Fact]
    public void Yield_GeenKandidaten_ReductieGeklemdOpBlindeTelling_GeenOneindig()
    {
        var yield = HypothesisYield.Measure(10, []);
        Assert.Equal(45, yield.BlindPairCount);
        Assert.Equal(0, yield.HypothesisPairCount);
        Assert.Equal(45, yield.ReductionFactor); // deel-door-1, niet oneindig
        Assert.Null(yield.Precision);
    }

    // ── Koppeling hypothese → fase-2-promotie-poort ────────────────────────────

    [Fact]
    public void Coupling_CardCard_PositiefVerdict_ZonderSteun_LandtInColdStart()
    {
        var hyp = HypothesisEngine.Generate([
            Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("y", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
        ]).Single();

        var signals = HypothesisPromotion.ToSignals(hyp, llmVerdictInteracts: true);
        var result = InteractionPromotionGate.Evaluate(signals);

        // Structureel + LLM samen dragen nooit een stille promotie — cold-start.
        Assert.Equal(InteractionGateOutcome.ModelHypothesizedUnruled, result.Outcome);
        Assert.Equal(InteractionStatus.ModelHypothesizedUnruled, result.Status);
    }

    [Fact]
    public void Coupling_OnafhankelijkeSteun_Promoveert()
    {
        var hyp = HypothesisEngine.Generate([
            Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("y", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
        ]).Single();

        var signals = HypothesisPromotion.ToSignals(hyp, llmVerdictInteracts: true, lexicalSupport: true);
        var result = InteractionPromotionGate.Evaluate(signals);
        Assert.Equal(InteractionGateOutcome.Promoted, result.Outcome);
    }

    [Fact]
    public void Coupling_NegatiefVerdict_SoftReject_ZonderTombstone()
    {
        var hyp = HypothesisEngine.Generate([
            Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("y", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
        ]).Single();

        var signals = HypothesisPromotion.ToSignals(hyp, llmVerdictInteracts: false);
        var result = InteractionPromotionGate.Evaluate(signals);
        Assert.Equal(InteractionGateOutcome.Rejected, result.Outcome);
        Assert.False(result.WritesTombstone); // een losstaand LLM-nee sluit de sleutel niet permanent
    }

    [Fact]
    public void Coupling_LevendeTombstone_BlokkeertStilHeropenen()
    {
        var hyp = HypothesisEngine.Generate([
            Card("x", ["fury"], (MechanicPredicateKinds.TriggersOn, "exhaust")),
            Card("y", ["fury"], (MechanicPredicateKinds.Prevents, "exhaust")),
        ]).Single();

        var signals = HypothesisPromotion.ToSignals(hyp, llmVerdictInteracts: true, hasBlockingTombstone: true);
        Assert.Equal(InteractionGateOutcome.Rejected, InteractionPromotionGate.Evaluate(signals).Outcome);
    }

    [Fact]
    public void Coupling_MechanicNiveau_GeenColdStart_MaarKandidaat()
    {
        // Mechanic-holders (geen card×card): een positief verdict zonder steun valt
        // NIET in cold-start (dat is voor emergente card×card) maar in kandidaat.
        var m1 = new PredicateHolder("mechanic:Fury", EntityType.Mechanic, [],
            [new(MechanicPredicateKinds.TriggersOn, "exhaust")]);
        var m2 = new PredicateHolder("mechanic:Accelerate", EntityType.Mechanic, [],
            [new(MechanicPredicateKinds.Prevents, "exhaust")]);
        var hyp = HypothesisEngine.Generate([m1, m2]).Single();
        Assert.False(hyp.IsEmergentCardCardPair);

        var result = InteractionPromotionGate.Evaluate(
            HypothesisPromotion.ToSignals(hyp, llmVerdictInteracts: true));
        Assert.Equal(InteractionGateOutcome.Candidate, result.Outcome);
    }

    // ── Residueel embedding-kanaal (begrensd) ──────────────────────────────────

    [Fact]
    public void Residual_RespecteertVloerKEnBudget()
    {
        var neighborhoods = new List<CardNeighborhood>
        {
            new("card:a", [new("card:b", 0.95), new("card:c", 0.90), new("card:d", 0.70)]),
            new("card:b", [new("card:a", 0.95)]),
        };
        var budget = new ResidualChannelBudget(CosineFloor: 0.82, PerCardNeighbors: 5, MaxCandidates: 50);
        var res = ResidualInteractionChannel.Select(neighborhoods, budget: budget);

        // a-d (0.70) valt onder de vloer; a-b dubbel gezien maar één ongeordend paar.
        Assert.Equal(2, res.Count);
        Assert.Contains(res, r => r.UnorderedPairKey == "card:a|card:b" && r.Cosine == 0.95);
        Assert.Contains(res, r => r.UnorderedPairKey == "card:a|card:c");
        Assert.DoesNotContain(res, r => r.UnorderedPairKey.Contains("card:d"));
        Assert.Equal("residual-embedding", ResidualCandidate.Channel);
    }

    [Fact]
    public void Residual_SluitStructureelGedekteParenUit()
    {
        var neighborhoods = new List<CardNeighborhood>
        {
            new("card:a", [new("card:b", 0.99)]),
        };
        var covered = new HashSet<string> { "card:a|card:b" };
        Assert.Empty(ResidualInteractionChannel.Select(neighborhoods, covered));
    }

    [Fact]
    public void Residual_BlijftOnderBudget_GeenN2Explosie()
    {
        // 200 kaarten, elk 3 buren boven de vloer → 600 kandidaat-zichtingen, maar het
        // kanaal is HARD begrensd op MaxCandidates ongeacht de kaartaantallen.
        var neighborhoods = new List<CardNeighborhood>();
        for (var i = 0; i < 200; i++)
            neighborhoods.Add(new($"card:{i}",
                [new($"card:{(i + 1) % 200}", 0.99), new($"card:{(i + 2) % 200}", 0.98),
                 new($"card:{(i + 3) % 200}", 0.97)]));
        var budget = new ResidualChannelBudget(CosineFloor: 0.82, PerCardNeighbors: 5, MaxCandidates: 25);
        var res = ResidualInteractionChannel.Select(neighborhoods, budget: budget);
        Assert.True(res.Count <= 25);
        // Deterministisch op cosine aflopend.
        for (var i = 1; i < res.Count; i++)
            Assert.True(res[i - 1].Cosine >= res[i].Cosine);
    }
}
