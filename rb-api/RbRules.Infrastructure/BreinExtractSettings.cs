using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>De beheerde model-alias voor tool-forced brein-extractie (#325).
/// De alias is het enige dat rb-api kent; provider en concreet model worden
/// uitsluitend door rb-ai's gesloten registry bepaald.</summary>
public sealed record BreinExtractSettings(string ModelAlias)
{
    public static BreinExtractSettings Default { get; } =
        new(BreinExtractModelAliases.Sonnet);

    public BreinExtractSettings WithOverrides(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue(SettingKeys.BreinExtractModel, out var alias)
        && BreinExtractModelAliases.IsValid(alias)
            ? this with { ModelAlias = alias }
            : this;
}
