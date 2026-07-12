// Token-usage per vraag (#121). De Agent SDK rapporteert in het afsluitende
// result-bericht de opgetelde usage over álle beurten van de run — dus ook de
// tool-beurten van research/agentic. Deze mapper vertaalt dat SDK-object
// (snake_case) naar één simpel totaal dat /ask en /ask/stream teruggeven;
// rb-api boekt het op ask_metric voor het kostenoverzicht.

export interface AskUsage {
  inputTokens: number;
  outputTokens: number;
}

/** Best-effort mapper van het SDK-usage-object naar AskUsage.
 *
 * Input telt de cache-tokens (cache_creation/cache_read) mee: het abonnement
 * kent geen prijs per token, dus "hoeveel prompt-tokens zijn er verwerkt" is
 * de eerlijke volume-maat — zonder cache-reads zou een multi-turn-run
 * (grotendeels cache) er kunstmatig klein uitzien. Onbruikbare invoer (geen
 * object, geen getallen) levert null op, nooit een exception: usage is
 * inzicht, geen voorwaarde voor een antwoord. */
export function usageFromSdk(raw: unknown): AskUsage | null {
  if (typeof raw !== "object" || raw === null) return null;
  const u = raw as Record<string, unknown>;
  const num = (v: unknown): number | null =>
    typeof v === "number" && Number.isFinite(v) && v >= 0 ? v : null;
  const input = num(u.input_tokens);
  const output = num(u.output_tokens);
  if (input === null && output === null) return null;
  return {
    inputTokens:
      (input ?? 0) +
      (num(u.cache_creation_input_tokens) ?? 0) +
      (num(u.cache_read_input_tokens) ?? 0),
    outputTokens: output ?? 0,
  };
}
