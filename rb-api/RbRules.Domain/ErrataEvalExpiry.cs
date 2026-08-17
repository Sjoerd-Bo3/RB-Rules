namespace RbRules.Domain;

/// <summary>De koppeling fase 6 → fase 7 (#231, spec §7 — "koppel aan de fase-6
/// governance-errata-flow voor forbidden_claim-verval"). De
/// <see cref="ErrataLifecycle"/> materialiseert bij een erratum een plan met
/// invalidaties van afhankelijke feiten; dit past dat plan toe op het eval-corpus.
/// Twee vervalniveaus (spec §7, Kritiek C):
/// <list type="bullet">
/// <item>Een invalidatie met <c>FactKind == "eval_case"</c> die een case-id raakt →
/// de héle case krijgt <see cref="EvalCase.SupersededByErratum"/> (overgeslagen tot
/// een mens hem herziet; CI faalt niet op een case die nu tegen achterhaalde grond
/// toetst).</item>
/// <item>Een invalidatie met <c>FactKind == "forbidden_claim"</c> die een forbidden-
/// claim-id raakt → alléén die claim vervalt (<see
/// cref="ForbiddenClaim.SupersededByErratum"/>): produceert het brein die claim ná
/// het erratum, dan is dat GEEN fout meer (de oude "hallucinatie" is nu waar). De
/// rest van de case blijft gewoon scoren.</item>
/// </list>
/// Puur/IO-loos: records zijn immutable, dus dit levert bijgewerkte kopieën; de
/// aanroeper persisteert ze. Een reeds vervallen (superseded) case/claim wordt niet
/// heropend — het eerste erratum wint, latere invalidaties raken hem niet.</summary>
public static class ErrataEvalExpiry
{
    public const string EvalCaseFactKind = "eval_case";
    public const string ForbiddenClaimFactKind = "forbidden_claim";

    /// <summary>Pas één errata-plan toe op het corpus. Cases zonder raakvlak komen
    /// ONgewijzigd (dezelfde referentie) terug, zodat de aanroeper goedkoop kan zien
    /// wat veranderde.</summary>
    public static IReadOnlyList<EvalCase> Apply(
        IEnumerable<EvalCase> cases, ErrataLifecycle.Plan plan)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(plan);

        var caseRefs = plan.Invalidations
            .Where(i => i.FactKind == EvalCaseFactKind)
            .Select(i => i.SubjectRef)
            .ToHashSet(StringComparer.Ordinal);
        var claimRefs = plan.Invalidations
            .Where(i => i.FactKind == ForbiddenClaimFactKind)
            .Select(i => i.SubjectRef)
            .ToHashSet(StringComparer.Ordinal);

        return [.. cases.Select(c => ApplyToCase(c, plan.ErratumRef, caseRefs, claimRefs))];
    }

    private static EvalCase ApplyToCase(
        EvalCase @case, string erratumRef,
        IReadOnlySet<string> caseRefs, IReadOnlySet<string> claimRefs)
    {
        // Case-niveau verval: alleen zetten als nog niet eerder vervallen (eerste
        // erratum wint — geen stille heropening/overschrijving).
        if (@case.SupersededByErratum is null && caseRefs.Contains(@case.Id))
            return @case with { SupersededByErratum = erratumRef };

        // Claim-niveau verval: raak alleen de nog-actieve, matchende claims.
        if (@case.ForbiddenClaims.Any(fc => fc.IsActive && claimRefs.Contains(fc.Id)))
        {
            var updated = @case.ForbiddenClaims
                .Select(fc => fc.IsActive && claimRefs.Contains(fc.Id)
                    ? fc with { SupersededByErratum = erratumRef }
                    : fc)
                .ToList();
            return @case with { ForbiddenClaims = updated };
        }

        return @case;
    }
}
