using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Antwoordgeheugen (#384) aan/uit: env <c>ASK_MEMORY_ENABLED</c> als
/// bootstrap-default, de beheerde instelling <see cref="SettingKeys.AskMemoryEnabled"/>
/// als override die direct werkt (#254). Uit = /ask leest én schrijft het geheugen
/// niet: exact het gedrag van vóór #384, geen extra query, geen extra rij.</summary>
public sealed record AskMemorySettings(bool Enabled)
{
    public static readonly AskMemorySettings Default = new(true);

    public static AskMemorySettings FromEnvironment() =>
        new(ManagedSettingsCatalog.ParseBool(Environment.GetEnvironmentVariable("ASK_MEMORY_ENABLED"))
            ?? Default.Enabled);

    public AskMemorySettings WithOverrides(IReadOnlyDictionary<string, string> overrides) =>
        overrides.TryGetValue(SettingKeys.AskMemoryEnabled, out var raw)
            && ManagedSettingsCatalog.ParseBool(raw) is { } b
            ? this with { Enabled = b }
            : this;
}
