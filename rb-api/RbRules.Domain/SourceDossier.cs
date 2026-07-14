namespace RbRules.Domain;

/// <summary>Compleetheidssignaal voor het bron-dossier (#171): pragmatisch
/// afgeleid uit de laatste scan-uitkomst plus of een vervolgstap (classify
/// op een change, claims-mining op het document) ergens vastliep of nog
/// hangt. Semantische volledigheid — "zijn écht alle rulings uit dit artikel
/// gehaald?" — is niet hard garandeerbaar; dit is een signaal, geen garantie
/// (de UI zegt dat er ook expliciet bij, zie de dossier-service).</summary>
public static class SourceDossierCompleteness
{
    /// <summary>Scan ok, geen hangende/mislukte vervolgstap, en er is
    /// opbrengst — de sterkste stand die dit signaal kan geven.</summary>
    public const string Volledig = "volledig";

    /// <summary>De laatste scan faalde, of een vervolgstap (classify/claims)
    /// op materiaal van deze bron mislukte of loopt nog (pending).</summary>
    public const string Onvolledig = "onvolledig";

    /// <summary>Scan ok, maar niets opgeleverd — kan legitiem zijn (bron
    /// zonder wijziging sinds de vorige scan).</summary>
    public const string Leeg = "leeg";

    /// <summary>Nog geen enkele scan-regel voor deze bron gevonden.</summary>
    public const string NooitGescand = "nooit-gescand";

    /// <summary>Bepaalt het compleetheidssignaal. Pure functie, geen I/O —
    /// de aanroeper (Infrastructure) verzamelt de vier ingrediënten uit
    /// run_log/Document/Change.
    /// <list type="bullet">
    /// <item><paramref name="lastScanStatus"/>: status van de laatste
    /// "scan"-regel voor deze bron (run_log, Ref = source.Id); null = nog
    /// nooit gescand.</item>
    /// <item><paramref name="anyStepFailed"/>: een vervolgstap (classify op
    /// een change van deze bron, claims-mining op het document van deze
    /// bron) logde een error-regel.</item>
    /// <item><paramref name="anyStepPending"/>: een vervolgstap is nog niet
    /// (kunnen) lopen, bv. het nieuwste document is nog niet
    /// claims-gemined.</item>
    /// <item><paramref name="opbrengstTotaal"/>: het totaal aantal
    /// entiteiten die aan deze bron hangen (changes, bans, errata, rulings,
    /// claims, regelsecties) — 0 betekent "niets opgeleverd".</item>
    /// </list></summary>
    public static string Evaluate(
        string? lastScanStatus, bool anyStepFailed, bool anyStepPending, int opbrengstTotaal)
    {
        if (lastScanStatus is null) return NooitGescand;
        if (lastScanStatus == "error") return Onvolledig;
        if (anyStepFailed || anyStepPending) return Onvolledig;
        return opbrengstTotaal == 0 ? Leeg : Volledig;
    }

    /// <summary>Korte NL-toelichting voor de UI — bewust eerlijk over de
    /// grens van dit signaal (#171: "is niet hard garandeerbaar").</summary>
    public static string Note(string status) => status switch
    {
        NooitGescand => "Nog nooit gescand.",
        Onvolledig => "Laatste verwerking: onvolledig — een stap mislukte of loopt nog. "
            + "Controleer het ruwe document als je meer verwacht.",
        Leeg => "Laatste scan gelukt, maar niets opgeleverd — kan legitiem zijn "
            + "(geen wijziging sinds de vorige keer).",
        _ => "Laatste verwerking: voltooid. Semantische volledigheid is niet hard "
            + "te garanderen — controleer het ruwe document als je meer verwacht.",
    };
}
