namespace RbRules.Domain.GraphRag;

/// <summary>Het HARDE latency-budget per /ask (beslissing #232). Overschrijding →
/// terugval naar Local-only i.p.v. de gebruiker laten wachten op de dure
/// Path/DRIFT-kanalen.</summary>
public sealed record LatencyBudget(double HardMs)
{
    public static readonly LatencyBudget Default = new(4000);
}

/// <summary>Terugval-redenen (beslissing #232) — expliciet zodat de trace en de UI
/// kunnen tonen wáárom er gedegradeerd is (inzicht #236: geen stille state).</summary>
public static class RetrievalFallback
{
    public const string LatencyExceeded = "latency-exceeded";
    public const string GdsCold = "gds-cold";
}

/// <summary>De begrotings-poort van de retrieval (beslissing #232). Twee harde
/// regels, puur en getest:
/// <list type="bullet">
/// <item>k-shortest (Path) draait ALLEEN op een warme, vooraf-geprojecteerde,
///   gepinde GDS-named-graph (warm-up bij startup, niet per query). Koud → geen
///   Path-kanaal, degradeer.</item>
/// <item>Overschrijdt de verstreken tijd het harde budget → val terug op
///   Local-only.</item>
/// </list>
/// De orchestrator raadpleegt dit vóór de dure fasen.</summary>
public static class RetrievalGuard
{
    public static bool WithinBudget(double elapsedMs, LatencyBudget budget) =>
        elapsedMs < budget.HardMs;

    /// <summary>Mag het Path-kanaal draaien? Alleen als de GDS-graaf warm is ÉN we
    /// nog binnen budget zitten.</summary>
    public static bool CanRunPath(bool gdsWarm, double elapsedMs, LatencyBudget budget) =>
        gdsWarm && WithinBudget(elapsedMs, budget);

    /// <summary>Pas de begrotings-regels toe op een modus-keuze en geef de
    /// (eventueel gedegradeerde) modus terug plus de reden. Volgorde is bewust:
    /// budget-overschrijding degradeert alles naar Local-only; anders strip alleen
    /// het Path-kanaal als GDS koud staat.</summary>
    public static ModeSelection Apply(
        ModeSelection mode, bool gdsWarm, double elapsedMs, LatencyBudget budget,
        out string? fallbackReason)
    {
        ArgumentNullException.ThrowIfNull(mode);

        if (!WithinBudget(elapsedMs, budget))
        {
            fallbackReason = RetrievalFallback.LatencyExceeded;
            return mode.ToLocalOnly(RetrievalFallback.LatencyExceeded);
        }

        var needsPath = mode.Primary == RetrievalMode.Path || mode.UsePath;
        if (needsPath && !gdsWarm)
        {
            fallbackReason = RetrievalFallback.GdsCold;
            // Path-kanaal weg; als Path de primaire modus wás, val terug op DRIFT
            // (nog steeds getypeerd-graaf, maar zonder GDS-pathfinding).
            var primary = mode.Primary == RetrievalMode.Path ? RetrievalMode.Drift : mode.Primary;
            return mode with
            {
                Primary = primary,
                UsePath = false,
                Reason = $"{mode.Reason} → Path uit ({RetrievalFallback.GdsCold})",
            };
        }

        fallbackReason = null;
        return mode;
    }
}
