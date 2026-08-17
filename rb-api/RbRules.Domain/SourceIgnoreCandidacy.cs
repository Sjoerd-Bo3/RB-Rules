namespace RbRules.Domain;

/// <summary>Negeer-kandidaat-signaal (#180 deel 2): een bron die na meerdere
/// scans niets aantoonbaars heeft opgeleverd (geen changes, geen
/// claims-/rulings-bijdrage) is een kandidaat om te negeren. Puur een hint in
/// de bronnenlijst-projectie (<see cref="RbRules.Infrastructure.
/// SourceListService"/>) — de beheerder beslist, nooit automatisch negeren
/// (dat is te risicovol: een stille-maar-relevante bron mag nooit vanzelf
/// verdwijnen), en bewust geen aparte kandidaten-tabel: de vier ingrediënten
/// komen uit bestaande, in één keer te groeperen tellingen (run_log/Change/
/// ClaimSource/Correction) — geen aparte query per bron, dus geen N+1 over de
/// hele bronnenlijst.</summary>
public static class SourceIgnoreCandidacy
{
    /// <summary>Eén (mislukte of gelukte) scan zegt nog niets — pas na
    /// meerdere pogingen is "niets opgeleverd" een betekenisvol signaal.</summary>
    public const int MinCompletedScans = 2;

    /// <param name="completedScans">Aantal "scan"-run_log-regels voor deze
    /// bron met een andere status dan "error" — een mislukte poging telt niet
    /// mee (zegt niets over de bron zelf).</param>
    /// <param name="changes">Aantal <see cref="Change"/>-rijen op deze bron.</param>
    /// <param name="claims">Aantal claims die deze bron als bewijs draagt
    /// (via ClaimSource).</param>
    /// <param name="rulings">Aantal clarify-mining-rulings (<see
    /// cref="Correction"/>) die aan deze bron te herleiden zijn.</param>
    public static bool Evaluate(int completedScans, int changes, int claims, int rulings) =>
        completedScans >= MinCompletedScans && changes == 0 && claims == 0 && rulings == 0;
}
