namespace RbRules.Domain;

/// <summary>Schaduwtarief per model (#328): de API-prijs per miljoen tokens die
/// zou gelden als we per token betaalden (we betalen een abonnement — elk bedrag
/// hieruit is een SCHADUWKOST, geen factuur). Rijen zijn append-only met een
/// ingangsdatum: prijzen wijzigen, en een bedrag zonder tariefdatum is later
/// niet te herleiden. Beheerbaar zonder deploy via /api/admin/tariffs; de seed
/// (<see cref="AiTariffSeed"/>) zet alleen de startwaarden neer.</summary>
public class AiTariff
{
    public long Id { get; set; }
    /// <summary>Model-ID zoals hij op <see cref="AiUsageEvent.Model"/> staat
    /// (bv. "claude-sonnet-4-6"). Matching is exact — een onbekend model levert
    /// een metering-rij zónder tariefversie op, nooit een gok.</summary>
    public required string Model { get; set; }
    /// <summary>USD per miljoen input-tokens (cache-tokens tellen in onze
    /// meting mee als input — de som is dus een bovengrens-benadering).</summary>
    public decimal InputUsdPerMTok { get; set; }
    public decimal OutputUsdPerMTok { get; set; }
    /// <summary>Vanaf wanneer dit tarief geldt; bij meerdere rijen voor één
    /// model wint de recentste ingangsdatum ≤ nu.</summary>
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Eén gemeterde AI-gebeurtenis (#328): wie (gebruiker of platform-job)
/// veroorzaakte hoeveel tokens op welk model, en tegen welke tariefversie. Dit is
/// het kosten-grootboek voor het beheer-paneel; ask_metric blijft de duur-/
/// quota-statistiek per vraag. PRIVACY: uitsluitend maten en verwijzingen —
/// nooit vraaginhoud (dezelfde scheiding als AskTrace: inhoud achter de
/// admin-poort, aggregaten in het paneel).</summary>
public class AiUsageEvent
{
    public const string OriginUser = "user";
    public const string OriginPlatform = "platform";

    public long Id { get; set; }
    /// <summary>"user" (door een bezoeker veroorzaakt) of "platform"
    /// (mining/audit/primer/…): de hoofdsplitsing van het paneel.</summary>
    public required string Origin { get; set; }
    /// <summary>Het account dat de call veroorzaakte; alleen bij Origin
    /// "user". Sinds de login-poort (#328) is dit op nieuwe rijen gevuld.</summary>
    public long? UserId { get; set; }
    /// <summary>Wat de call veroorzaakte: "ask" (user) of de job-soort
    /// ("mining", "audit", "primer", …) bij platform.</summary>
    public required string Kind { get; set; }
    /// <summary>Model-ID van de call (bv. "claude-sonnet-4-6"). Op het
    /// ask-pad afgeleid uit het antwoordpad via <see cref="AskPathModels"/>;
    /// platform-jobs geven hun eigen (beheerde) model-ID door.</summary>
    public required string Model { get; set; }
    /// <summary>Echte tellingen (#121-vorm: input incl. cache-tokens);
    /// null = geen usage ontvangen — onbekend is niet 0.</summary>
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public bool Ok { get; set; } = true;
    /// <summary>De <see cref="AiTariff.Id"/> die gold op het moment van
    /// schrijven — daarmee is het schaduwbedrag later exact reproduceerbaar
    /// als rij × tarief, óók nadat de prijzen zijn bijgewerkt. Null = geen
    /// tarief bekend voor dit model (het paneel toont dan "geen tarief",
    /// nooit een verzonnen bedrag).</summary>
    public long? TariffVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Pure schaduwkost-berekening: tokens × tarief, op één plek zodat
/// paneel én tests exact dezelfde som maken. Decimal, want geld.</summary>
public static class ShadowCost
{
    public const decimal TokensPerMTok = 1_000_000m;

    /// <summary>USD voor één (deel)meting; null wanneer er geen tokens of geen
    /// tarief zijn — onbekend blijft onbekend, geen stille nul.</summary>
    public static decimal? ComputeUsd(
        long? inputTokens, long? outputTokens,
        decimal? inputUsdPerMTok, decimal? outputUsdPerMTok)
    {
        if (inputUsdPerMTok is null || outputUsdPerMTok is null) return null;
        if (inputTokens is null && outputTokens is null) return null;
        return (inputTokens ?? 0) * inputUsdPerMTok.Value / TokensPerMTok
            + (outputTokens ?? 0) * outputUsdPerMTok.Value / TokensPerMTok;
    }
}

/// <summary>Antwoordpad → model-ID op het /ask-pad. SPIEGELT de MODEL-map in
/// rb-ai/src/ai.ts (cheap/hard/agentic zijn rb-ai's taken; rb-ai kiest daar het
/// model en stuurt het niet terug). Wijzigt rb-ai zijn map, werk deze dan mee
/// bij — de tariefversie op de rij houdt oude bedragen intussen reproduceerbaar
/// zoals ze destijds geboekt zijn.</summary>
public static class AskPathModels
{
    public static string Resolve(string? path) => path switch
    {
        "hard" => "claude-opus-4-8",
        "agentic" => "claude-sonnet-4-6",
        _ => "claude-sonnet-4-6", // cheap en onbekend: het default-model
    };
}

/// <summary>Startwaarden voor de tarieventabel — de publieke API-prijzen per
/// miljoen tokens (juli 2026). Alleen geseed wanneer de tabel leeg is; daarna
/// is /api/admin/tariffs (append-only) de bron van waarheid.</summary>
public static class AiTariffSeed
{
    public static readonly DateTimeOffset SeedEffectiveFrom =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<AiTariff> Defaults =>
    [
        new() { Model = "claude-sonnet-4-6", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m, EffectiveFrom = SeedEffectiveFrom },
        new() { Model = "claude-sonnet-4-6[1m]", InputUsdPerMTok = 3m, OutputUsdPerMTok = 15m, EffectiveFrom = SeedEffectiveFrom },
        new() { Model = "claude-opus-4-8", InputUsdPerMTok = 5m, OutputUsdPerMTok = 25m, EffectiveFrom = SeedEffectiveFrom },
        new() { Model = "claude-fable-5", InputUsdPerMTok = 10m, OutputUsdPerMTok = 50m, EffectiveFrom = SeedEffectiveFrom },
        new() { Model = "claude-fable-5[1m]", InputUsdPerMTok = 10m, OutputUsdPerMTok = 50m, EffectiveFrom = SeedEffectiveFrom },
    ];
}
