namespace RbRules.Domain;

/// <summary>Gerichte model-upgrade-invalidatie MÉT kostengate (fase 6, #230, §6 +
/// BESLISSING #232) — puur, €0, geen LLM. Bij een model-bump wordt NIET de hele graaf
/// N²-her-gemined; alléén de PUUR-LLM-ONGESTEUNDE feiten (gegenereerd door het oude
/// model, zónder menselijke goedkeuring én zónder onafhankelijke corroboratie) komen
/// in aanmerking — die hingen aan het model-oordeel alléén. Feiten met menselijke
/// goedkeuring of onafhankelijke steun blijven staan. De selectie is INCREMENTEEL met
/// een BUDGETPLAFOND op het abonnement-token: per cyclus een begrensde batch, de rest
/// als backlog. Dit spiegelt de §6-Cypher één-op-één in testbare .NET-logica.</summary>
public static class ModelUpgradeInvalidation
{
    /// <summary>Provenance-samenvatting van één afgeleid feit — precies de assen die
    /// de §6-Cypher raadpleegt: welk model het aandroeg, of het menselijk geverifieerd
    /// is (VERIFIED_BY {verifier:'human'}), en of het onafhankelijk gecorroboreerd is
    /// (CORROBORATED_BY een tweede bron).</summary>
    public sealed record FactProvenance(
        string SubjectRef,
        string FactKind,
        string? MinedModel,
        bool HumanVerified,
        bool IndependentlyCorroborated);

    /// <summary>Het budgetplafond op de schaduw-mine (BESLISSING #232): een harde
    /// grens op het aantal feiten én op de geschatte tokens (abonnement-token). De
    /// laagste van de twee begrenst de batch — nooit een blinde volledige re-mine.</summary>
    public sealed record Budget(int MaxFacts, long MaxTokens, int EstTokensPerFact)
    {
        public static Budget Default => new(MaxFacts: 200, MaxTokens: 400_000, EstTokensPerFact: 1_500);
    }

    /// <summary>Het schaduw-mine-plan: welke feiten deze cyclus her-gemined worden
    /// (<see cref="Selected"/>) en welke naar de volgende cyclus doorschuiven
    /// (<see cref="Deferred"/>). <see cref="Skipped"/> zijn de feiten die BEWUST blijven
    /// staan (menselijk/onafhankelijk gesteund, of van een ander model).</summary>
    public sealed record Plan(
        IReadOnlyList<FactProvenance> Selected,
        IReadOnlyList<FactProvenance> Deferred,
        int Skipped,
        long EstimatedTokens)
    {
        public int TotalCandidates => Selected.Count + Deferred.Count;
        public bool HasBacklog => Deferred.Count > 0;

        public string Summary =>
            $"Schaduw-mine: {Selected.Count} feit(en) geselecteerd (~{EstimatedTokens} tokens), " +
            $"{Deferred.Count} in backlog, {Skipped} bewust behouden (gesteund/ander model).";
    }

    /// <summary>Stel het plan samen voor een bump van <paramref name="oldModel"/> naar
    /// een nieuw model. Kandidaten = feiten van het oude model zonder menselijke
    /// goedkeuring én zonder onafhankelijke corroboratie. De batch respecteert
    /// <paramref name="budget"/> (min van feit- en token-plafond); de volgorde is
    /// stabiel (invoervolgorde) zodat opeenvolgende cycli deterministisch de backlog
    /// afwerken.</summary>
    public static Plan Plan_(
        IEnumerable<FactProvenance> facts,
        string oldModel,
        Budget budget)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(oldModel);
        ArgumentNullException.ThrowIfNull(budget);

        var candidates = new List<FactProvenance>();
        var skipped = 0;
        foreach (var f in facts)
        {
            var fromOldModel = string.Equals(f.MinedModel, oldModel, StringComparison.Ordinal);
            var unsupported = !f.HumanVerified && !f.IndependentlyCorroborated;
            if (fromOldModel && unsupported)
                candidates.Add(f);
            else
                skipped++;
        }

        var perFact = Math.Max(1, budget.EstTokensPerFact);
        var tokenCap = budget.MaxTokens <= 0 ? 0 : (int)Math.Min(int.MaxValue, budget.MaxTokens / perFact);
        var cap = Math.Max(0, Math.Min(budget.MaxFacts, tokenCap));

        var selected = candidates.Take(cap).ToList();
        var deferred = candidates.Skip(cap).ToList();
        var estTokens = (long)selected.Count * perFact;

        return new Plan(selected, deferred, skipped, estTokens);
    }
}
