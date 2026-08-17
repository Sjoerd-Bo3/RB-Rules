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

// ── Model-aliassen (#323) ────────────────────────────────────────────────────
//
// Het extractie-model is een beheerde instelling in rb-api (brein.extract.model,
// #254-patroon); rb-api stuurt de ALIAS mee en rb-ai vertaalt hem hier naar het
// echte model-ID. GESLOTEN map, bewust: een vrije string zou onbeoordeeld in de
// SDK-options belanden en elke typefout zou pas als SDK-fout ná een dure spawn
// zichtbaar worden. Onbekende alias ⇒ 400 vóór er ook maar één permit is
// geclaimd. rb-api houdt voor de rij-provenance een eigen kopie van deze map
// (BreinExtractModels); drift daartussen komt luidruchtig terug als 400, en
// beide kanten hebben een literal-test (#286-les: een assertie tegen de
// constante die ze bewaakt schuift mee).
// De `-1m`-aliassen kiezen de 1M-contextvariant (Claude Code/Agent SDK-notatie
// `model[1m]`) — relevant voor grote batches (K richting 250), waar de sessie
// voorbij het standaardvenster groeit. Of de variant op dit abonnement
// beschikbaar is weten we niet zeker: een weigering komt als eigen reden
// `model_unavailable` naar buiten (failure.ts), nooit als generieke uitval.
export const EXTRACT_MODELS: Readonly<Record<string, string>> = {
  sonnet: "claude-sonnet-4-6",
  opus: "claude-opus-4-8",
  fable: "claude-fable-5",
  "fable-1m": "claude-fable-5[1m]",
  "sonnet-1m": "claude-sonnet-4-6[1m]",
};

export type ExtractModelParse =
  | { ok: true; model?: string }
  | { ok: false; error: string };

/** Vertaal het optionele `model`-veld van een extract-request naar een echt
 * model-ID. Afwezig/leeg = geen override (het bestaande taak-gedrag, zodat een
 * oudere rb-api niets merkt); een onbekende alias is een 400, nooit een stille
 * terugval — een schakelaar die iets anders doet dan hij zegt is erger dan een
 * foutmelding. */
export function parseExtractModelAlias(v: unknown): ExtractModelParse {
  if (v === undefined || v === null || v === "") return { ok: true };
  if (typeof v !== "string" || !Object.hasOwn(EXTRACT_MODELS, v.trim())) {
    return {
      ok: false,
      error: "onbekende model-alias (gebruik sonnet | opus | fable | fable-1m | sonnet-1m)",
    };
  }
  return { ok: true, model: EXTRACT_MODELS[v.trim()] };
}

// ── Interacties (spiegelt InteractionExtraction, emit_interactions) ──────────

/** Eén aangeboden ref (BrainRef + label) die de LLM als from/to MAG noemen. */
export interface OfferedRef {
  ref: string;
  label: string;
}

/** Het gesloten vocabulaire van één extractie-aanroep: de aangeboden refs + de
 * qualifier-lexica (Window/Status) + de kind-/conditie-/rol-enums + de
 * citeerbare sectie-refs voor `governed_by` (#286/#315). Alles komt uit de
 * .NET-Domain-laag (één bron); rb-ai geeft het aan het model als prompt-invoer
 * en rekent het antwoord er deterministisch tegen na (#312). */
