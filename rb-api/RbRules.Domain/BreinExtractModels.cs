namespace RbRules.Domain;

/// <summary>De GESLOTEN aliasmap voor het brein-extractie-model (#323). rb-api
/// stuurt de ALIAS naar rb-ai (dat zijn eigen kopie van deze map heeft en een
/// onbekende alias met 400 weigert — drift tussen de twee kopieën komt dus
/// luidruchtig terug, nooit stil); deze kant levert de keuzelijst voor de
/// beheerde instelling (<see cref="SettingKeys.BreinExtractModel"/>) en het
/// model-ID voor de rij-provenance (<see cref="Interaction.ExtractModel"/>,
/// <see cref="MiningRun.LlmModel"/>). Beide kopieën hebben een literal-test
/// (#286-les: een assertie tegen de constante die ze bewaakt schuift mee).
///
/// De <c>-1m</c>-aliassen kiezen de 1M-contextvariant (Agent SDK-notatie
/// <c>model[1m]</c>) — relevant zodra K richting 250 gaat. Of het abonnement
/// die variant draagt is niet gegarandeerd: rb-ai meldt een weigering als
/// eigen reden <c>model_unavailable</c>, zodat beheer de knop ziet (alias
/// terugzetten) in plaats van een generieke storing.</summary>
public static class BreinExtractModels
{
    public const string DefaultAlias = "fable";

    /// <summary>Volgorde-stabiel: dit is ook de keuzelijst in beheer.</summary>
    public static readonly IReadOnlyList<string> Aliases =
        ["sonnet", "opus", "fable", "fable-1m", "sonnet-1m"];

    /// <summary>Alias → rb-ai-model-ID, of null bij een onbekende alias (de
    /// aanroeper weigert dan; nooit een vrije string doorsturen).</summary>
    public static string? ModelId(string? alias) => alias switch
    {
        "sonnet" => "claude-sonnet-4-6",
        "opus" => "claude-opus-4-8",
        "fable" => "claude-fable-5",
        "fable-1m" => "claude-fable-5[1m]",
        "sonnet-1m" => "claude-sonnet-4-6[1m]",
        _ => null,
    };

    public static bool IsAlias(string? alias) => ModelId(alias) is not null;
}
