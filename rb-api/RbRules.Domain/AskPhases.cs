using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Per-fase-wandkloktijden van één /ask-vraag (#152): meten in
/// plaats van gissen waar de ~50s zit. De fasen overlappen elkaar sinds de
/// parallelle pipeline (rewrite loopt tegelijk met de eerste
/// retrieval-kanalen), dus de som van de fasen is bewust níet gelijk aan
/// TotalMs — elke fase is de eigen wandkloktijd:
/// - RewriteMs: de query-rewrite-call (0 bij een cache-hit);
/// - EmbedMs: de Ollama-embed-batches samen (ruwe vraag + extra queries);
/// - RetrievalMs: van de start van het eerste retrieval-kanaal tot alle
///   kanalen én de citatie-hydratie klaar zijn (overlapt de rewrite);
/// - AiMs: de afrondende antwoordfase — bij streaming tot het slotframe,
///   bij agentic de hele agent-run inclusief eventueel vangnet;
/// - TotalMs: de volledige vraag (gelijk aan AskTrace.DurationMs).
/// Wordt als compacte JSON op AskTrace.PhaseTimings bewaard; Parse is
/// tolerant — een kapotte rij levert null, nooit een fout.</summary>
public record AskPhases(long RewriteMs, long EmbedMs, long RetrievalMs, long AiMs, long TotalMs)
{
    /// <summary>camelCase, zoals de rest van de API-payloads — de beheer-UI
    /// leest de JSON rechtstreeks uit het trace-veld.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Tolerante parse: null of onleesbare JSON is gedegradeerd
    /// gedrag (oude trace-rijen hebben geen timings), geen crash.</summary>
    public static AskPhases? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AskPhases>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
