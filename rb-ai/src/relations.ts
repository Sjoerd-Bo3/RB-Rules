// Relatievoorstellen uit de agentic ask (#120): het agent-addendum (ai.ts)
// vraagt de agent ontdekte verbanden NA het eindantwoord te melden als één
// marker-regel + JSON in dezelfde vorm als de relatie-mining (#116). Dit
// bestand splitst dat blok van het antwoord af zodat de gebruiker nooit rauwe
// JSON ziet en rb-api het blok als eigen veld (`relations`) naast `steps`
// terugkrijgt. Bewust zonder JSON-parse: rb-api parseert met de gedeelde
// LlmJson (tolerant voor fences/prose) en doet daar ook de validatie —
// dezelfde poorten als de mining. Apart van ai.ts zodat dit zonder Agent SDK
// unit-testbaar is (patroon brain-tools.ts).

/** Vaste marker waarmee de agent het voorstellenblok aankondigt; exact deze
 * spelling staat in het AGENT_ADDENDUM (ai.ts). */
export const RELATIONS_MARKER = "RELATIEVOORSTELLEN:";

export interface SplitAnswer {
  answer: string;
  /** Rauwe tekst achter de marker (JSON + eventuele fences); undefined als er
   * geen marker of geen inhoud achter de marker staat. */
  relations?: string;
}

/** Splits het relatievoorstellen-blok van een agent-antwoord. De laatste
 * marker telt (het addendum eist hem ná het eindantwoord; mocht de agent de
 * marker eerder citeren, dan wint het echte blok aan het eind). Zonder marker
 * blijft het antwoord byte-gelijk. */
export function splitRelationProposals(raw: string): SplitAnswer {
  const idx = raw.lastIndexOf(RELATIONS_MARKER);
  if (idx < 0) return { answer: raw };
  const relations = raw.slice(idx + RELATIONS_MARKER.length).trim();
  const answer = raw.slice(0, idx).trim();
  return relations ? { answer, relations } : { answer };
}