export interface InteractionExtractRequest {
  system?: string;
  /** Opgelost model-ID uit de gesloten aliasmap (#323), of undefined voor het
   * bestaande taak-gedrag. Nooit de rauwe alias: de vertaling gebeurt éénmalig
   * in {@link parseExtractModelAlias}, mét 400-poort. */
  model?: string;
  text: string;
  refs: OfferedRef[];
  kinds: string[];
  conditionKinds: string[];
  roles: string[];
  windowLexicon: string[];
  statusLexicon: string[];
  /** De aangeboden `section:`-refs waaruit het model `governed_by` MAG kiezen
   * (#286). rb-api stuurt ze sinds #286 mee, maar rb-ai las ze tot #315 nergens
   * — waardoor `Interaction.GovernedByRef` in productie altijd null bleef. Leeg
   * = niets aangeboden, dus élke geëmit `governed_by` wordt ge-nuld (zo doet de
   * .NET-muur het ook: een lege sectionSet accepteert niets). */
  sections: string[];
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
  /** De aangeboden regelsectie die deze interactie normatief verankert (#286/
   * #315) — alleen aanwezig als het model een ref uit de aangeboden `sections`
   * noemde; de .NET-muur leest hem als `governed_by` en vult er
   * `Interaction.GovernedByRef` mee. */
  governed_by?: string;
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
  "refs, kinds, window/status-waarden en governed_by-sectie-refs uit het aangeboden " +
  "vocabulaire in de invoer — verzin er geen. Geef daarna geen verdere uitleg.";

export const PREDICATE_TOOL_ADDENDUM =
  "Roep de tool `emit_mechanic_predicates` PRECIES ÉÉN keer aan met alle eigenschappen " +
  "die uit de tekst blijken (een lege lijst als er niets uit blijkt). Gebruik UITSLUITEND " +
  "de aangeboden predicaten. Verzin geen predicaten of tokens. Geef daarna geen verdere uitleg.";

/** Het vaste item-schema van één geëmit interactie — gedeeld door de losse en de
 * batch-toolvorm (#312/#323), zodat die twee per constructie dezelfde velden
 * accepteren. */
function interactionItemSchema() {
  const conditionSchema = z.object({
    on_kind: z.string(),
    subject_role: z.string().nullish(),
    window: z.string().nullish(),
    status: z.string().nullish(),
    value: z.string().nullish(),
    operator: z.string().nullish(),
  });
  return z.object({
    from: z.string(),
    to: z.string(),
    kind: z.string(),
    interacts: z.boolean(),
    explanation: z.string().nullish(),
    conditions: z.array(conditionSchema).optional(),
    // #315: de vorm blijft request-onafhankelijk (#312) — het veld bestaat
    // ALTIJD, ook als er geen secties zijn aangeboden; de sectie-poort zit in
    // de narekening. De .NET-kant liet het veld juist per request uit het
    // schema verdwijnen (BuildToolSchema), maar dat was precies de per-aanroep-
    // variatie die #312 uit de tool-definitie heeft gehaald.
    governed_by: z.string().nullish(),
  });
}

/** De vaste zod raw shape voor emit_interactions (#312). Vrije strings in plaats
 * van enums: het vocabulaire zit in de prompt en de poort in
 * {@link enforceInteractionVocabulary}. De vorm zelf (velden, types, verplicht/
 * optioneel) blijft exact die van vóór #312 — alleen de enum-poorten zijn naar
 * de narekening verhuisd. */
export function buildInteractionToolShape(): z.ZodRawShape {
  return { interactions: z.array(interactionItemSchema()) };
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
    // #315: de citeerbare sectie-refs als eigen vocabulaire-regel — zonder deze
    // regel kan het model niet weten wélke secties het als anker mag noemen, en
    // nult de narekening elke gok. Leeg = regel weg, net als de andere assen.
    vocabLine("governed_by (sectie-refs)", req.sections),
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
  // Sectie-refs zijn Ordinal-exact, en hier geldt de lege-lijst-fallback van
  // inLexicon bewust NIET: de .NET-muur toetst governed_by tegen een gewone
  // HashSet (leeg aangeboden = alles ge-nuld), dus "niets aangeboden ⇒ geen
  // poort" zou hier juist ruimer zijn dan de muur — en de narekening mag nooit
  // ruimer én nooit strenger zijn dan wat rb-api doet (#315).
  const sections = new Set(req.sections);
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

    // Anker-poort (#286/#315): een sectie die niet is aangeboden is verzonnen —
    // ge-nuld, niet geweigerd, exact zoals de .NET-muur (`sectionSet.Contains`
    // in InteractionExtraction.ParseDetailed) en zoals subject_role hierboven:
    // het anker kwijtraken mag het item niet kosten dat rb-api zou behouden.
    const governedByRaw = str(item.governed_by);
    const governedBy =
      governedByRaw !== null && sections.has(governedByRaw) ? governedByRaw : null;

    accepted.push({
      from,
      to,
      kind,
      interacts: item.interacts,
      ...(str(item.explanation) !== null ? { explanation: str(item.explanation)! } : {}),
      ...(conditions.length > 0 ? { conditions } : {}),
      ...(governedBy !== null ? { governed_by: governedBy } : {}),
    });
  }
  return { accepted, rejected, rejectedConditions };
}

