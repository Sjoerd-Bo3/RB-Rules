using RbRules.Infrastructure;
using RbRules.Infrastructure.GraphRag;

namespace RbRules.Tests;

/// <summary>Testhulp voor de beheerde instellingen-laag (#254): een
/// <see cref="ManagedSettingsService"/> ZONDER database (dbFactory null, patroon
/// <c>AskService.dbFactory</c>) met vaste basiswaarden. Zo blijven de bestaande
/// flag-tests lezen als vóór #254 — ze zeggen "deze vlag staat uit", niet "er is een
/// instellingen-tabel".</summary>
public static class TestSettings
{
    public static ManagedSettingsService Fixed(BreinRetrievalSettings brein) =>
        new(breinBase: brein, nightlyBase: NightlyRunSettings.Default);

    public static ManagedSettingsService Fixed(NightlyRunSettings nightly) =>
        new(breinBase: BreinRetrievalSettings.Disabled, nightlyBase: nightly);
}
