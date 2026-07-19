// Ontologie-begrensde, tool-forced brein-extractie (#226, ARCHITECTURE brein-epic
// §3.1). Twee endpoints spiegelen de .NET-extractie-VORM (RbRules.Domain
// InteractionExtraction / MechanicPredicateExtraction): gegeven kaart/regel-tekst +
// de ontologie-enum-constraints levert de agent GESTRUCTUREERDE kandidaten via een
// geforceerde tool-call. Het model KAN geen ref/kind/window buiten de aangeboden
// enums noemen (zod-enum-poort); de .NET-parser blijft de tweede muur
// (defense-in-depth). Dit bestand is PUUR (zod-schema's + request-validatie), zonder
// Agent SDK — de SDK-gedreven run woont in ai.ts, net als askClaude. Zo is de
// vocabulaire→schema-vertaling unit-testbaar zonder LLM.
import { z } from "zod";

// ── Interacties (spiegelt InteractionExtraction, emit_interactions) ──────────

/** Eén aangeboden ref (BrainRef + label) die de LLM als from/to MAG noemen. De
 * enum in het tool-schema wordt hieruit gegenereerd. */
export interface OfferedRef {
  ref: string;
  label: string;
}

/** Het gesloten vocabulaire waarmee het emit_interactions-schema zijn enum-poorten
 * sluit: de aangeboden refs + de qualifier-lexica (Window/Status) + de kind-/
 * conditie-/rol-enums. Alles komt uit de .NET-Domain-laag (één bron); rb-ai
 * spiegelt het slechts in zod. */
export interface InteractionExtractRequest {
  system?: string;
  text: string;
  refs: OfferedRef[];
  kinds: string[];
  conditionKinds: string[];
  roles: string[];
  windowLexicon: string[];
  statusLexicon: string[];
}

/** Eén geëxtraheerde interactie zoals de tool ze emit — dezelfde vorm die de .NET
 * InteractionExtraction.Parse verwacht (snake_case conditie-velden). */
export interface ExtractedInteraction {
  from: string;
  to: string;
  kind: string;
  interacts: boolean;
  explanation?: string;
  conditions?: ExtractedCondition[];
}

export interface ExtractedCondition {
  on_kind: string;
  subject_role?: string | null;
  window?: string | null;
  status?: string | null;
  value?: string | null;
  operator?: string | null;
}

/** Server-side addendum: dwingt de tool-call af ongeacht wat de aanroeper als
 * system meestuurt (zelfde patroon als RESEARCH_CONTRACT/AGENT_ADDENDUM in ai.ts). */
export const INTERACTION_TOOL_ADDENDUM =
  "Roep de tool `emit_interactions` PRECIES ÉÉN keer aan met alle gevonden interacties " +
  "(een lege lijst als er geen noemenswaardige interactie is). Gebruik UITSLUITEND de " +
  "aangeboden refs, kinds en window/status-waarden — verzin er geen. Geef daarna geen " +
  "verdere uitleg.";

export const PREDICATE_TOOL_ADDENDUM =
  "Roep de tool `emit_mechanic_predicates` PRECIES ÉÉN keer aan met alle eigenschappen " +
  "die uit de tekst blijken (een lege lijst als er niets uit blijkt). Verzin geen " +
  "predicaten of tokens. Geef daarna geen verdere uitleg.";

/** Een zod string-enum over een dynamische lijst; valt terug op z.string() bij een
 * lege lijst (dan is er geen zinnige enum-poort, en de .NET-parser gate't alsnog). */
function stringEnum(values: readonly string[]): z.ZodTypeAny {
  return values.length > 0
    ? z.enum(values as [string, ...string[]])
    : z.string();
}

/** Bouwt de zod raw shape voor emit_interactions uit het aangeboden vocabulaire —
 * de tool-forced enum-poort van §3.1. from/to zijn een enum van de aangeboden refs,
 * kind/on_kind/subject_role zijn gesloten enums, window/status komen uit hun
 * lexicon. */
export function buildInteractionToolShape(req: InteractionExtractRequest): z.ZodRawShape {
  const refEnum = stringEnum(req.refs.map((r) => r.ref));
  const conditionSchema = z.object({
    on_kind: stringEnum(req.conditionKinds),
    subject_role: stringEnum(req.roles).nullish(),
    window: stringEnum(req.windowLexicon).nullish(),
    status: stringEnum(req.statusLexicon).nullish(),
    value: z.string().nullish(),
    operator: z.string().nullish(),
  });
  const interactionSchema = z.object({
    from: refEnum,
    to: refEnum,
    kind: stringEnum(req.kinds),
    interacts: z.boolean(),
    explanation: z.string().nullish(),
    conditions: z.array(conditionSchema).optional(),
  });
  return { interactions: z.array(interactionSchema) };
}