// ── Batch-extractie: K kaarten per sessie (#323) ─────────────────────────────
//
// De vaste sessiekost (SDK-spawn + opstart, gemeten ~49 s bij 3 refs) werd tot
// #323 per kaart betaald. Eén sessie die K kaarten na elkaar behandelt
// amortiseert die kost; de randen uit de issue zijn hard: K blijft klein
// (nooit "de hele set"), de timeout schaalt mee met K (ai.ts), en
// kruisbesmetting wordt afgedwongen — elke tool-call draagt de kaartcode, een
// code buiten de aangeboden set wordt geweigerd en GETELD (unknown_code), en de
// vocabulaire-narekening draait PER KAART tegen het vocabulaire van díe kaart.

/** Harde bovengrens op K, aan déze kant van de lijn (defense-in-depth naast de
 * clamp in rb-api's beheerde instelling). LETTERLIJK 250 — expliciete
 * productkeuze van Sjoerd (een hele set in één context; de issue begon op
 * 5-15). De vangnetten bij grote K zijn partial salvage, de heartbeat per
 * kaart en de met K meeschalende timeout — niet een lagere grens. */
export const MAX_BATCH_CARDS = 250;

/** Eén kaart in een batch-request: eigen code, eigen tekst, eigen refs en eigen
 * citeerbare secties. De assen (kinds/rollen/lexica) zijn run-constanten en
 * reizen één keer mee op de envelop. */
export interface BatchCard {
  code: string;
  text: string;
  refs: OfferedRef[];
  sections: string[];
}

export interface InteractionBatchExtractRequest {
  system?: string;
  /** Zie {@link InteractionExtractRequest.model} (#323). */
  model?: string;
  kinds: string[];
  conditionKinds: string[];
  roles: string[];
  windowLexicon: string[];
  statusLexicon: string[];
  cards: BatchCard[];
}

/** Het per-kaart-vocabulaire als los {@link InteractionExtractRequest}, zodat de
 * bestaande narekening ({@link enforceInteractionVocabulary}) ONGEWIJZIGD per
 * kaart kan draaien. Dit is de anti-kruisbesmettingspoort: kaart A wordt tegen
 * de refs/secties van kaart A nagerekend, nooit tegen die van kaart B. */
export function batchCardRequest(
  req: InteractionBatchExtractRequest,
  card: BatchCard,
): InteractionExtractRequest {
  return {
    system: req.system,
    text: card.text,
    refs: card.refs,
    kinds: req.kinds,
    conditionKinds: req.conditionKinds,
    roles: req.roles,
    windowLexicon: req.windowLexicon,
    statusLexicon: req.statusLexicon,
    sections: card.sections,
  };
}

/** Server-side addendum voor de batchvorm: één tool-call per kaart, mét de
 * kaartcode, en uitsluitend het vocabulaire van díe kaart. */
export const BATCH_INTERACTION_TOOL_ADDENDUM =
  "Je krijgt meerdere genummerde kaarten. Roep de tool `emit_interactions` voor ELKE " +
  "kaart PRECIES ÉÉN keer aan, met `card` exact gelijk aan de aangeboden kaartcode. " +
  "Gebruik per kaart UITSLUITEND de refs, kinds, window/status-waarden en " +
  "governed_by-sectie-refs uit het vocabulaire dat bij DÍE kaart staat — nooit dat " +
  "van een andere kaart, en verzin er geen. Behandel de kaarten onafhankelijk van " +
  "elkaar. Geef daarna geen verdere uitleg.";

/** De vaste zod raw shape voor de batchvorm (#323): het losse interactie-item
 * plus de kaartcode die de vangst aan de juiste kaart bindt. */
export function buildBatchInteractionToolShape(): z.ZodRawShape {
  return { card: z.string(), interactions: z.array(interactionItemSchema()) };
}

export function batchInteractionToolDescription(): string {
  return (
    "Emit ontologie-begrensde, gekwalificeerde interacties voor één van de aangeboden " +
    "kaarten: `card` is de kaartcode uit de invoer, `interactions` gebruikt uitsluitend " +
    "het vocabulaire dat bij die kaart staat."
  );
}

/** De prompt-invoer voor de batchvorm: per kaart een genummerde kop met de code,
 * gevolgd door exact hetzelfde vocabulaire+tekst-blok als de losse vorm
 * ({@link interactionPromptText}) — de per-kaart-lokaliteit van het vocabulaire
 * is de eerste verdediging tegen kruisbesmetting; de narekening is de tweede. */
