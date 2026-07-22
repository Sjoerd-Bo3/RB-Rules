using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de interactie-steekproef-audit (#255). Zelfde
/// #254-snit als <see cref="NightlyRunSettings"/>: de omgeving is de
/// BOOTSTRAP-DEFAULT (<see cref="FromEnvironment"/>), beheer schrijft er via de
/// <c>setting</c>-tabel overheen (<see cref="WithOverrides"/>), en de service leest
/// hem op het GEBRUIKSMOMENT via <see cref="ManagedSettingsService.BreinAuditAsync"/>
/// — een bijgestelde dichtheid geldt dus meteen, zonder herstart.</summary>
/// <param name="SampleDivisor">De steekproefdichtheid: 1 op de N gepromoveerde
/// interacties gaat langs het sterkere model. 1 = alles auditen; 10 (default) =
/// een tiende van de pool.</param>
/// <param name="ModelAlias">Gesloten rb-ai-modelalias voor de audit; standaard
/// opus, onafhankelijk van brein.extract.model.</param>
public sealed record BreinAuditSettings(
    int SampleDivisor,
    string ModelAlias = BreinExtractModelAliases.Opus)
{
    /// <summary>Bereik van de knop — dezelfde 1..100 als
    /// <see cref="ManagedSettingsCatalog.ParseValue"/> voor <c>Count</c> afdwingt,
    /// zodat env en beheer nooit verschillende grenzen hanteren.</summary>
    public const int MaxSampleDivisor = 100;

    public static readonly BreinAuditSettings Default = new(10, BreinExtractModelAliases.Opus);

    public static BreinAuditSettings FromEnvironment() =>
        new(ParseDivisor(Environment.GetEnvironmentVariable("BREIN_AUDIT_SAMPLE_N")));

    /// <summary>Beheerde override (#254) over de env-basiswaarde. Ontbrekende
    /// sleutel ⇒ ongewijzigd; een onzin-waarde in de tabel valt terug op de
    /// basiswaarde (de catalogus-validatie hoort dat al tegen te houden — dit is
    /// het vangnet tegen handmatige SQL).</summary>
    public BreinAuditSettings WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var result = overrides.TryGetValue(SettingKeys.BreinAuditSampleN, out var raw)
            && int.TryParse(raw, out var n) && n is >= 1 and <= MaxSampleDivisor
            ? this with { SampleDivisor = n }
            : this;
        return overrides.TryGetValue(SettingKeys.BreinAuditModel, out var alias)
               && BreinExtractModelAliases.IsValid(alias)
            ? result with { ModelAlias = alias }
            : result;
    }

    private static int ParseDivisor(string? raw) =>
        int.TryParse(raw, out var v) && v >= 1 && v <= MaxSampleDivisor
            ? v
            : Default.SampleDivisor;
}
