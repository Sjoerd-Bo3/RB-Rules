// Ontologie-begrensde, tool-forced brein-extractie (#226, ARCHITECTURE brein-epic
// §3.1). Twee endpoints spiegelen de .NET-extractie-VORM (RbRules.Domain
// InteractionExtraction / MechanicPredicateExtraction): gegeven kaart/regel-tekst +
// het ontologie-vocabulaire levert de agent GESTRUCTUREERDE kandidaten via een
// geforceerde tool-call.
//
// SINDS #312 IS DE TOOL-VORM VAST. Tot dan bakte dit bestand het aangeboden
// vocabulaire als zod-enum ín het schema, per aanroep — elke kaart een ander
// schema, dus een andere tool-definitie in het request-prefix (slecht voor de
// prompt-cache) en per constructie onverenigbaar met een voorverwarmde sessie
// (#154: een warme sessie boot met één vaste tool-set). Nu is de tool-vorm een
// CONSTANTE en reist het vocabulaire als INVOER mee in de prompt
// ({@link interactionPromptText}). De gesloten-vraag-regel uit CLAUDE.md
// ("nooit een term buiten het aangeboden lijstje") verschuift daarmee van het
// schema naar een DETERMINISTISCHE NAREKENING in dit bestand
// ({@link enforceInteractionVocabulary}): wat het model ook emit, geen term
// buiten het vocabulaire verlaat rb-ai. De .NET-parser blijft de tweede muur
// (defense-in-depth), en de narekening hier is bewust nooit strénger dan die
// muur — anders zou de dekking dalen op items die rb-api wél zou accepteren.
//
// Dit bestand is PUUR (zod-schema's + request-validatie + narekening), zonder
// Agent SDK — de SDK-gedreven run woont in ai.ts, net als askClaude. Zo is de
// hele vocabulaire-poort unit-testbaar zonder LLM.
import { z } from "zod";

// ── Interacties (spiegelt InteractionExtraction, emit_interactions) ──────────

/** Eén aangeboden ref (BrainRef + label) die de LLM als from/to MAG noemen. */
export interface OfferedRef {
  ref: string;
  label: string;
}

/** Het gesloten vocabulaire van één extractie-aanroep: de aangeboden refs + de
 * qualifier-lexica (Window/Status) + de kind-/conditie-/rol-enums. Alles komt
 * uit de .NET-Domain-laag (één bron); rb-ai geeft het aan het model als
 * prompt-invoer en rekent het antwoord er deterministisch tegen na (#312). */
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
  "refs, kinds en window/status-waarden uit het aangeboden vocabulaire in de invoer — " +
  "verzin er geen. Geef daarna geen verdere uitleg.";

export const PREDICATE_TOOL_ADDENDUM =
  "Roep de tool `emit_mechanic_predicates` PRECIES ÉÉN keer aan met alle eigenschappen " +
  "die uit de tekst blijken (een lege lijst als er niets uit blijkt). Gebruik UITSLUITEND " +
  "de aangeboden predicaten. Verzin geen predicaten of tokens. Geef daarna geen verdere uitleg.";

/** De vaste zod raw shape voor emit_interactions (#312). Vrije strings in plaats
 * van enums: het vocabulaire zit in de prompt en de poort in
 * {@link enforceInteractionVocabulary}. De vorm zelf (velden, types, verplicht/
 * optioneel) blijft exact die van vóór #312 — alleen de enum-poorten zijn naar
 * de narekening verhuisd. */
export function buildInteractionToolShape(): z.ZodRawShape {
  const conditionSchema = z.object({
    on_kind: z.string(),
    subject_role: z.string().nullish(),
    window: z.string().nullish(),
    status: z.string().nullish(),
    value: z.string().nullish(),
    operator: z.string().nullish(),
  });
  const interactionSchema = z.object({
    from: z.string(),
    to: z.string(),
    kind: z.string(),
    interacts: z.boolean(),
    explanation: z.string().nullish(),
    conditions: z.array(conditionSchema).optional(),
  });
  return { interactions: z.array(interactionSchema) };
}

/** Vaste description voor de emit_interactions-tool (#312): de refs staan niet
 * langer hier maar in de prompt ({@link interactionPromptText}) — een
 * per-aanroep-description zou de tool-definitie, en daarmee het cache-bare
 * request-prefix, alsnog per kaart verschillend maken. */
export function interactionToolDescription(): string {
  return (
    "Emit ontologie-begrensde, gekwalificeerde interacties tussen de refs die in de " +
    "invoer als aangeboden vocabulaire staan opgesomd."
  );
}

/** Zet één vocabulaire-regel om, of "" bij een lege lijst (dan is er op die as
 * niets aangeboden en hoort de regel niet in de prompt). */