export function batchInteractionPromptText(req: InteractionBatchExtractRequest): string {
  const blocks = req.cards.map((card, i) => {
    const header = `=== Kaart ${i + 1} van ${req.cards.length} — code: ${card.code} ===`;
    return `${header}\n${interactionPromptText(batchCardRequest(req, card))}`;
  });
  return blocks.join("\n\n");
}

export function parseInteractionBatchExtractRequest(
  body: unknown,
): ExtractParseResult<InteractionBatchExtractRequest> {
  const b = asRecord(body);

  const kinds = stringArray(b.kinds);
  if (kinds.length === 0) return { ok: false, error: "kinds-enum vereist" };

  const model = parseExtractModelAlias(b.model);
  if (!model.ok) return { ok: false, error: model.error };

  const rawCards = Array.isArray(b.cards) ? b.cards : [];
  if (rawCards.length === 0) return { ok: false, error: "ten minste één kaart vereist" };
  if (rawCards.length > MAX_BATCH_CARDS)
    return { ok: false, error: `maximaal ${MAX_BATCH_CARDS} kaarten per batch` };

  const cards: BatchCard[] = [];
  const seen = new Set<string>();
  for (const raw of rawCards) {
    const c = asRecord(raw);
    const code = typeof c.code === "string" ? c.code.trim() : "";
    if (!code) return { ok: false, error: "elke kaart heeft een code nodig" };
    // Dubbele codes maken de vangst-toewijzing ambigu — weigeren, niet raden.
    if (seen.has(code)) return { ok: false, error: `dubbele kaartcode: ${code}` };
    seen.add(code);
    const text = typeof c.text === "string" ? c.text : "";
    if (!text.trim()) return { ok: false, error: `kaart ${code}: text vereist` };
    const rawRefs = Array.isArray(c.refs) ? c.refs : [];
    const refs: OfferedRef[] = [];
    for (const rr of rawRefs) {
      const r = asRecord(rr);
      const ref = typeof r.ref === "string" ? r.ref.trim() : "";
      const label = typeof r.label === "string" ? r.label : ref;
      if (ref) refs.push({ ref, label });
    }
    if (refs.length === 0)
      return { ok: false, error: `kaart ${code}: ten minste één ref vereist` };
    cards.push({ code, text, refs, sections: stringArray(c.sections) });
  }

  return {
    ok: true,
    request: {
      system: optionalSystem(b.system),
      ...(model.model ? { model: model.model } : {}),
      kinds,
      conditionKinds: stringArray(b.conditionKinds),
      roles: stringArray(b.roles),
      windowLexicon: stringArray(b.windowLexicon),
      statusLexicon: stringArray(b.statusLexicon),
      cards,
    },
  };
}

// ── Mechanic-predicaten (spiegelt MechanicPredicateExtraction) ───────────────

export interface PredicateExtractRequest {
  system?: string;
  /** Zie {@link InteractionExtractRequest.model} (#323). */
  model?: string;
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

  // Model-alias (#323): onbekend is een 400 vóór er ook maar één SDK-sessie of
  // permit aan te pas komt — nooit een vrije string richting de SDK-options.
  const model = parseExtractModelAlias(b.model);
  if (!model.ok) return { ok: false, error: model.error };

  return {
    ok: true,
    request: {
      system: optionalSystem(b.system),
      ...(model.model ? { model: model.model } : {}),
      text,
      refs,
      kinds,
      conditionKinds: stringArray(b.conditionKinds),
      roles: stringArray(b.roles),
      windowLexicon: stringArray(b.windowLexicon),
      statusLexicon: stringArray(b.statusLexicon),
      // #315: rb-api stuurt de citeerbare sectie-refs sinds #286 mee, maar dit
      // veld werd hier nooit gelezen — de GOVERNED_BY-verankering bestond
      // daardoor alleen op papier. Afwezig = lege lijst: een oudere rb-api
      // zonder sections blijft gewoon werken (governed_by wordt dan altijd
      // ge-nuld, wat de .NET-muur toch al deed).
      sections: stringArray(b.sections),
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

  const model = parseExtractModelAlias(b.model);
  if (!model.ok) return { ok: false, error: model.error };

  return {
    ok: true,
    request: {
      system: optionalSystem(b.system),
      ...(model.model ? { model: model.model } : {}),
      text,
      subjectRef,
      subjectLabel,
      predicates,
      objectHints: stringArray(b.objectHints),
    },
  };
}
