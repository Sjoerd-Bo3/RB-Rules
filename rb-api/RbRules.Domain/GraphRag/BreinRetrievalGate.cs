namespace RbRules.Domain.GraphRag;

/// <summary>De feature-flag-poort van de brein-GraphRAG-retrieval in /ask (fase
/// ask-retrieval, #228). /ask is een LIVE gebruikerspad; de brein-retrieval mag het
/// NIET breken, dus zit alles achter deze DEFAULT-UIT flag. Puur parse-werk zodat de
/// flag-semantiek testbaar is zonder configuratie/omgeving.
///
/// Flag UIT ⇒ /ask draait EXACT zoals nu: geen brein-call, geen extra latency, geen
/// gedragswijziging. Alleen een expliciet aan-woord ("1"/"true"/"on"/"enabled",
/// hoofdletter-ongevoelig) zet hem aan; alles anders (leeg, null, "0", onzin) is
/// UIT — de veilige default.</summary>
public static class BreinRetrievalGate
{
    /// <summary>De omgevingsvariabele/config-sleutel. Bewust dezelfde spelling als de
    /// docs (docs/ARCHITECTURE §graphrag) zodat beheer en code niet uiteenlopen.</summary>
    public const string EnvVar = "BREIN_RETRIEVAL_ENABLED";

    /// <summary>Parse de ruwe flag-waarde. Default (null/leeg/onbekend) = FALSE.</summary>
    public static bool Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var v = raw.Trim();
        return v.Equals("1", StringComparison.Ordinal)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase)
            || v.Equals("enabled", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
