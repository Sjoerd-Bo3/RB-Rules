namespace RbRules.Domain;

/// <summary>De errata-mid-set-flow (fase 6, #230, §6) als PURE plan: een
/// <em>Erratum</em> dat een keyword/ruling erratert produceert (a) een
/// SUPERSEDES-deprecatie van de betrokken ruling — die BLIJFT bestaan
/// (kaartdossier-historie, oude toernooi-uitspraken reconstrueren) maar wordt
/// <see cref="LifecycleState.Superseded"/> — en (b) invalidatie van de afhankelijke
/// feiten en eval-cases die aan hetzelfde onderwerp hangen (koppeling aan het
/// eval-scaffold #231/#235: forbidden_claim-verval). Geen nieuwe heuristiek: de
/// aanroeper levert de afhankelijken op via dezelfde onderwerp-mapper als
/// <see cref="KnowledgeRecheck"/>. Geen IO; de service materialiseert het plan.</summary>
public static class ErrataLifecycle
{
    /// <summary>Een afhankelijk feit of eval-case dat aan het geërratete onderwerp
    /// hangt (ruling, GOVERNED_BY-sectie, of een eval-case die de oude bewoording
    /// toetst). <paramref name="Ref"/> is de BrainRef, <paramref name="FactKind"/>
    /// het soort (ruling | assertion | relation | eval_case | forbidden_claim).</summary>
    public sealed record Dependent(string Ref, string FactKind);

    /// <summary>Eén invalidatie-actie: dit feit naar <see cref="LifecycleState.Stale"/>
    /// (her-verificatie vereist), met de reden als memo. Eval-cases die de oude,
    /// nu-vervangen bewoording afdwingen (forbidden_claim) vervallen zo automatisch
    /// mee — ze toetsen straks tegen achterhaalde grond.</summary>
    public sealed record Invalidation(string SubjectRef, string FactKind, string Reason);

    /// <summary>Het errata-plan: de ruling-deprecatie (SUPERSEDES) plus de
    /// invalidaties. <see cref="TargetRulingRef"/> is null als het erratum geen
    /// bestaande ruling vervangt (puur een kaart-tekst-erratum) — dan alleen
    /// afhankelijken-invalidatie.</summary>
    public sealed record Plan(
        string? TargetRulingRef,
        string ErratumRef,
        IReadOnlyList<Invalidation> Invalidations)
    {
        public bool DeprecatesRuling => TargetRulingRef is not null;
        public bool IsEmpty => TargetRulingRef is null && Invalidations.Count == 0;
    }

    /// <summary>Bouw het plan. <paramref name="targetRulingRef"/> is de ruling die het
    /// erratum vervangt (of null); <paramref name="dependents"/> zijn de feiten/
    /// eval-cases die aan het onderwerp hangen. De ruling zelf wordt niet in de
    /// invalidaties opgenomen (die krijgt een eigen SUPERSEDES-transitie); ze wordt
    /// wel uit de afhankelijken gefilterd zodat er geen dubbele actie ontstaat.</summary>
    public static Plan Plan_(
        string erratumRef,
        string? targetRulingRef,
        IEnumerable<Dependent> dependents)
    {
        ArgumentNullException.ThrowIfNull(erratumRef);
        ArgumentNullException.ThrowIfNull(dependents);

        var invalidations = new List<Invalidation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in dependents)
        {
            if (string.IsNullOrWhiteSpace(d.Ref)) continue;
            // De vervangen ruling krijgt haar eigen SUPERSEDES-transitie, niet ook nog
            // een invalidatie — anders telt ze dubbel.
            if (targetRulingRef is not null && string.Equals(d.Ref, targetRulingRef, StringComparison.Ordinal))
                continue;
            if (!seen.Add(d.Ref)) continue;
            invalidations.Add(new Invalidation(d.Ref, d.FactKind,
                $"errata-invalidatie: {erratumRef} raakt hetzelfde onderwerp"));
        }

        return new Plan(targetRulingRef, erratumRef, invalidations);
    }
}