function vocabLine(label: string, values: string[]): string {
  return values.length > 0 ? `- ${label}: ${values.join(" | ")}` : "";
}

/** De prompt-invoer voor emit_interactions (#312): het gesloten vocabulaire als
 * tekstblok VÓÓR de te analyseren tekst. Bewust in de user-prompt en niet in de
 * system-prompt: de system-prompt is het stabiele (cache-bare) deel van de
 * aanroep, het vocabulaire het per-kaart-variabele deel. */
export function interactionPromptText(req: InteractionExtractRequest): string {
  const refList = req.refs.map((r) => `${r.ref} (${r.label})`).join("; ");
  const lines = [
    "Aangeboden vocabulaire (gesloten — gebruik uitsluitend deze waarden):",
    `- refs (from/to): ${refList}`,
    vocabLine("kinds", req.kinds),
    vocabLine("conditie-assen (on_kind)", req.conditionKinds),
    vocabLine("subject_role", req.roles),
    vocabLine("window-lexicon", req.windowLexicon),
    vocabLine("status-lexicon", req.statusLexicon),
  ].filter(Boolean);
  return `${lines.join("\n")}\n\nTekst:\n${req.text}`;
}

/** Uitkomst van de deterministische narekening: wat door mag, en de MAAT van wat
 * niet door mocht (aantallen, nooit inhoud — werkafspraak 7). */
export interface VocabularyGateResult<T> {
  accepted: T[];
  /** Items met een term buiten het vocabulaire of een kapotte vorm — geweigerd. */
  rejected: number;
  /** Condities die hun as- of lexicon-poort niet haalden — weggelaten terwijl het
   * item zelf bleef (dezelfde semantiek als de .NET-muur, die een invalide
   * conditie laat vallen zonder het item te verwerpen). */
  rejectedConditions: number;
}

const str = (v: unknown): string | null => (typeof v === "string" && v.trim() ? v : null);

/** Case-insensitieve lidmaatschapstoets, met de lege lijst als "niets aangeboden
 * op deze as ⇒ geen poort" — exact de fallback die de oude enum-bouw had
 * (stringEnum([]) werd z.string()). Case-insensitief omdat de .NET-muur dat op
 * kind/on_kind/window/status óók is (OrdinalIgnoreCase/Canonicalize): een
 * strengere poort hier zou items wegfilteren die rb-api gewoon accepteert. */
function inLexicon(values: string[], v: string): boolean {
  return values.length === 0 || values.some((x) => x.toLowerCase() === v.trim().toLowerCase());
}

/** Reken één geëmit conditie na. Null = conditie geweigerd (as of lexicon
 * geschonden); het ITEM blijft dan staan — de .NET-muur (ParseDetailed) doet
 * exact hetzelfde (`continue` op de conditie, niet op het item), en een conditie
 * ALTIJD laten vallen waar de muur hem zou accepteren zou dekking kosten. De
 * poort toetst per as alleen het veld dat bij die as hoort, net als de muur:
 * een STATUS-conditie met een junk-window ernaast blijft een geldige
 * STATUS-conditie. */
function checkCondition(
  raw: unknown,
  req: InteractionExtractRequest,
): ExtractedCondition | null {
  if (typeof raw !== "object" || raw === null) return null;
  const c = raw as Record<string, unknown>;
  const onKind = str(c.on_kind);
  if (onKind === null || !inLexicon(req.conditionKinds, onKind)) return null;

  // subject_role buiten de rollen wordt ge-nuld, niet geweigerd — de .NET-muur
  // (`if (!IsValid(role)) role = null`) doet dat ook; hier weigeren zou een
  // conditie kosten die rb-api behoudt.
  const roleRaw = str(c.subject_role);
  const role = roleRaw !== null && inLexicon(req.roles, roleRaw) ? roleRaw : null;

  const window = str(c.window);
  const status = str(c.status);
  const axis = onKind.trim().toUpperCase();
  if (axis === "WINDOW" && (window === null || !inLexicon(req.windowLexicon, window)))
    return null;
  if (axis === "STATUS" && (status === null || !inLexicon(req.statusLexicon, status)))
    return null;

  return {
    on_kind: onKind,
    subject_role: role,
    window: window !== null && inLexicon(req.windowLexicon, window) ? window : null,
    status: status !== null && inLexicon(req.statusLexicon, status) ? status : null,
    value: str(c.value),
    operator: str(c.operator),
  };
}

