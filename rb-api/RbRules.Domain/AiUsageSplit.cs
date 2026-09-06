namespace RbRules.Domain;

/// <summary>Verdeelt de token-som van één /ask-vraag over de twee rb-ai-calls
/// die haar maakten (#381): de query-rewrite (light-model) en het antwoord
/// (cheap/hard/agentic). De ask-pijplijn telt alle calls bij elkaar op voor
/// het quotum (<c>AskMetric</c>); het kostengrootboek (<c>ai_usage_event</c>)
/// wil ze juist gescheiden, want ze dragen een ander tarief. Puur: geen IO,
/// geen DbContext — zodat de arithmetiek los van EF te toetsen is.</summary>
public static class AiUsageSplit
{
    public readonly record struct Tokens(long? Input, long? Output);

    /// <summary>Uitkomst: wat er tegen het rewrite-model wordt geboekt en wat
    /// er voor het antwoordpad overblijft. <see cref="Rewrite"/> is null als er
    /// geen rewrite-call was (cache-hit, of geen usage teruggekregen).</summary>
    public readonly record struct Result(Tokens? Rewrite, Tokens? Answer);

    /// <summary>
    /// <paramref name="total"/> is de som over álle calls (rewrite inbegrepen);
    /// <paramref name="rewrite"/> de tokens van alleen de rewrite-call.
    /// <list type="bullet">
    /// <item>Geen total ⇒ niets bekend: beide null ("onbekend ≠ 0", #121).</item>
    /// <item>Geen rewrite ⇒ het hele total is antwoord.</item>
    /// <item>Anders: antwoord = total − rewrite, nooit onder nul — een som die
    /// kleiner is dan haar deel kan alleen uit een programmeerfout komen, en
    /// dan is 0 eerlijker dan een negatief tokenaantal in het grootboek.</item>
    /// </list>
    /// </summary>
    public static Result Apply((long Input, long Output)? total, (long Input, long Output)? rewrite)
    {
        if (total is null)
            return new Result(
                rewrite is null ? null : new Tokens(rewrite.Value.Input, rewrite.Value.Output),
                null);
        if (rewrite is null)
            return new Result(null, new Tokens(total.Value.Input, total.Value.Output));

        var answerIn = Math.Max(0, total.Value.Input - rewrite.Value.Input);
        var answerOut = Math.Max(0, total.Value.Output - rewrite.Value.Output);
        return new Result(
            new Tokens(rewrite.Value.Input, rewrite.Value.Output),
            new Tokens(answerIn, answerOut));
    }
}
