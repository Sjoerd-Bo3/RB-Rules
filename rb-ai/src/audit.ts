// Steekproef-audit van gepromoveerde brein-interacties (#255). PUUR (zod-schema +
// request-validatie), zonder Agent SDK — zelfde snit als extract.ts, en om dezelfde
// reden: de vocabulaire→schema-vertaling is zo unit-testbaar zonder LLM.
//
// WAAROM DIT ENDPOINT BESTAAT. De "precisie ≈ 0,91" in de observability is de
// accept-ratio van onze eigen promotie-poort (verified ÷ judged) — zelfreferentieel:
// een pijplijn die tautologieën promoveert scoort er uitstekend op. De audit laat
// een STERKER model (task "hard" i.p.v. de cheap-bulk) per gepromoveerde interactie
// een gesloten oordeel vellen: klopt de bewering, en wordt ze gedragen door het
// meegegeven bewijs? Het oordeel is data met eigen provenance — rb-api legt het
// vast als aparte audit-regel en verandert er NOOIT zelfstandig een tier mee.
import { z } from "zod";
// Type-only: audit.ts blijft runtime-puur (geen Agent SDK-import), maar de
// builder hieronder moet wél het Task-vocabulaire van ai.ts spreken.
import type { Task } from "./ai.js";
import { isModelAlias } from "./providers/registry.js";
import type { ModelAlias } from "./providers/types.js";

/** De audit-request: rb-api componeert claim + bewijs al tot één tekst (net als de
 * extract-endpoints krijgt deze sidecar nooit losse database-toegang). */
export interface InteractionAuditRequest {
  system?: string;
  model?: ModelAlias;
  text: string;
}

/** Eén geveld oordeel zoals de tool het emit — de vorm die de .NET-parser
 * (InteractionAuditExtraction.Parse) als tweede muur verwacht. */
export interface AuditVerdict {
  correct: boolean;
  supported_by_evidence: boolean;
  motivation: string;
}

/** Server-side addendum: dwingt de tool-call af ongeacht wat de aanroeper als
 * system meestuurt (zelfde patroon als INTERACTION_TOOL_ADDENDUM). Precies één
 * verdict: een audit zonder oordeel of met twee oordelen is geen audit. */
export const AUDIT_TOOL_ADDENDUM =
  "Roep de tool `emit_audit_verdict` PRECIES ÉÉN keer aan met precies één verdict. " +
  "Beoordeel UITSLUITEND de voorgelegde bewering tegen het meegeleverde bewijs — " +
  "geen kennis van buiten het bewijs gebruiken voor `supported_by_evidence`. Geef " +
  "daarna geen verdere uitleg.";

/** Het gesloten oordeel-schema (#255): twee booleans + een korte motivering. Bewust
 * GEEN vrije tekst-parse en geen extra velden — het oordeel moet machine-leesbaar
 * en éénduidig zijn. `.length(1)` op de array: de tool-forced vorm hergebruikt de
 * array-envelop van extractWithTool (resultKey vangt een array), maar een audit
 * heeft per constructie precies één verdict. */
export function buildAuditToolShape(): z.ZodRawShape {
  const verdictSchema = z.object({
    correct: z.boolean(),
    supported_by_evidence: z.boolean(),
    motivation: z.string(),
  });
  return { verdicts: z.array(verdictSchema).length(1) };
}

export function auditToolDescription(): string {
  return (
    "Emit exact één audit-verdict over de voorgelegde interactie-bewering: " +
    "`correct` (klopt de bewering inhoudelijk voor Riftbound TCG?), " +
    "`supported_by_evidence` (wordt ze gedragen door het meegeleverde bewijs?), " +
    "`motivation` (1-3 zinnen, Engels)."
  );
}

/** De VOLLEDIGE extractWithTool-aanroep van het audit-endpoint, als puur object
 * (#255-review). Waarom dit bestaat: de `task: "hard"`-bedrading was onbewaakt —
 * haal hem uit de server.ts-handler en tsc én alle tests bleven groen, terwijl
 * elke `interaction_audit`-rij "claude-opus-4-8" zou stempelen over een run die
 * stil op het cheap-model draaide. Valse provenance voor exact de meting waarvan
 * "sterker model" de pointe is. De handler spreidt dit object ongewijzigd in
 * `extractWithTool`; de tests toetsen hier het GEDRAG (task "hard", en via de
 * runQuery-naad: dat de keten op het opus-model eindigt). `task` is bewust
 * niet-optioneel in het return-type: wie hem verwijdert, krijgt de compiler én
 * de tests tegen zich. */
export function buildAuditExtraction(
  request: InteractionAuditRequest,
  signal?: AbortSignal,
): {
  toolName: string;
  description: string;
  schema: z.ZodRawShape;
  resultKey: string;
  system?: string;
  addendum: string;
  text: string;
  signal?: AbortSignal;
  task: Task;
  model: ModelAlias;
} {
  return {
    toolName: "emit_audit_verdict",
    description: auditToolDescription(),
    schema: buildAuditToolShape(),
    resultKey: "verdicts",
    system: request.system,
    addendum: AUDIT_TOOL_ADDENDUM,
    text: request.text,
    signal,
    // Het sterkere model — de hele pointe van de audit (#255): MODEL.hard via de
    // bestaande taak-typering.
    task: "hard",
    model: request.model ?? "opus",
  };
}

export type AuditParseResult =
  | { ok: true; request: InteractionAuditRequest }
  | { ok: false; error: string };

export function parseInteractionAuditRequest(body: unknown): AuditParseResult {
  const b = (typeof body === "object" && body !== null ? body : {}) as Record<string, unknown>;
  if (b.model !== undefined && b.model !== null && !isModelAlias(b.model))
    return { ok: false, error: "onbekende modelalias" };
  const text = typeof b.text === "string" ? b.text : "";
  if (!text.trim()) return { ok: false, error: "text vereist" };
  const system = typeof b.system === "string" && b.system.trim() ? b.system : undefined;
  return {
    ok: true,
    request: { system, text, ...(isModelAlias(b.model) ? { model: b.model } : {}) },
  };
}
