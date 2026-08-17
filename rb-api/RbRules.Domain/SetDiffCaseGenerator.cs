namespace RbRules.Domain;

/// <summary>Een nieuwe kaart uit een set-diff. <see cref="EnergyCostId"/> is het
/// gold-support-id van de knoop die de energiekost draagt (mag gelijk zijn aan
/// <see cref="CardRef"/> als de kost een property op de kaartknoop is).</summary>
public sealed record NewCardFact(string CardRef, string Name, string? EnergyCostId = null);

/// <summary>Een nieuw keyword/mechanic uit een set-diff, met de verankerende
/// rule-sectie (Core-Rules/glossary) die het definieert — die sectie is de
/// verwachte citatie én een gold-support-knoop.</summary>
public sealed record NewConceptFact(string ConceptRef, string Label, string? RuleSectionId = null);

/// <summary>Een erratum uit een set-diff. <see cref="PreErrataClaim"/> is de nu-
/// achterhaalde bewoording — die wordt als <see cref="ForbiddenClaim"/> in de
/// gegenereerde case gezet: produceert het brein die na het erratum nog, dan is dat
/// een fout. <see cref="RuleSectionId"/> is de geërraterde sectie (gold + citatie).</summary>
public sealed record ErratumFact(
    string ErratumRef,
    string SubjectRef,
    string PreErrataClaim,
    string? RuleSectionId = null);

/// <summary>De diff van één nieuwe set/errata-drop (#231, spec §7 — cold-start stap
/// 3: "genereer kandidaat-EvalCases uit de set-diff"). Bevat de deterministisch uit
/// gestructureerde velden afleidbare nieuwigheden; rb-ai zou de vraagteksten later
/// natuurlijker kunnen formuleren, maar de VÓRM (welke case, welke gold-support,
/// welke forbidden_claim) is puur afleidbaar en dus €0 testbaar.</summary>
public sealed record SetDiff(
    string SetId,
    IReadOnlyList<NewCardFact> NewCards,
    IReadOnlyList<NewConceptFact> NewConcepts,
    IReadOnlyList<ErratumFact> Errata);

/// <summary>Genereert kandidaat-<see cref="EvalCase"/>s uit een set/errata-diff
/// (#231, spec §7). ALTIJD in <see cref="EvalStatus.Shadow"/>: de gold-ids van een
/// verse set zijn nog niet door een mens gecureerd — de cases scoren en worden
/// gerapporteerd, maar breken de CI van <c>main</c> niet tot een reviewer ze op
/// Active zet (cold-start stap 4: "draai de harness in shadow"). Puur/IO-loos: de
/// aanroeper (een ingest-service) levert de diff uit de gestructureerde velden en
/// zet de kandidaten in de reviewqueue — dat wegschrijven is de integratie-follow-up.</summary>
public static class SetDiffCaseGenerator
{
    /// <summary>Bouw de kandidaat-cases. <paramref name="releasedOn"/> ankert
    /// <see cref="EvalCase.ValidFrom"/> (temporele cases toetsen "gold dit vanaf deze
    /// set?"). Deterministische ids (<c>evc:{set}:{kind}:{ref}</c>) zodat een her-run
    /// van dezelfde diff dezelfde case-ids oplevert (idempotente reviewqueue).</summary>
    public static IReadOnlyList<EvalCase> Generate(SetDiff diff, DateOnly releasedOn)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var cases = new List<EvalCase>();

        // Kaart → Factoid: "wat is de energiekost van X?" — één feit uit één knoop.
        foreach (var card in diff.NewCards)
        {
            var gold = new List<string> { card.CardRef };
            if (card.EnergyCostId is { } cost && cost != card.CardRef) gold.Add(cost);
            cases.Add(new EvalCase
            {
                Id = $"evc:{diff.SetId}:card:{card.CardRef}",
                Question = $"Wat is de energiekost van {card.Name}?",
                QueryType = EvalQueryType.Factoid,
                Status = EvalStatus.Shadow,
                ValidFrom = releasedOn,
                GoldSupport = gold,
            });
        }

        // Keyword/mechanic → Inference: "hoe werkt X?" — meerdere hops naar de regel.
        foreach (var concept in diff.NewConcepts)
        {
            var gold = new List<string> { concept.ConceptRef };
            var citations = new List<string>();
            if (concept.RuleSectionId is { } sec)
            {
                gold.Add(sec);
                citations.Add(sec);
            }
            cases.Add(new EvalCase
            {
                Id = $"evc:{diff.SetId}:concept:{concept.ConceptRef}",
                Question = $"Hoe werkt {concept.Label}?",
                QueryType = EvalQueryType.Inference,
                Status = EvalStatus.Shadow,
                ValidFrom = releasedOn,
                GoldSupport = gold,
                ExpectedCitations = citations,
            });
        }

        // Erratum → Temporal: tijd-/versie-gevoelig, met de oude bewoording als
        // forbidden_claim (nu een bekende hallucinatie).
        foreach (var err in diff.Errata)
        {
            var gold = new List<string> { err.SubjectRef };
            var citations = new List<string>();
            if (err.RuleSectionId is { } sec)
            {
                gold.Add(sec);
                citations.Add(sec);
            }
            cases.Add(new EvalCase
            {
                Id = $"evc:{diff.SetId}:errata:{err.ErratumRef}",
                Question = $"Geldt de oude regel voor {err.SubjectRef} nog na dit erratum?",
                QueryType = EvalQueryType.Temporal,
                Status = EvalStatus.Shadow,
                ValidFrom = releasedOn,
                GoldSupport = gold,
                ExpectedCitations = citations,
                ForbiddenClaims =
                [
                    new ForbiddenClaim(
                        Id: $"fc:{err.ErratumRef}:preerrata",
                        Text: err.PreErrataClaim),
                ],
            });
        }

        return cases;
    }
}