/** De deterministische narekening voor emit_interactions (#312) — de poort die
 * de zod-enums verving. GESLOTEN VRAAG, GESLOTEN ANTWOORD (CLAUDE.md): een item
 * met een ref, kind of conditie-term buiten het aangeboden vocabulaire wordt
 * geweigerd; wat overblijft wordt HERBOUWD tot exact de bekende velden, zodat er
 * ook geen onbekende velden of vreemde types richting rb-api lekken (de oude
 * zod-parse stripte die net zo goed).
 *
 * Itemgranulariteit is bewust: de oude enum-poort liet bij één schending de
 * HELE tool-call falen (SDK-validatiefout), waarna de run vaak als
 * no_tool_call/timeout eindigde. Eén verzonnen ref kost nu één item in plaats
 * van de hele kaart — dekking kan daardoor alleen maar stijgen. De weigeringen
 * worden geteld (maten, geen inhoud) zodat de logregel laat zien hoe vaak het
 * model buiten het lijstje kleurt. */
export function enforceInteractionVocabulary(
  items: unknown[],
  req: InteractionExtractRequest,
): VocabularyGateResult<ExtractedInteraction> {
  const refs = new Set(req.refs.map((r) => r.ref));
  const accepted: ExtractedInteraction[] = [];
  let rejected = 0;
  let rejectedConditions = 0;

  for (const raw of items) {
    if (typeof raw !== "object" || raw === null) {
      rejected += 1;
      continue;
    }
    const item = raw as Record<string, unknown>;
    const from = str(item.from);
    const to = str(item.to);
    const kind = str(item.kind);
    // refs zijn Ordinal-exact (zo vergelijkt de .NET-muur ze ook); kind is
    // case-insensitief (de muur canonicaliseert via OrdinalIgnoreCase).
    if (
      from === null || to === null || kind === null ||
      !refs.has(from) || !refs.has(to) || !inLexicon(req.kinds, kind) ||
      typeof item.interacts !== "boolean"
    ) {
      rejected += 1;
      continue;
    }

    const conditions: ExtractedCondition[] = [];
    if (Array.isArray(item.conditions)) {
      for (const c of item.conditions) {
        const checked = checkCondition(c, req);
        if (checked) conditions.push(checked);
        else rejectedConditions += 1;
      }
    }

    accepted.push({
      from,
      to,
      kind,
      interacts: item.interacts,
      ...(str(item.explanation) !== null ? { explanation: str(item.explanation)! } : {}),
      ...(conditions.length > 0 ? { conditions } : {}),
    });
  }
  return { accepted, rejected, rejectedConditions };
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

/** De vaste zod raw shape voor emit_mechanic_predicates (#312): predicate en
 * object als vrije strings — de predicaten-poort zit in
 * {@link enforcePredicateVocabulary}, het object was al vrij (een nieuwe set mag
 * nieuwe events/keywords introduceren; de .NET-parser normaliseert en de review
 * cureert). */
export function buildPredicateToolShape(): z.ZodRawShape {
  const predicateSchema = z.object({
    predicate: z.string(),
    object: z.string(),
  });
  return { predicates: z.array(predicateSchema) };
}

/** Vaste description (#312): subject en hints staan in de prompt
 * ({@link predicatePromptText}), niet hier — zelfde cache-/warm-argument als bij
 * {@link interactionToolDescription}. */
export function predicateToolDescription(): string {
  return (
    "Emit getypeerde eigenschappen (predicate, object) van de mechanic die in de " +
    "invoer als onderwerp is aangeboden."
  );
}

/** De prompt-invoer voor emit_mechanic_predicates (#312): onderwerp + gesloten
 * predicatenlijst + de niet-limitatieve object-hints, vóór de tekst. */
export function predicatePromptText(req: PredicateExtractRequest): string {
  const hints = (req.objectHints ?? []).join(", ");
  const lines = [
    `Onderwerp: ${req.subjectLabel} (${req.subjectRef})`,
    `Toegestane predicaten (gesloten): ${req.predicates.join(" | ")}`,
    ...(hints ? [`Object-hints (niet-limitatief): ${hints}`] : []),
  ];
  return `${lines.join("\n")}\n\nTekst:\n${req.text}`;
}

/** De deterministische narekening voor emit_mechanic_predicates (#312): het
 * predicaat moet in de aangeboden lijst staan (case-insensitief, zie
 * {@link inLexicon} — de .NET-muur normaliseert zelf), het object is een vrij
 * maar verplicht token. */
export function enforcePredicateVocabulary(
  items: unknown[],
  req: PredicateExtractRequest,
): VocabularyGateResult<ExtractedPredicate> {
  const accepted: ExtractedPredicate[] = [];
  let rejected = 0;
  for (const raw of items) {
    if (typeof raw !== "object" || raw === null) {
      rejected += 1;
      continue;
    }
    const item = raw as Record<string, unknown>;
    const predicate = str(item.predicate);
    const object = str(item.object);
    if (predicate === null || object === null || !inLexicon(req.predicates, predicate)) {
      rejected += 1;
      continue;
    }
    accepted.push({ predicate, object });
  }
  return { accepted, rejected, rejectedConditions: 0 };
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