/** Description voor de emit_interactions-tool, met de aangeboden refs opgesomd
 * zodat het model weet welke agent/patient-labels achter elke ref horen. */
export function interactionToolDescription(req: InteractionExtractRequest): string {
  const refList = req.refs.map((r) => `${r.ref} (${r.label})`).join("; ");
  return (
    "Emit ontologie-begrensde, gekwalificeerde interacties tussen de aangeboden refs. " +
    `Aangeboden refs: ${refList || "(geen)"}.`
  );
}

// ── Mechanic-predicaten (spiegelt MechanicPredicateExtraction) ───────────────

export interface PredicateExtractRequest {
  system?: string;
  text: string;
  subjectRef: string;
  subjectLabel: string;
  predicates: string[];
  objectHints?: string[];
}

export interface ExtractedPredicate {
  predicate: string;
  object: string;
}

/** Bouwt de zod raw shape voor emit_mechanic_predicates: predicate is een harde
 * enum van de vier predicaten, object een genormaliseerd token (vrije string — een
 * nieuwe set mag nieuwe events/keywords introduceren; de .NET-parser normaliseert en
 * de review cureert). */
export function buildPredicateToolShape(req: PredicateExtractRequest): z.ZodRawShape {
  const predicateSchema = z.object({
    predicate: stringEnum(req.predicates),
    object: z.string(),
  });
  return { predicates: z.array(predicateSchema) };
}

export function predicateToolDescription(req: PredicateExtractRequest): string {
  const hints = (req.objectHints ?? []).join(", ");
  return (
    `Emit getypeerde eigenschappen van ${req.subjectLabel} (${req.subjectRef}).` +
    (hints ? ` Object-hint-lijst: ${hints}.` : "")
  );
}

// ── Request-validatie (puur — apart van server.ts zodat unit-testbaar) ───────

export type ExtractParseResult<T> =
  | { ok: true; request: T }
  | { ok: false; error: string };

function asRecord(v: unknown): Record<string, unknown> {
  return typeof v === "object" && v !== null ? (v as Record<string, unknown>) : {};
}

function stringArray(v: unknown): string[] {
  return Array.isArray(v)
    ? v.filter((x): x is string => typeof x === "string" && x.trim().length > 0)
    : [];
}

function optionalSystem(v: unknown): string | undefined {
  return typeof v === "string" && v.trim() ? v : undefined;
}

export function parseInteractionExtractRequest(
  body: unknown,
): ExtractParseResult<InteractionExtractRequest> {
  const b = asRecord(body);
  const text = typeof b.text === "string" ? b.text : "";
  if (!text.trim()) return { ok: false, error: "text vereist" };

  const rawRefs = Array.isArray(b.refs) ? b.refs : [];
  const refs: OfferedRef[] = [];
  for (const raw of rawRefs) {
    const r = asRecord(raw);
    const ref = typeof r.ref === "string" ? r.ref.trim() : "";
    const label = typeof r.label === "string" ? r.label : ref;
    if (ref) refs.push({ ref, label });
  }
  if (refs.length === 0) return { ok: false, error: "ten minste één ref vereist" };

  const kinds = stringArray(b.kinds);
  if (kinds.length === 0) return { ok: false, error: "kinds-enum vereist" };

  return {
    ok: true,
    request: {
      system: optionalSystem(b.system),
      text,
      refs,
      kinds,
      conditionKinds: stringArray(b.conditionKinds),
      roles: stringArray(b.roles),
      windowLexicon: stringArray(b.windowLexicon),
      statusLexicon: stringArray(b.statusLexicon),
    },
  };
}

export function parsePredicateExtractRequest(
  body: unknown,
): ExtractParseResult<PredicateExtractRequest> {
  const b = asRecord(body);
  const text = typeof b.text === "string" ? b.text : "";
  if (!text.trim()) return { ok: false, error: "text vereist" };

  const subjectRef = typeof b.subjectRef === "string" ? b.subjectRef.trim() : "";
  if (!subjectRef) return { ok: false, error: "subjectRef vereist" };
  const subjectLabel =
    typeof b.subjectLabel === "string" && b.subjectLabel.trim()
      ? b.subjectLabel
      : subjectRef;

  const predicates = stringArray(b.predicates);
  if (predicates.length === 0) return { ok: false, error: "predicates-enum vereist" };

  return {
    ok: true,
    request: {
      system: optionalSystem(b.system),
      text,
      subjectRef,
      subjectLabel,
      predicates,
      objectHints: stringArray(b.objectHints),
    },
  };
}
