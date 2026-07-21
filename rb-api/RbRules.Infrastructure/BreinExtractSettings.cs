using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Instellingen voor de brein-interactie-extractie (#323): welk model
/// (alias uit de gesloten map <see cref="BreinExtractModels"/>) en hoeveel
/// kaarten per rb-ai-sessie (K). Zelfde #254-snit als
/// <see cref="BreinAuditSettings"/>: de omgeving is de BOOTSTRAP-DEFAULT
/// (<see cref="FromEnvironment"/>), beheer schrijft er via de setting-tabel
/// overheen (<see cref="WithOverrides"/>), en de mining leest hem op het
/// GEBRUIKSMOMENT (per run) via
/// <see cref="ManagedSettingsService.BreinExtractAsync"/>.</summary>
/// <param name="ModelAlias">Alias uit <see cref="BreinExtractModels.Aliases"/>.
/// Default fable — expliciete keuze van Sjoerd na de 10%-audituitslag van de
/// sonnet-baseline.</param>
/// <param name="BatchK">Kaarten per rb-ai-sessie; 1 = het losse pad van vóór
/// #323. Default 50 (idem: expliciete productkeuze).</param>
/// <param name="BaseTimeoutMs">Spiegel van rb-ai's <c>AI_EXTRACT_TIMEOUT_MS</c>
/// (zelfde env-naam, zelfde default): de basis van de keten-timeout. Beide
/// containers lezen dezelfde .env-variabele — zie de compose-file.</param>
/// <param name="PerCardTimeoutMs">Spiegel van rb-ai's
/// <c>AI_EXTRACT_PER_CARD_MS</c>: extra budget per extra kaart in de sessie.</param>
public sealed record BreinExtractSettings(
    string ModelAlias, int BatchK, int BaseTimeoutMs, int PerCardTimeoutMs)
{
    /// <summary>Bovengrens op K — LETTERLIJK 250 (een hele set in één context,
    /// expliciete productkeuze; rb-ai weigert erboven met 400 als tweede muur).</summary>
    public const int MaxBatchK = 250;

    /// <summary>Marge bóven de rb-ai-keten voor de HttpClient-kant: wachtrij
    /// (AI_QUEUE_WAIT_MS, 30 s), transport en NDJSON-afronding. De les van #281
    /// is hard: een rb-api-timeout die korter is dan de keten eronder verkleedt
    /// elke upstream-fout als "traag".</summary>
    public const int CallMarginMs = 120_000;

    public static readonly BreinExtractSettings Default =
        new(BreinExtractModels.DefaultAlias, 50, 90_000, 180_000);

    public static BreinExtractSettings FromEnvironment() => new(
        ParseAlias(Environment.GetEnvironmentVariable("BREIN_EXTRACT_MODEL")),
        ParseBatchK(Environment.GetEnvironmentVariable("BREIN_EXTRACT_BATCH_K")),
        ParseMs(Environment.GetEnvironmentVariable("AI_EXTRACT_TIMEOUT_MS"),
            Default.BaseTimeoutMs),
        ParseMs(Environment.GetEnvironmentVariable("AI_EXTRACT_PER_CARD_MS"),
            Default.PerCardTimeoutMs));

    /// <summary>Beheerde overrides (#254) over de env-basiswaarden. Onzin in de
    /// tabel valt terug op de basiswaarde (de catalogus-validatie hoort dat al
    /// tegen te houden — dit is het vangnet tegen handmatige SQL). De
    /// timeout-spiegels zijn bewust NIET beheerbaar: ze horen bij rb-ai's
    /// container-env en verzetten zonder die kant mee te verzetten zou precies
    /// de #281-val opnieuw graven.</summary>
    public BreinExtractSettings WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var result = this;
        if (overrides.TryGetValue(SettingKeys.BreinExtractModel, out var alias)
            && BreinExtractModels.IsAlias(alias))
            result = result with { ModelAlias = alias };
        if (overrides.TryGetValue(SettingKeys.BreinExtractBatchK, out var raw)
            && int.TryParse(raw, out var k) && k >= 1 && k <= MaxBatchK)
            result = result with { BatchK = k };
        return result;
    }

    /// <summary>Het rb-api-budget voor één batch-call van <paramref name="k"/>
    /// kaarten: rb-ai's eigen keten (basis + (k−1) × per-kaart, exact de
    /// formule van <c>scaledExtractTimeoutMs</c> in rb-ai) plus
    /// <see cref="CallMarginMs"/>. RUIMER dan de keten eronder, per constructie
    /// (#281-les). De heartbeat-frames houden de verbinding intussen zichtbaar
    /// levend; dit is het vangnet, niet het verwachte pad.</summary>
    public TimeSpan BatchCallTimeout(int k)
    {
        var cards = Math.Clamp(k, 1, MaxBatchK);
        return TimeSpan.FromMilliseconds(
            BaseTimeoutMs + (long)(cards - 1) * PerCardTimeoutMs + CallMarginMs);
    }

    private static string ParseAlias(string? raw) =>
        BreinExtractModels.IsAlias(raw) ? raw! : Default.ModelAlias;

    private static int ParseBatchK(string? raw) =>
        int.TryParse(raw, out var v) && v >= 1 && v <= MaxBatchK ? v : Default.BatchK;

    private static int ParseMs(string? raw, int fallback) =>
        int.TryParse(raw, out var v) && v >= 1_000 ? v : fallback;
}
