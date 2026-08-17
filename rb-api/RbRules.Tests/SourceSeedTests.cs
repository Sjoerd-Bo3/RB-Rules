using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Register-invarianten: de kennislagen (docs/KNOWLEDGE.md) zijn
/// alleen te handhaven als de seed zelf consistent blijft — vooral het harde
/// principe dat community-bronnen nooit trust 1 krijgen.</summary>
public class SourceSeedTests
{
    [Fact]
    public void Ids_AreUnique()
    {
        var ids = SourceSeed.Defaults.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void TrustTier1_IsExclusivelyOfficial()
    {
        // Kennislagen-principe: alleen officiële Riot-bronnen zijn normatief.
        foreach (var s in SourceSeed.Defaults.Where(s => s.TrustTier == 1))
            Assert.Equal("official", s.Type);
        foreach (var s in SourceSeed.Defaults.Where(s => s.Type != "official"))
            Assert.True(s.TrustTier >= 2, $"{s.Id}: {s.Type}-bron mag geen trust 1 hebben");
    }

    [Fact]
    public void CommunitySources_HaveTrustTier3OrLower()
    {
        foreach (var s in SourceSeed.Defaults.Where(s => s.Type == "community"))
            Assert.True(s.TrustTier >= 3, $"{s.Id}: community is maximaal trust 3");
    }

    [Fact]
    public void ParserAndCadence_AreKnownValues()
    {
        // De ingest kent alleen deze parsers/cadences; een typefout in de seed
        // zou pas bij een live scan opvallen.
        foreach (var s in SourceSeed.Defaults)
        {
            Assert.Contains(s.Parser, new[] { "html", "pdf" });
            Assert.Contains(s.Cadence, new[] { "daily", "weekly" });
        }
    }

    [Fact]
    public void OfficialErrataSources_FollowIdConvention()
    {
        // BanErrataSyncService selecteert errata-bronnen op Id-conventie
        // (bevat "errata") binnen trust 1 — een officiële errata-bron zonder
        // die Id-vorm zou stilletjes niet gestructureerd worden.
        foreach (var s in SourceSeed.Defaults.Where(s =>
                     s.TrustTier == 1 &&
                     s.Name.Contains("errata", StringComparison.OrdinalIgnoreCase)))
            Assert.Contains("errata", s.Id);
    }

    [Fact]
    public void Urls_AreAbsoluteHttps()
    {
        foreach (var s in SourceSeed.Defaults)
        {
            Assert.True(Uri.TryCreate(s.Url, UriKind.Absolute, out var uri), $"{s.Id}: geen absolute URL");
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        }
    }
}
