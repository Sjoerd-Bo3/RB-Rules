using RbRules.Domain.GraphRag;

namespace RbRules.Tests;

/// <summary>Fase 5 (#229, §6) — de trust-vector-afronding: de idee-niveau cluster-
/// afleiding die de noisy-OR-corroboratie COMPLEET maakt (echo-kamer-dedup op ruwe
/// bron-metadata), en de CONTEXT-afhankelijke conflict-resolutie met de juiste tie-break-
/// richting per context (recentste-gezaghebbende vs. vroegste-detectie), elk met een
/// expliciete <see cref="TrustDecision"/>.</summary>
public class TrustConflictTests
{
    // ── Provenance-cluster (idee-niveau) ───────────────────────────────────────

    [Fact]
    public void Cluster_ThreadWintVanAuteurWintVanHost()
    {
        Assert.Equal("thread:t1", ProvenanceCluster.KeyFor(thread: "T1", author: "a", host: "h"));
        Assert.Equal("author:jax", ProvenanceCluster.KeyFor(author: "Jax", host: "reddit.com"));
        Assert.Equal("host:reddit.com", ProvenanceCluster.KeyFor(host: "Reddit.com"));
        Assert.Null(ProvenanceCluster.KeyFor());
    }

    [Fact]
    public void RawSources_ZelfdeThread_TeltAlsEenStem_EchoKamer()
    {
        // Drie posts uit dezelfde thread: gecorreleerd, tellen als één onafhankelijke stem.
        var echoed = new[]
        {
            new RawCorroborationSource("s1", Thread: "abc", Author: "a", Host: "discord", SourceStrength: 0.7, AuthorityWeight: 0.45),
            new RawCorroborationSource("s2", Thread: "abc", Author: "b", Host: "discord", SourceStrength: 0.7, AuthorityWeight: 0.45),
            new RawCorroborationSource("s3", Thread: "abc", Author: "c", Host: "discord", SourceStrength: 0.7, AuthorityWeight: 0.45),
        };
        var sources = RawCorroborationSource.ToSources(echoed);
        Assert.Equal(1, Corroboration.IndependentCount(sources));

        // Drie onafhankelijke threads: drie stemmen → hogere corroboratie.
        var independent = new[]
        {
            echoed[0],
            echoed[1] with { Thread = "def" },
            echoed[2] with { Thread = "ghi" },
        };
        var indepSources = RawCorroborationSource.ToSources(independent);
        Assert.Equal(3, Corroboration.IndependentCount(indepSources));
        Assert.True(Corroboration.NoisyOr(indepSources) > Corroboration.NoisyOr(sources));
    }

    [Fact]
    public void RawSources_GeenMetadata_TeltElkAlsEigenStem()
    {
        // Zonder thread/auteur/host valt de sleutel terug op de bron-id — elke bron
        // telt als één onafhankelijke stem, niet allemaal samen in één leeg cluster.
        var raw = new[]
        {
            new RawCorroborationSource("s1", null, null, null, 0.6, 0.45),
            new RawCorroborationSource("s2", null, null, null, 0.6, 0.45),
        };
        Assert.Equal(2, Corroboration.IndependentCount(RawCorroborationSource.ToSources(raw)));
    }

    // ── Context-afhankelijke conflict-resolutie ────────────────────────────────

    [Fact]
    public void CrossTier_OfficieelWintMetVeto_CommunityContradicted()
    {
        var official = new TrustParty("section:core/7.4", KnowledgeTier.Official, null, DateTimeOffset.UtcNow);
        var claim = new TrustParty("claim:42", KnowledgeTier.Community, null, DateTimeOffset.UtcNow);

        var d = TrustConflictResolver.Resolve(claim, official, TrustConflictContext.CrossTier);
        Assert.Equal("section:core/7.4", d.WinnerRef);
        Assert.Equal("claim:42", d.LoserRef);
        Assert.Equal(TrustDisposition.ContradictedByOfficial, d.LoserDisposition);
        Assert.Contains("veto", d.Memo);
    }

    [Fact]
    public void WithinTierTemporal_RecentsteWint_OudeGesuperseerd()
    {
        var oud = new TrustParty("ruling:old", KnowledgeTier.VerifiedRuling,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow);
        var nieuw = new TrustParty("ruling:new", KnowledgeTier.VerifiedRuling,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow);

        var d = TrustConflictResolver.Resolve(oud, nieuw, TrustConflictContext.WithinTierTemporal);
        Assert.Equal("ruling:new", d.WinnerRef);       // recentste-gezaghebbende (#168-richting)
        Assert.Equal(TrustDisposition.Superseded, d.LoserDisposition);
    }

    [Fact]
    public void DetectionConflict_VroegsteWint_OmgekeerdeRichting_LatereAliasOf()
    {
        // De detectie-as gebruikt de OMGEKEERDE tie-break: de vroegst-gedetecteerde
        // canonieke ID wint, ook al is de andere "nieuwer" (#150/#175-les).
        var vroeg = new TrustParty("entity:1", KnowledgeTier.Community, null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var laat = new TrustParty("entity:2", KnowledgeTier.Community, null,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var d = TrustConflictResolver.Resolve(laat, vroeg, TrustConflictContext.DetectionConflict);
        Assert.Equal("entity:1", d.WinnerRef);         // vroegste detectie wint
        Assert.Equal("entity:2", d.LoserRef);
        Assert.Equal(TrustDisposition.AliasOf, d.LoserDisposition);
    }

    [Fact]
    public void DetectionConflict_GelijkeTijd_StabielOpRef_NietInvoervolgorde()
    {
        var t = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var a = new TrustParty("entity:a", KnowledgeTier.Community, null, t);
        var b = new TrustParty("entity:b", KnowledgeTier.Community, null, t);

        var ab = TrustConflictResolver.Resolve(a, b, TrustConflictContext.DetectionConflict);
        var ba = TrustConflictResolver.Resolve(b, a, TrustConflictContext.DetectionConflict);
        Assert.Equal("entity:a", ab.WinnerRef);
        Assert.Equal("entity:a", ba.WinnerRef); // uitkomst hangt niet van invoervolgorde af
    }
}
