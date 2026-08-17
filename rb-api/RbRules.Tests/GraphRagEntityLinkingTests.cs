using RbRules.Domain;
using RbRules.Domain.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase 4 (#228): entity-linking — mention-detectie (longest-match + fuzzy),
/// de disambiguatie-scoring en vooral de GRAAF-TRUC (co-mention-coherentie breekt
/// homoniemen). Puur en getest; de embedding-cosine en graaf-adjacency komen als
/// functies binnen.</summary>
public class GraphRagEntityLinkingTests
{
    private static Gazetteer BuildGazetteer() => Gazetteer.Build(
    [
        new(BrainRef.Mechanic("Empowered"), "Empowered", []),      // de Status/mechanic
        new(BrainRef.Card("emp-card-001"), "Empowered", []),        // homoniem: een kaart
        new(BrainRef.Concept("showdown"), "Showdown", ["showdowns"]),
        new(BrainRef.Mechanic("Might"), "Might", []),
        new(BrainRef.Mechanic("Death Knell"), "Death Knell", ["deathknell"]),
    ]);

    // ── Mention-detectie ──

    [Fact]
    public void Detect_LongestMatch_MeerwoordigeNaamWintVanLosWoord()
    {
        var mentions = MentionDetector.Detect("does Death Knell trigger", BuildGazetteer());
        var dk = mentions.Single(m => m.Surface.Contains("Death", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, dk.TokenLength);
        Assert.Equal(BrainRef.Mechanic("Death Knell"), dk.Candidates[0].Ref);
    }

    [Fact]
    public void Detect_Homoniem_LevertBeideKandidaten()
    {
        var mentions = MentionDetector.Detect("is Empowered good", BuildGazetteer());
        var m = mentions.Single(x => x.Surface.Equals("Empowered", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, m.Candidates.Count); // mechanic + card, beide exact
        Assert.All(m.Candidates, c => Assert.Equal(1.0, c.LexicalScore));
    }

    [Fact]
    public void Detect_Fuzzy_TolereertSpelfout()
    {
        // "showdow" (ontbrekende n) ligt trigram-dichtbij "showdown".
        var mentions = MentionDetector.Detect("during a showdow", BuildGazetteer());
        Assert.Contains(mentions, m => m.Candidates.Any(c => c.Ref == BrainRef.Concept("showdown")));
    }

    [Fact]
    public void Detect_GeenTreffer_GeenMention() =>
        Assert.Empty(MentionDetector.Detect("hello world foobar", BuildGazetteer()));

    // ── Disambiguatie: de graaf-truc ──

    [Fact]
    public void Link_CoMentionCoherentie_BreektHomoniemRichtingVerbondenKandidaat()
    {
        // "Werkt Empowered met Might in een showdown?" — Empowered is homoniem
        // (mechanic vs. kaart). De mechanic is via een edge verbonden met de
        // Window Showdown; de kaart met niets. De coherentie moet de mechanic
        // laten winnen, ondanks gelijke lexicale score.
        var mentions = MentionDetector.Detect(
            "Werkt Empowered met Might in een showdown?", BuildGazetteer());

        // Alleen (mechanic:Empowered — concept:showdown) is verbonden.
        bool Connected(BrainRef a, BrainRef b) =>
            (a == BrainRef.Mechanic("Empowered") && b == BrainRef.Concept("showdown")) ||
            (b == BrainRef.Mechanic("Empowered") && a == BrainRef.Concept("showdown"));

        var decisions = EntityLinker.Link(mentions, edgeConnected: Connected);
        var empowered = decisions.Single(d => d.Surface.Equals("Empowered", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BrainRef.Mechanic("Empowered"), empowered.Chosen);
        Assert.Contains("versloeg", empowered.Memo); // provenance noemt de verslagen kandidaat
    }

    [Fact]
    public void Link_ProduceertLinkDecisionProvenance()
    {
        var mentions = MentionDetector.Detect("is Might strong", BuildGazetteer());
        var decisions = EntityLinker.Link(mentions);
        var might = decisions.Single(d => d.Surface.Equals("Might", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BrainRef.Mechanic("Might"), might.Chosen);
        Assert.NotEmpty(might.Ranked);
        Assert.False(string.IsNullOrWhiteSpace(might.Memo));
    }

    [Fact]
    public void Link_OnderDrempel_BlijftOngelinkt_GeenGok()
    {
        // Zwakke fuzzy-kandidaat (lexicaal 0.5), geen cos/coherentie → totaal 0.15 <
        // 0.20 → Chosen null (liever niet linken dan fout linken).
        var mention = new EntityMention("blurst", 0, 1,
            [new CandidateNode(new GazetteerEntry(BrainRef.Mechanic("Burst"), "Burst", []), 0.5)]);
        var decisions = EntityLinker.Link([mention]);
        Assert.Null(decisions[0].Chosen);
        Assert.Contains("ongelinkt", decisions[0].Memo);
    }

    [Fact]
    public void Anchors_LevertAlleenGelinkteRefs()
    {
        var mentions = MentionDetector.Detect(
            "Werkt Empowered met Might in een showdown?", BuildGazetteer());
        bool Connected(BrainRef a, BrainRef b) =>
            (a == BrainRef.Mechanic("Empowered") && b == BrainRef.Concept("showdown")) ||
            (b == BrainRef.Mechanic("Empowered") && a == BrainRef.Concept("showdown"));
        var anchors = EntityLinker.Anchors(EntityLinker.Link(mentions, edgeConnected: Connected));
        Assert.Contains(BrainRef.Mechanic("Empowered"), anchors);
        Assert.Contains(BrainRef.Mechanic("Might"), anchors);
        Assert.Contains(BrainRef.Concept("showdown"), anchors);
        Assert.DoesNotContain(BrainRef.Card("emp-card-001"), anchors);
    }
}
