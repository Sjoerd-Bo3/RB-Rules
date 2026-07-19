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

    [Fact]
    public void WithinTierTemporal_GelijkeTier_OnbekendeDatum_StabielOpRef_NietInvoervolgorde()
    {
        // Twee same-tier passages met onbekende (null) datum: Precedence.Compare == 0.
        // Zonder stabiele ref-tie-break zou de winnaar puur van de argumentvolgorde
        // afhangen en de SUPERSEDES-richting per run flippen — precies de flip-flop die
        // het project bevecht (een TrustDecision is een persistente first-class knoop).
        var a = new TrustParty("ruling:a", KnowledgeTier.VerifiedRuling, null, DateTimeOffset.UtcNow);
        var b = new TrustParty("ruling:b", KnowledgeTier.VerifiedRuling, null, DateTimeOffset.UtcNow);

        var ab = TrustConflictResolver.Resolve(a, b, TrustConflictContext.WithinTierTemporal);
        var ba = TrustConflictResolver.Resolve(b, a, TrustConflictContext.WithinTierTemporal);
        Assert.Equal("ruling:a", ab.WinnerRef);
        Assert.Equal("ruling:a", ba.WinnerRef); // winnaar/richting hangen niet van invoervolgorde af
        Assert.Equal("ruling:b", ab.LoserRef);
        Assert.Equal("ruling:b", ba.LoserRef);
    }

    [Fact]
    public void WithinTierTemporal_GelijkeDatum_StabielOpRef_NietInvoervolgorde()
    {
        // Twee rulings op dezelfde dag uitgevaardigd: ook cmp == 0, ook stabiel op ref.
        var d = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);
        var a = new TrustParty("ruling:a", KnowledgeTier.VerifiedRuling, d, DateTimeOffset.UtcNow);
        var b = new TrustParty("ruling:b", KnowledgeTier.VerifiedRuling, d, DateTimeOffset.UtcNow);

        var ab = TrustConflictResolver.Resolve(a, b, TrustConflictContext.WithinTierTemporal);
        var ba = TrustConflictResolver.Resolve(b, a, TrustConflictContext.WithinTierTemporal);
        Assert.Equal("ruling:a", ab.WinnerRef);
        Assert.Equal("ruling:a", ba.WinnerRef);
    }

    [Fact]
    public void CrossTier_GelijkeTier_OnbekendeDatum_StabielOpRef_NietInvoervolgorde()
    {
        // De equal-tier fallback in ResolveCrossTier moet net zo stabiel zijn.
        var a = new TrustParty("section:a", KnowledgeTier.Official, null, DateTimeOffset.UtcNow);
        var b = new TrustParty("section:b", KnowledgeTier.Official, null, DateTimeOffset.UtcNow);

        var ab = TrustConflictResolver.Resolve(a, b, TrustConflictContext.CrossTier);
        var ba = TrustConflictResolver.Resolve(b, a, TrustConflictContext.CrossTier);
        Assert.Equal("section:a", ab.WinnerRef);
        Assert.Equal("section:a", ba.WinnerRef);
    }

    [Fact]
    public void CrossTier_NietOfficieleWinnaar_GeenVeto_VerliezerSuperseded()
    {
        // Een Primer (0.65, NIET officieel) spreekt een Community-claim (0.45) tegen.
        // De Primer wint op de gezags-as, maar zonder officiële dekking mag er GEEN
        // veto zijn: de verliezer wordt superseded, niet vals contradicted_by_official
        // (beslissing #229 — routing keyt op "is er officiële dekking?").
        var primer = new TrustParty("primer:combat", KnowledgeTier.Primer, null, DateTimeOffset.UtcNow);
        var claim = new TrustParty("claim:42", KnowledgeTier.Community, null, DateTimeOffset.UtcNow);

        var d = TrustConflictResolver.Resolve(primer, claim, TrustConflictContext.CrossTier);
        Assert.Equal("primer:combat", d.WinnerRef);
        Assert.Equal("claim:42", d.LoserRef);
        Assert.Equal(TrustDisposition.Superseded, d.LoserDisposition);
        Assert.DoesNotContain("veto", d.Memo);
        Assert.DoesNotContain("contradicted_by_official", d.Memo);
    }

    [Fact]
    public void CrossTier_CommunityVanMeta_GeenValsOfficieelVeto()
    {
        // Ook Community-vs-Meta: winnaar (Community) is niet officieel → geen veto.
        var claim = new TrustParty("claim:7", KnowledgeTier.Community, null, DateTimeOffset.UtcNow);
        var meta = new TrustParty("meta:tierlist", KnowledgeTier.Meta, null, DateTimeOffset.UtcNow);

        var d = TrustConflictResolver.Resolve(claim, meta, TrustConflictContext.CrossTier);
        Assert.Equal("claim:7", d.WinnerRef);
        Assert.Equal(TrustDisposition.Superseded, d.LoserDisposition);
        Assert.NotEqual(TrustDisposition.ContradictedByOfficial, d.LoserDisposition);
    }

    [Fact]
    public void CrossTier_GeverifieerdeRuling_IsOfficieel_HoudtVeto()
    {
        // Een geverifieerde ruling telt WEL als officiële dekking (Authority.IsOfficial):
        // het veto blijft geldig, verliezer → contradicted_by_official.
        var ruling = new TrustParty("ruling:9", KnowledgeTier.VerifiedRuling, null, DateTimeOffset.UtcNow);
        var claim = new TrustParty("claim:3", KnowledgeTier.Community, null, DateTimeOffset.UtcNow);

        var d = TrustConflictResolver.Resolve(ruling, claim, TrustConflictContext.CrossTier);
        Assert.Equal("ruling:9", d.WinnerRef);
        Assert.Equal(TrustDisposition.ContradictedByOfficial, d.LoserDisposition);
        Assert.Contains("veto", d.Memo);
    }
}
