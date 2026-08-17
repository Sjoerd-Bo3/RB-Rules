// Faaldiagnostiek voor rb-ai (#281). PUUR (geen Agent SDK, geen IO behalve de
// ene `console.log` in `logEvent`) zodat het volledig unit-testbaar is. Sinds
// #292 is die ene regel ook letterlijk de enige stdout-schrijver van de hele
// sidecar — zie {@link logEvent}.
//
// AANLEIDING. Een mining-run over 40 kaarten meldde `22 rb-ai-uitval (5xx×22)`
// terwijl `docker logs rb-v2-ai` precies ÉÉN regel bevatte: de opstartregel.
// Meer dan de helft van de kaarten haalde de LLM niet, en van buitenaf was niet
// te zien waarom. De verklaring zit in twee stille gaten:
//
//  1. **De Agent SDK GOOIT niet bij een mislukte run.** Een run die vastloopt
//     (max beurten op, API-fout, subprocess om) eindigt met een gewoon
//     `result`-bericht met `subtype: "error_*"`, `is_error: true`, een
//     `api_error_status` en een `errors[]`-lijst. `collectAnswer` las daar
//     alleen `result`/`usage` uit en gooide de rest weg; `extractWithTool`
//     las de berichtenstroom zelfs volledig leeg (`for await (const _ of …)`).
//     Zo werd elke mislukte run een kale `captured === null` → `500 {"error":
//     "extractie mislukt"}` zonder één spoor van de oorzaak.
//  2. **rb-api gooide de foutbody weg.** `RbAiClient` logde alleen de
//     statuscode, dus zelfs een sprekende body kwam nergens aan.
//
// Dit bestand levert het vocabulaire (`AiFailureReason`), de twee vertalers
// (`describeThrown` voor een geworpen fout, `resultFailure` voor het
// SDK-resultaatbericht) en de log-regel. Werkafspraak 7 is hier hard:
// `redactSecrets` draait over ELKE tekst die naar buiten gaat, en `logCall`
// schrijft nooit prompt-inhoud — alleen endpoint, duur, uitkomst en oorzaak.

/** Machine-leesbare uitvalsoort van één rb-ai-aanroep. Reist mee in de
 * foutbody zodat rb-api hem in de per-oorzaak-telling van #251 kan opnemen
 * (`AiOutcomeTally`), en staat in de containerlog voor het geval de run zelf
 * niet meer te raadplegen is. Bewust grofmazig: elke waarde wijst naar een
 * ANDERE knop. */
export type AiFailureReason =
  /** SDK-run eindigde met `subtype: "error_during_execution"` — de subprocess
   * liep vast of viel om. Wijst naar de machine (geheugen, spawn), niet naar
   * de prompt. */
  | "sdk_error"
  /** `error_max_turns`: de run raakte `maxTurns` op zonder af te ronden. Wijst
   * naar de beurten-begroting of naar een model dat de tool niet vindt. */
  | "max_turns"
  /** `error_max_budget_usd` / `error_max_structured_output_retries` — een
   * SDK-begrenzing anders dan beurten. */
  | "sdk_limit"
  /** Het resultaat droeg een `api_error_status`: Anthropic gaf zelf een
   * HTTP-fout terug (529 overbelast, 500, …). Wijst naar de dienst, niet naar
   * ons. */
  | "api_error"
  /** Auth: token ontbreekt, is verlopen of werd geweigerd (401/403). */
  | "auth"
  /** Het gevraagde MODEL(-variant) bestaat niet of is niet beschikbaar op dit
   * abonnement (#323) — bv. een 1M-contextvariant (`claude-fable-5[1m]`) die
   * het plan niet draagt. Eigen reden, want de knop is een ándere dan bij
   * `api_error`: niet wachten of opnieuw proberen, maar in beheer een andere
   * model-alias kiezen. */
  | "model_unavailable"
  /** Het subprocess kon niet starten of viel om (ENOMEM, ENOENT, EPIPE,
   * SIGKILL). Op een krappe VM is dit het scenario dat GEEN container-restart
   * en GEEN OOMKilled-vlag achterlaat — de kernel killt het kind, niet de
   * container. */
  | "spawn"
  /** Onze eigen harde timeout sloeg toe (EXTRACT_TIMEOUT_MS e.d.). */
  | "timeout"
  /** De client liep weg; de call is afgebroken. Geen uitval van rb-ai. */
  | "aborted"
  /** De run slaagde, maar de GEFORCEERDE tool werd nooit geroepen — het model
   * antwoordde met tekst. Wijst naar de prompt/het schema, niet naar de
   * machine. */
  | "no_tool_call"
  /** De run slaagde, maar leverde geen bruikbare tekst op. */
  | "empty_answer"
  /** De permissiepoort weigerde de tool (allowlist/permissionMode). */
  | "permission_denied"
  /** ONZE EIGEN semaphore wees de aanvraag af (#155/#279) — geen uitval van de
   * LLM maar van de cap. Staat hier zodat de logregel van een 429 dezelfde vorm
   * heeft als die van een 500; rb-api kende dit onderscheid al
   * (`AiCallOutcome.ConcurrencyLimited`) via de `code` in de body. */
  | "concurrency_limit"
  /** Niet in te delen — de detail-tekst is dan het enige spoor. */
  | "unknown";

/** Uitvalsoort + een KORTE, ge-redacte toelichting. `detail` is bedoeld voor
 * mensenogen (containerlog, run-detail) en nooit voor logica. */
export interface AiFailure {
  reason: AiFailureReason;
  detail: string;
}

/** Een uitval die we AL geclassificeerd hebben, als Error zodat hij het
 * bestaande throw-pad kan volgen zonder de reden onderweg te verliezen.
 * `askClaude` gooit deze wanneer de SDK een mislukte run meldde; server.ts
 * herkent hem via {@link failureOf} en zet reden + detail in de foutbody. */
export class AiRunError extends Error {
  readonly failure: AiFailure;

  constructor(failure: AiFailure) {
    super(`${failure.reason}: ${failure.detail}`);
    this.name = "AiRunError";
    this.failure = failure;
  }
}

/** De uitvalsoort van een geworpen fout: de al-geclassificeerde variant als het
 * een {@link AiRunError} is, anders een verse vertaling. Eén poort, zodat een
 * eenmaal vastgestelde reden nooit stilletjes op "unknown" terugvalt. */
export function failureOf(e: unknown): AiFailure {
  return e instanceof AiRunError ? e.failure : describeThrown(e);
}

/** Harde bovengrens op elke toelichting. Houdt de logregel leesbaar én beperkt
 * de schade als er ooit iets langs de redactie glipt. */
const MAX_DETAIL = 300;

/** Env-variabelen waarvan de WAARDE nooit in een logregel mag belanden. De
 * lijst is een vangnet naast de patronen hieronder: die dekken het gangbare
 * formaat, dit dekt de exacte waarde ongeacht vorm. Bewust per aanroep gelezen
 * (niet bij module-load) zodat een test hem kan zetten en de container hem na
 * een herstart nog steeds ziet. */
const SECRET_ENV_PATTERN = /(TOKEN|KEY|SECRET|PASSWORD|CREDENTIAL)/i;

/** Minimale lengte voordat een env-waarde als secret wordt behandeld. Onder
 * deze grens is de kans op een toevallige match (bv. `KEY=1`) groter dan de
 * winst, en zou de redactie zelf ruis worden. */
const MIN_SECRET_LENGTH = 8;

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Verwijder alles wat op een secret lijkt uit `text` (werkafspraak 7).
 *
 * Vier lagen, van specifiek naar generiek:
 *  1. de letterlijke waarden van elke env-variabele die TOKEN/KEY/SECRET/…
 *     heet — dit vangt `CLAUDE_CODE_OAUTH_TOKEN` en `ANTHROPIC_API_KEY`
 *     ongeacht hun vorm;
 *  2. bekende sleutelvormen (`sk-ant-…`, `sk-…`);
 *  3. dragervormen (`Bearer …`, `authorization: …`, `token=…`);
 *  4. een generiek vangnet op lange, spatieloze, ondoorzichtige runs.
 *
 * Bewust agressief: een te veel geredacte foutmelding kost hooguit een beetje
 * diagnostisch comfort, een gelekt token kost het abonnement. */
export function redactSecrets(text: string): string {
  let out = text;

  for (const [name, value] of Object.entries(process.env)) {
    if (!SECRET_ENV_PATTERN.test(name)) continue;
    if (!value || value.length < MIN_SECRET_LENGTH) continue;
    out = out.replaceAll(value, "[redacted]");
  }

  out = out.replace(/\bsk-ant-[A-Za-z0-9_-]+/g, "[redacted]");
  out = out.replace(/\bsk-[A-Za-z0-9_-]{16,}/g, "[redacted]");
  out = out.replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/gi, "Bearer [redacted]");
  out = out.replace(
    /\b(authorization|x-api-key|api[-_]?key|auth[-_]?token|oauth[-_]?token|access[-_]?token)\b(\s*[:=]\s*)("?)[^\s",}]+\3/gi,
    "$1$2[redacted]",
  );
  // Vangnet: een lange spatieloze run met zowel letters als cijfers is vrijwel
  // nooit proza en vrijwel altijd een sleutel of een blob.
  out = out.replace(/[A-Za-z0-9_\-+/=]{32,}/g, (match) =>
    /[0-9]/.test(match) && /[A-Za-z]/.test(match) ? "[redacted]" : match,
  );

  return out;
}

/** Redacteer én kap af. Elke tekst die dit bestand naar buiten laat gaat hier
 * doorheen — één poort, zodat er geen tweede pad kan ontstaan dat hem mist.
 *
 * DE VOLGORDE IS LOAD-BEARING (#281-review): eerst redacteren, DAN afkappen.
 * Andersom kan `MAX_DETAIL` midden door een token snijden, waarna het
 * overgebleven fragment te kort is voor de patronen hieronder en alsnog in de
 * log belandt. Wie dit ooit "opruimt" tot kappen-dan-redacteren maakt er een
 * lek van. */
export function safeDetail(text: string): string {
  const cleaned = redactSecrets(text).replace(/\s+/g, " ").trim();
  return cleaned.length > MAX_DETAIL ? `${cleaned.slice(0, MAX_DETAIL)}…` : cleaned;
}

/** Patronen in een foutmelding die naar een subprocess-probleem wijzen. Op een
 * krappe VM is dit het stilste faalpad dat er is: de kernel killt het
 * Claude-subprocess, de rb-ai-container zelf blijft ver onder zijn limiet en
 * herstart nooit — precies het beeld uit #281 (`OOMKilled=false`, `restarts=0`,
 * ~120 MiB van 2,44 GiB, en tien minuten eerder een door de kernel gekilde
 * `llama-server` van 2,5 GB op dezelfde machine).
 *
 * De eerste drie regels komen LETTERLIJK uit de SDK
 * (`node_modules/@anthropic-ai/claude-agent-sdk/sdk.mjs`, `getProcessExitError`
 * / de spawn-handler) — geraden patronen zouden hier precies het geval missen
 * waarvoor ze bedoeld zijn. */
const SPAWN_PATTERNS = [
  /failed to spawn/i,
  /process exited with code/i,
  /process terminated by signal/i,
  /\bENOMEM\b/i,
  /\bENOENT\b/i,
  /\bEPIPE\b/i,
  /\bEAGAIN\b/i,
  /\bSIGKILL\b/i,
  /\bSIGSEGV\b/i,
  /out of memory/i,
  /spawn\s+\S+\s+(failed|error)/i,
  /(child|sub)process\b.*(exit|kill|died|crash)/i,
];

const AUTH_PATTERNS = [
  /\b401\b/,
  /\b403\b/,
  /unauthor/i,
  /forbidden/i,
  /invalid[_\s-]?api[_\s-]?key/i,
  /authentication[_\s-]?error/i,
  /(oauth|token).{0,20}(expired|invalid|missing|revoked)/i,
  /not logged in/i,
];

/** De tekst waaruit we een geworpen fout beoordelen: `name: message (cause: …)`.
 *
 * BEWUST GEEN `e.stack` (#281-review): een stack trace draagt frames, paden en
 * soms geïnterpoleerde argumenten mee — een onbegrensd kanaal waar van alles in
 * kan zitten. `name` + `message` + `cause` is genoeg om de faalsoort te bepalen,
 * en is begrensd tot wat de werper zelf formuleerde. Voeg `stack` hier niet toe
 * "voor de diagnose": dat vergroot het lekoppervlak zonder de classificatie te
 * verbeteren.
 *
 * `cause` draagt bij de SDK vaak de ECHTE systeemfout (spawn/ENOMEM) achter een
 * generieke wrapper; zonder meelezen blijft die onzichtbaar. Eén helper voor
 * {@link describeThrown} én {@link logEvent} (#295-review): die twee gaven
 * eerst een tegengesteld oordeel over `cause` binnen hetzelfde bestand — de een
 * las hem bewust wél, de ander gooide hem weg. NIET redacterend: elke aanroeper
 * haalt het resultaat door {@link safeDetail}. */
function renderThrown(e: unknown): string {
  const name = e instanceof Error ? e.name : typeof e;
  const message = e instanceof Error ? e.message : String(e);
  const cause =
    e instanceof Error && e.cause !== undefined
      ? ` (cause: ${e.cause instanceof Error ? `${e.cause.name}: ${e.cause.message}` : String(e.cause)})`
      : "";
  return `${name}: ${message}${cause}`;
}

/** Vertaal een GEWORPEN fout naar een uitvalsoort. Wordt gebruikt op elk pad
 * waar `query()` of de omliggende bedrading daadwerkelijk gooit — anders dan
 * bij een mislukte run, die als resultaatbericht binnenkomt
 * (zie {@link resultFailure}). */
export function describeThrown(e: unknown): AiFailure {
  const name = e instanceof Error ? e.name : typeof e;
  const haystack = renderThrown(e);
  const detail = safeDetail(haystack);

  if (name === "AbortError" || /\babort/i.test(haystack)) return { reason: "aborted", detail };
  if (AUTH_PATTERNS.some((p) => p.test(haystack))) return { reason: "auth", detail };
  if (SPAWN_PATTERNS.some((p) => p.test(haystack))) return { reason: "spawn", detail };
  if (/\btimed?[\s_-]?out\b|\btimeout\b/i.test(haystack)) return { reason: "timeout", detail };
  if (/\b(429|500|502|503|529)\b|overloaded|api[_\s-]?error/i.test(haystack))
    return { reason: "api_error", detail };
  return { reason: "unknown", detail };
}

/** De velden van het SDK-`result`-bericht die we lezen. Bewust structureel
 * getypeerd in plaats van de SDK-types te importeren: `collectAnswer` krijgt
 * een `AsyncIterable<unknown>` binnen (ook uit de warme pool en uit tests), en
 * een structurele vorm houdt dit bestand vrij van de SDK. */
export interface SdkResultLike {
  type?: string;
  subtype?: string;
  is_error?: boolean;
  api_error_status?: number | null;
  num_turns?: number;
  errors?: unknown;
  permission_denials?: unknown;
  result?: string;
}

/** Lees de uitvalsoort uit een SDK-`result`-bericht, of null als de run
 * gewoon slaagde.
 *
 * DIT is het gat uit #281: de SDK meldt een mislukte run niet met een
 * exception maar met dit bericht, en rb-ai negeerde het volledig. `subtype`
 * onderscheidt max-beurten van een uitvoeringsfout, `api_error_status` verraadt
 * een fout aan Anthropics kant, en `errors[]` draagt de tekst waar we het al
 * die tijd zonder deden. */
export function resultFailure(message: unknown): AiFailure | null {
  if (typeof message !== "object" || message === null) return null;
  const m = message as SdkResultLike;
  if (m.type !== "result") return null;

  const errors = Array.isArray(m.errors)
    ? m.errors.filter((x): x is string => typeof x === "string")
    : [];
  const denials = Array.isArray(m.permission_denials) ? m.permission_denials.length : 0;
  const subtype = typeof m.subtype === "string" ? m.subtype : "";
  const status = typeof m.api_error_status === "number" ? m.api_error_status : null;

  // Een geslaagd resultaat zonder foutvlag, foutstatus of geweigerde tool is
  // gewoon een geslaagde run — niets te melden.
  if (subtype === "success" && !m.is_error && status === null && denials === 0) return null;
  if (subtype !== "success" || m.is_error || status !== null || denials > 0) {
    const parts = [
      subtype ? `subtype=${subtype}` : null,
      status !== null ? `api_status=${status}` : null,
      typeof m.num_turns === "number" ? `turns=${m.num_turns}` : null,
      denials > 0 ? `permission_denials=${denials}` : null,
      errors.length > 0 ? errors.join(" | ") : null,
    ].filter((p): p is string => p !== null);
    const detail = safeDetail(parts.join(" "));
    return { reason: resultReason(subtype, status, denials, detail), detail };
  }
  return null;
}

/** Herkent een model-weigering in de fouttekst van het RESULT-bericht (#323).
 * CLASSIFIER, geen passthrough-poort (#300-les): hij draait uitsluitend over
 * tekst waarvan al vaststaat dat het een SDK-/API-foutmelding is, dus "matcht
 * ergens" is hier de juiste vorm. De patronen eisen het woord "model" mét een
 * weiger-woord in de buurt — een kale 404 blijft `api_error`. */
const MODEL_UNAVAILABLE_PATTERNS = [
  /model[^,;|]{0,80}(not[\s_-]?found|not[\s_-]?available|unavailable|not[\s_-]?supported|invalid|unknown)/i,
  /(not[\s_-]?found|unknown|invalid|unsupported)[^,;|]{0,40}\bmodel\b/i,
  /no access to[^,;|]{0,60}\bmodel\b/i,
];

function resultReason(
  subtype: string,
  status: number | null,
  denials: number,
  detail: string,
): AiFailureReason {
  if (subtype === "error_max_turns") return "max_turns";
  // Auth vóór de generieke api_error: 401/403 wijst naar het token, niet naar
  // de belasting van de dienst — een andere knop.
  if (status === 401 || status === 403) return "auth";
  // Model-weigering vóór de generieke api_error (#323): "variant niet
  // beschikbaar op dit abonnement" is een beheer-knop (andere alias kiezen),
  // geen dienst-storing. Eén keer geclassificeerd, hier — de catch mag een
  // al-geclassificeerde fout nooit herclassificeren (#300-les).
  if (MODEL_UNAVAILABLE_PATTERNS.some((p) => p.test(detail))) return "model_unavailable";
  if (status !== null) return "api_error";
  if (denials > 0) return "permission_denied";
  if (subtype === "error_max_budget_usd" || subtype === "error_max_structured_output_retries")
    return "sdk_limit";
  if (subtype === "error_during_execution") {
    // De SDK gebruikt dit subtype ook voor een omgevallen subprocess; de
    // errors[]-tekst is dan het enige onderscheid dat er is.
    const spawn = SPAWN_PATTERNS.some((p) => p.test(detail));
    return spawn ? "spawn" : "sdk_error";
  }
  return "sdk_error";
}

// ── SDK-interne retries: de gemeten oorzaak van #281 ────────────────────────
//
// Een probe met een ongeldige sleutel legde het patroon bloot dat de hele issue
// verklaart. De Agent SDK probeert een mislukte API-call ZELF opnieuw — tot
// `max_retries` (10) met exponentiële backoff — en meldt elke poging als een
// gewoon systeembericht:
//
//   {"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,
//    "retry_delay_ms":540,"error_status":401,"error":"authentication_failed"}
//
// Gemeten wachttijden: 0,5s → 1,0s → 2,3s → 4,5s → 9,6s → 16,4s → 32,1s → …
// Ná zeven pogingen is er 37 seconden verstreken en is er nog geen enkel token
// verwerkt; poging 8 en 9 duwen het totaal ruim voorbij `EXTRACT_TIMEOUT_MS`
// (90 s). Onze AbortController hakt de run dan af.
//
// Het gevolg was precies het beeld uit #281: een aanhoudende API-fout (429,
// 500, 529) verscheen NIET als die fout, maar als een generieke rb-ai-500 —
// zonder logregel, zonder statuscode, met een container die nauwelijks geheugen
// gebruikte omdat het subprocess vooral lag te WACHTEN. Dat verklaart ook
// waarom #251 concludeerde "5xx, geen rate-limit": de 5xx was ónze eigen
// timeout-vertaling; de rate-limit zat een laag dieper, onzichtbaar.
//
// Deze tracker maakt die laag zichtbaar.

/** Eén waargenomen SDK-retry. `status` is null bij verbindingsfouten zonder
 * HTTP-antwoord (zo documenteert de SDK het). */
export interface RetryNote {
  attempt: number;
  maxRetries: number | null;
  status: number | null;
  error: string | null;
}

/** Lees een `api_retry`-systeembericht, of null als dit een ander bericht is. */
export function retryNote(message: unknown): RetryNote | null {
  if (typeof message !== "object" || message === null) return null;
  const m = message as Record<string, unknown>;
  if (m.type !== "system" || m.subtype !== "api_retry") return null;
  return {
    attempt: typeof m.attempt === "number" ? m.attempt : 0,
    maxRetries: typeof m.max_retries === "number" ? m.max_retries : null,
    status: typeof m.error_status === "number" ? m.error_status : null,
    error: typeof m.error === "string" ? m.error : null,
  };
}

/** Telt de SDK-retries van één run en onthoudt de laatste. Zonder deze telling
 * ziet een timeout eruit als "het model was traag", terwijl de run in
 * werkelijkheid grotendeels in backoff zat op een fout die de SDK bleef
 * herhalen — een compleet andere knop. */
export class RetryTracker {
  private count = 0;
  private last: RetryNote | null = null;

  /** Voed elk SDK-bericht hierin; niet-retry-berichten zijn een no-op. */
  observe(message: unknown): void {
    const note = retryNote(message);
    if (!note) return;
    this.count += 1;
    this.last = note;
  }

  get retries(): number {
    return this.count;
  }

  /** Korte samenvatting voor de logregel, of "" als er niets te melden is. */
  summary(): string {
    if (this.count === 0 || !this.last) return "";
    const parts = [`${this.count} SDK-retries`];
    const limit = this.last.maxRetries;
    if (limit !== null) parts.push(`van max ${limit}`);
    const status = this.last.status === null ? "geen HTTP-antwoord" : `HTTP ${this.last.status}`;
    parts.push(`laatste: ${status}${this.last.error ? ` ${this.last.error}` : ""}`);
    return parts.join(", ");
  }

  /** De uitvalsoort waar deze retries naar wijzen, of null zonder retries. Een
   * 401/403 wijst naar het token, al het overige naar de dienst. */
  reason(): AiFailureReason | null {
    if (this.count === 0 || !this.last) return null;
    return this.last.status === 401 || this.last.status === 403 ? "auth" : "api_error";
  }
}

/** Verrijk een uitval met wat de SDK-retries vertelden.
 *
 * Bij een TIMEOUT wint de retry-oorzaak: dat is de kern van #281. Een run die
 * na zeven mislukte API-pogingen in onze harde timeout loopt is geen trage
 * LLM-run maar een aanhoudende API-fout, en dat is de knop die je wilt zien.
 * De timeout blijft wel in de detail-tekst staan — de run is immers echt
 * afgekapt. Andere oorzaken (max_turns, spawn) blijven leidend; de retries gaan
 * daar alleen als context achteraan. */
export function withRetries(failure: AiFailure, retries: RetryTracker): AiFailure {
  const summary = retries.summary();
  if (!summary) return failure;
  const upstream = retries.reason();
  const reason = failure.reason === "timeout" && upstream ? upstream : failure.reason;
  return { reason, detail: safeDetail(`${failure.detail} | ${summary}`) };
}

/** Machine-leesbare code op een afgekapte extractie (#281) — zelfde vorm als
 * `concurrency_limit` (#279), zodat rb-api de oorzaak niet uit proza hoeft te
 * raden. De 504 draagt hem al; de code is de expliciete bevestiging. */
export const EXTRACT_TIMEOUT_CODE = "extract_timeout";

/** Het HTTP-antwoord op een MISLUKTE extractie, als pure functie (#281-review).
 *
 * Dit is de kern van #281 in één beslissing: drie totaal verschillende oorzaken
 * vielen samen in één ononderscheidbare `500 {"error":"extractie mislukt"}` —
 * het model rondde af zonder de geforceerde tool te roepen, onze tijdslimiet
 * sloeg toe, of er ging echt iets stuk. Een afgekapte run krijgt daarom **504**
 * plus een `code`; `RbAiClient.Classify` vertaalt 504 al naar
 * `AiCallOutcome.Timeout`, dus het run-detail meldt "timeout×22" in plaats van
 * "5xx×22" zonder dat rb-api een nieuwe enum-waarde nodig heeft.
 *
 * Puur en apart van server.ts omdat die bij import meteen gaat luisteren: zo is
 * de beslissing te toetsen op GEDRAG in plaats van met een grep op de
 * broncode — en een grep-test vangt zijn eigen bug niet. */
export function extractFailureResponse(outcome: {
  failure?: AiFailure;
  timedOut?: boolean;
}): { status: number; error: string; code?: string; failure: AiFailure } {
  const failure = outcome.failure ?? { reason: "unknown" as const, detail: "" };
  return outcome.timedOut
    ? {
        status: 504,
        error: "extractie afgebroken op de tijdslimiet",
        code: EXTRACT_TIMEOUT_CODE,
        failure,
      }
    : { status: 500, error: "extractie mislukt", failure };
}

/** Regels in een stderr-staart die aantoonbaar over de MACHINE gaan (#300).
 *
 * Dit is een GESLOTEN vocabulaire, en dat is de hele reden dat het bestaat.
 * `StderrTail` vangt een ongecontroleerde stroom: wat het Claude-subprocess
 * print komt erin, en er is geen structuur die "diagnostiek" van "toevallig
 * meegeëchode invoer" scheidt. Op het extract-pad is dat residu aanvaard omdat
 * de invoer daar publieke Riot-kaarttekst is; op /ask is de invoer de VRAAG VAN
 * EEN BEZOEKER, en dan gaat de afweging de andere kant op (zie
 * {@link withStderrDigest}).
 *
 * De oplossing is dezelfde als bij de brein-stappen in #292: niet "beter
 * redacteren" — `safeDetail` haalt secrets weg, geen gebruikersinvoer — maar de
 * inhoud NIET meegeven. Alleen regels die op dit lijstje matchen worden
 * doorgelaten (bij een match reist de hele regel mee); de rest wordt geteld,
 * niet geciteerd.
 *
 * BEWUST NIET `SPAWN_PATTERNS` + `AUTH_PATTERNS` hergebruikt (#300-review). Die
 * zijn gebouwd als CLASSIFIERS: gegeven een tekst waarvan al vaststaat dat het
 * een machinefout is, wélke knop is het — daar is "matcht ergens" precies goed.
 * Hier is de rol omgekeerd: een POORT die van een WILLEKEURIGE regel beslist of
 * hij door mag, en dan is "matcht ergens" juist gevaarlijk. `forbidden`, `401`,
 * `Killed`, `token invalid` zijn gewone woorden die een speler in zijn vraag
 * tikt ("Is the **Forbidden** Idol banned", "if my unit is **Killed** in
 * combat") — hergebruik lekte 6 van 8 natuurlijke vragen als hele regel. Zelfde
 * verwarring als de tie-break-les van #206: een predicaat dat in de ene rol
 * klopt, klopt niet vanzelf in de andere.
 *
 * Daarom twee soorten patroon, elk met zijn eigen veiligheidsargument:
 *  - TOKENS die in geen enkele natuurlijke Riftbound-vraag voorkomen (errno,
 *    signalen, `heap out of memory`) — die mogen overal in de regel matchen;
 *  - PREFIXEN van echte machine-regels, verankerd aan regel-START (`^`), zodat
 *    een vraag die zo'n woord ergens in het midden bevat niet per ongeluk zijn
 *    hele regel doorlaat.
 *
 * Auth staat er bewust NIET meer in: een 401/403 wordt al geclassificeerd uit
 * het `result`-bericht en de `RetryTracker` (`api_error_status`), niet uit
 * stderr — we verliezen dus geen diagnose door het uit de passthrough te halen,
 * en we winnen dat `forbidden`/`401` geen willekeurige vraagregel meer
 * doorlaten. Uitbreiden mag, maar alleen met een token dat geen natuurlijke
 * taal is, of een aan `^` verankerde prefix van een echte SDK-/node-/kernel-
 * regel — nooit met een los woord dat "meestal wel een foutmelding is". */
const MACHINE_STDERR_PATTERNS = [
  // Tokens/frasen die geen natuurlijke Riftbound-vraag zijn — veilig overal in
  // de regel. `process exited with code N` en `process terminated by signal`
  // zijn de letterlijke SDK-vormen (`getProcessExitError` in `sdk.mjs`), dus de
  // "Claude Code process …"-regels vallen hieronder zonder aparte prefix.
  /\b(ENOMEM|ENOENT|EPIPE|EAGAIN|EACCES|EMFILE|ENFILE|ETIMEDOUT|ECONNRESET|ECONNREFUSED)\b/,
  /\bSIG(KILL|SEGV|TERM|ABRT|BUS|FPE)\b/,
  /heap out of memory/i,
  /\bprocess exited with code \d+/,
  /\bprocess terminated by signal\b/,
  // Prefixen van echte machine-regels — verankerd aan regel-START, zodat alleen
  // een regel die ZO BEGINT doorgaat, niet een vraag die het woord ergens bevat.
  /^Failed to spawn\b/,
  /^[A-Z][A-Za-z]*Error:/, // Error:, TypeError:, RangeError: …
  /^FATAL ERROR\b/,
  /^Cannot find module\b/,
  /^Killed$/, // de bash-OOM-killer print exact deze regel, in z'n eentje
];

/** Hoeveel machine-regels er hooguit uit een staart gemeld worden, en hoe lang
 * zo'n regel mag zijn. Samen ruim onder {@link MAX_DETAIL}, zodat de rest van de
 * toelichting (de reden, de SDK-retries) niet achter de stderr wegvalt. */
const MAX_MACHINE_LINES = 3;
const MAX_MACHINE_LINE_LENGTH = 160;

/** Wat een stderr-staart over de machine zegt, zonder wat er verder in staat. */
export interface StderrDigest {
  /** Totaal aantal bytes dat het subprocess naar stderr schreef — inclusief wat
   * inmiddels uit de ringbuffer geschoven is. Een MAAT, geen inhoud, en op
   * zichzelf al een signaal: een subprocess dat 40 kB uitbraakt en dan omvalt
   * vertelt iets anders dan een dat zwijgend verdwijnt. */
  bytes: number;
  /** De regels die op {@link MACHINE_STDERR_PATTERNS} matchen, afgekapt. */
  machine: string[];
  /** Aantal regels dat NIET gemeld wordt (niet herkend, of boven de cap).
   * Zonder deze telling valt "het subprocess zei niets" niet te onderscheiden
   * van "het zei van alles, maar niets wat wij mogen citeren". */
  withheld: number;
}

/** Ringbuffer voor de stderr van het Claude-subprocess (#281).
 *
 * De Agent SDK biedt een `stderr`-callback op `Options`; die gebruikten we
 * niet, dus alles wat het subprocess over zijn eigen ellende te melden had
 * verdween. Dat is precies de informatie die ontbrak toen 22 aanroepen faalden
 * zonder één logregel. Let op dat de callback niet alleen een AFNEMER is maar
 * een SCHAKELAAR: de SDK spawnt met `stdio:[…,…, options.stderr ? "pipe" :
 * "ignore"]`, dus zonder de optie wordt de stroom niet eens opgevangen (#300).
 *
 * Waarom een buffer en geen directe doorvoer naar stdout: op een geslaagde run
 * is die uitvoer ruis (voortgang, waarschuwingen) die de logs onbruikbaar zou
 * maken, en bij 40 kaarten per run telt dat op. We houden daarom alleen de
 * LAATSTE `limit` tekens vast en gooien ze pas naar buiten als de aanroep
 * daadwerkelijk mislukt — dan is de staart van stderr meestal precies de
 * oorzaak. Redactie gebeurt bij het uitlezen: het subprocess kan een
 * auth-header of tokenfragment naar stderr schrijven.
 *
 * ÉÉN BUFFER, TWEE LEESVORMEN. {@link tail} geeft de volledige staart en is
 * bedoeld voor paden waar de invoer publiek is; {@link digest} geeft alleen de
 * machine-diagnostiek plus maten, voor paden met gebruikersinvoer. De keuze
 * hoort bij het PAD, niet bij de buffer — vandaar dat de ruwe buffer privé
 * blijft en er geen accessor voor is. */
export class StderrTail {
  private buffer = "";
  private bytes = 0;

  constructor(private readonly limit = 2000) {}

  append(chunk: string): void {
    // Vóór het afkappen tellen: de MAAT moet blijven kloppen ook als de inhoud
    // allang uit de ringbuffer geschoven is.
    this.bytes += Buffer.byteLength(chunk, "utf8");
    this.buffer += chunk;
    if (this.buffer.length > this.limit) {
      this.buffer = this.buffer.slice(this.buffer.length - this.limit);
    }
  }

  /** De VOLLEDIGE ge-redacte staart, of een lege string als het subprocess
   * niets zei. Alleen voor paden waar de invoer publiek is (de extractie-
   * endpoints, zie {@link withStderr}). */
  tail(): string {
    return safeDetail(this.buffer);
  }

  /** Alleen wat over de machine gaat, plus maten (#300). Voor paden waar er
   * gebruikersinvoer in het spel is. */
  digest(): StderrDigest {
    const machine: string[] = [];
    let withheld = 0;
    for (const raw of this.buffer.split(/\r?\n/)) {
      const line = raw.trim();
      if (!line) continue;
      if (
        machine.length < MAX_MACHINE_LINES &&
        MACHINE_STDERR_PATTERNS.some((p) => p.test(line))
      )
        // Redacteren VÓÓR het afkappen (#281-regel, ook hier): een `slice` die
        // een secret doormidden snijdt laat een fragment achter dat te kort is
        // voor de patronen en alsnog lekt. `redactSecrets` matcht op de patroon-
        // grens (`\b`), niet op de regel-startankers hierboven, dus dit
        // verandert de PASSTHROUGH-beslissing niet — die is al gevallen op de
        // ongeredacteerde regel — alleen wat er van de doorgelaten regel
        // overblijft.
        machine.push(redactSecrets(line).slice(0, MAX_MACHINE_LINE_LENGTH));
      else withheld += 1;
    }
    return { bytes: this.bytes, machine, withheld };
  }
}

/** Plak de VOLLEDIGE stderr-staart achter een detail-tekst, als er iets te
 * melden valt. Eén plek, zodat elk faalpad dezelfde vorm oplevert.
 *
 * ALLEEN VOOR DE EXTRACTIE-ENDPOINTS. Het residu (prompt-inhoud kan meeliften)
 * is daar aanvaard omdat de invoer publieke Riot-kaarttekst is — die afweging
 * staat in ARCHITECTURE §6.6 en geldt niet voor /ask; gebruik daar
 * {@link withStderrDigest}. */
export function withStderr(failure: AiFailure, stderr: StderrTail): AiFailure {
  const tail = stderr.tail();
  if (!tail) return failure;
  return { reason: failure.reason, detail: safeDetail(`${failure.detail} | stderr: ${tail}`) };
}

/** De stderr-samenvatting als tekst, of "" als het subprocess niets schreef.
 *
 * Eén rendering voor de twee afnemers (de faal-toelichting en de
 * warmpool-fallback-logregel), zodat de vorm niet uiteen kan lopen — en zodat
 * de regel "wat mag er uit stderr naar buiten" op ÉÉN plek staat in plaats van
 * per aanroeper opnieuw.
 *
 * Redacteert ZELF, en niet omdat de huidige aanroepers dat niet doen (beide
 * wel: `withStderrDigest` via `safeDetail`, `logEvent` via de poort). Maar dit
 * is een functie die per definitie ONGECONTROLEERDE subprocess-uitvoer
 * teruggeeft, en dan is "de aanroeper redacteert wel" precies het soort aanname
 * dat #292 duur maakte. Wie hier een derde afnemer aan hangt, krijgt de
 * redactie gratis mee. */
export function stderrDigestLine(stderr: StderrTail): string {
  const d = stderr.digest();
  if (d.bytes === 0) return "";
  const parts = [`stderr ${d.bytes}B`];
  if (d.machine.length > 0) parts.push(d.machine.join(" // "));
  if (d.withheld > 0) parts.push(`${d.withheld} regel(s) niet gemeld`);
  return safeDetail(parts.join(", "));
}

/** Plak de MACHINE-diagnostiek uit stderr achter een detail-tekst (#300).
 *
 * HET VERSCHIL MET {@link withStderr} IS DE INVOER, NIET HET KANAAL. Beide
 * lezen dezelfde ringbuffer van hetzelfde subprocess. Maar de motivering om het
 * prompt-residu te aanvaarden was nooit "stderr is ongevaarlijk" — ze was "op
 * dit endpoint is de invoer publieke Riot-tekst, dus de schade is nihil en het
 * diagnostisch nut groot" (ARCHITECTURE §6.6). Op /ask is de invoer de vraag
 * van een bezoeker, dus dezelfde afweging valt andersom uit: die vraag hoort in
 * `ask_trace` achter de admin-poort, niet in `docker logs rb-v2-ai`.
 *
 * Vandaar de gesloten vraag in plaats van de volledige staart: de regels die
 * aantoonbaar van de machine komen (spawn, OOM, auth, node-crash) gaan mee, de
 * rest wordt geteld. Dat dekt precies het scenario waarvoor #300 bestaat — een
 * omgevallen subprocess laat juist die regels achter — zonder een kanaal open
 * te zetten waar willekeurige tekst doorheen kan.
 *
 * Wat dit NIET is: een garantie. Een echode vraagregel die toevallig op een
 * machine-patroon matcht komt er nog steeds door. Het verschil is dat het
 * lekoppervlak van "de hele staart" naar "regels uit een gesloten lijst"
 * gaat — een bound, geen belofte. */
export function withStderrDigest(failure: AiFailure, stderr: StderrTail): AiFailure {
  const line = stderrDigestLine(stderr);
  if (!line) return failure;
  return { reason: failure.reason, detail: safeDetail(`${failure.detail} | ${line}`) };
}

/** Render één logwaarde tot tekst die {@link safeDetail} kan redacteren.
 *
 * WAAROM `JSON.stringify` EN NIET `String` (#295-review): `String({a:1})` geeft
 * `"[object Object]"`. Dat lekt niets — het VERNIETIGT de inhoud, wat iets
 * anders is dan hem redacteren, en het is de stille diagnostiekverlies-val van
 * #282: een toekomstige `logEvent("warmpool", { stats: pool.stats() })` zou
 * `[object Object]` loggen en de meting onzichtbaar weggooien. Erger nog, het
 * maakt een redactietest die een secret in een object stopt VACUÜM: `String`
 * gooit dat secret per constructie weg, dus de test slaagt ook als de redactie
 * kapot is. Serialiseren en dán redacteren behoudt de inhoud én laat de test
 * echt falen op de bug die hij bewaakt.
 *
 * Errors gaan door {@link renderThrown} (dus mét `cause`): `JSON.stringify` op
 * een Error geeft `{}` — name en message zijn niet-enumerable. */
function renderValue(value: unknown): string {
  if (typeof value === "string") return value;
  // Alleen niet-eindige getallen komen hier (de aanroeper vangt de rest af).
  // Via JSON.stringify zouden ze `null` worden — precies de samenval die we
  // wilden vermijden.
  if (typeof value === "number") return String(value);
  if (value instanceof Error) return renderThrown(value);
  try {
    // Kan gooien op circulaire structuren en op BigInt; een logregel mag nooit
    // de aanroeper omver trekken, dus valt hij terug op de kale vorm.
    return JSON.stringify(value) ?? String(value);
  } catch {
    return String(value);
  }
}

/** DE ENIGE PLEK IN rb-ai DIE NAAR STDOUT SCHRIJFT (#292).
 *
 * Waarom die regel zo absoluut is: #281 zette hier een redactie-poort neer,
 * maar twee oudere `console.log`'s in `ai.ts` liepen er gewoon omheen — de
 * agentic tool-argumenten (in de praktijk de VRAAGTEKST van de gebruiker) en
 * een rauwe `String(e)` uit het warmpool-faalpad. Een poort die je kunt
 * omzeilen is geen poort. Sinds #292 gaat élke logregel van de sidecar hier
 * doorheen, en {@link logCall} is er zelf ook gewoon een aanroeper van.
 *
 * Wat de poort garandeert:
 *  - elke waarde die geen eindig getal en geen boolean is, wordt eerst
 *    LEESBAAR gerenderd ({@link renderValue}) en gaat dan door
 *    {@link safeDetail};
 *  - `undefined`/`null` velden vallen weg (geen ruis, en `0` blijft staan);
 *  - de eventnaam is onoverschrijfbaar: een veld `evt` wordt genegeerd;
 *  - precies één parseerbare JSON-regel per aanroep.
 *
 * Wat de poort NIET garandeert (#292, expliciet): redactie is geen privacy.
 * `safeDetail` haalt SECRETS eruit, niet gebruikersinvoer — een vraagtekst
 * overleeft de poort ongeschonden. Inhoud die niet in de containerlog hoort,
 * geef je hier dus simpelweg niet mee; log de MAAT (bytes/aantallen). */
export function logEvent(evt: string, fields: Record<string, unknown> = {}): void {
  const line: Record<string, unknown> = { evt };
  for (const [key, value] of Object.entries(fields)) {
    if (value === undefined || value === null) continue;
    // `evt` mag niet overschreven worden: de eventnaam is waarop gegrepd en
    // geteld wordt, en een veld dat toevallig zo heet zou die stil kapen.
    if (key === "evt") continue;
    // NaN/Infinity vallen bewust NIET in de getallen-tak: `JSON.stringify`
    // maakt er `null` van, en dan is een kapotte meting niet te onderscheiden
    // van een ontbrekende (#282: een run die niet meldt wat er echt gebeurde,
    // liegt). Als tekst blijven ze zichtbaar.
    line[key] =
      (typeof value === "number" && Number.isFinite(value)) || typeof value === "boolean"
        ? value
        : safeDetail(renderValue(value));
  }
  console.log(JSON.stringify(line));
}

/** Eén regel per rb-ai-aanroep, als JSON zodat hij te grepp'en en te tellen is
 * (`docker logs rb-v2-ai | grep ai_call`).
 *
 * Wat er WEL in staat: endpoint, duur, uitkomst, uitvalsoort, payload-MATEN
 * (bytes/refs/items) en een korte ge-redacte toelichting.
 *
 * Secrets staan er NOOIT in: elke tekst gaat verplicht door {@link safeDetail},
 * en dat is getest tegen tien aanvalsvormen (JSON-embedded, over chunks
 * gesplitst, afgekapt door de ringbuffer, via een stack trace, met een
 * env-naam die het patroon niet matcht).
 *
 * Over prompt-inhoud is de eerlijke formulering ZWAKKER dan "nooit"
 * (#281-review). Wat rb-ai zélf samenstelt bevat geen promptmateriaal: de
 * toelichting komt uit foutmeldingen en SDK-metadata. Maar {@link StderrTail}
 * is een ONGECONTROLEERD kanaal — wat het Claude-subprocess naar stderr
 * schrijft belandt in `detail`, en draait de CLI ooit verbose, dan kan daar
 * kaarttekst tussen zitten. Dat is publieke Riot-tekst, dus de schade is nihil
 * en het diagnostisch nut groot; het is een bewust genomen residu, geen
 * garantie. Bouw er dus geen "hier staat gegarandeerd geen invoer"-aanname op. */
export function logCall(entry: {
  endpoint: string;
  ms: number;
  status: number;
  outcome: "ok" | "error";
  reason?: AiFailureReason;
  detail?: string;
  /** Grootte van de request-body in bytes — een MAAT, geen inhoud. Maakt de
   * vraag "vallen juist de grote payloads om?" direct toetsbaar in plaats van
   * achteraf reconstrueerbaar. */
  bytes?: number;
  /** Aantal aangeboden refs (omvang van het ontologie-vocabulaire). */
  refs?: number;
  /** Aantal geëmitteerde items bij een geslaagde extractie. */
  items?: number;
  /** Items die de deterministische narekening weigerde (#312): een term buiten
   * het aangeboden vocabulaire of een kapotte vorm. Een MAAT, geen inhoud —
   * hij meet hoe vaak het model buiten het lijstje kleurt nu het schema die
   * poort niet meer afdwingt. */
  rejected?: number;
  /** Condities die hun as-/lexicon-poort niet haalden terwijl het item bleef. */
  rejectedConditions?: number;
  /** Aantal aangeboden kaarten in een batch-aanroep (#323). */
  cards?: number;
  /** Aantal kaarten dat een geldige tool-call kreeg (partial salvage, #323). */
  cardsOk?: number;
  /** Tool-calls met een kaartcode buiten de aangeboden set — geweigerd en
   * geteld (kruisbesmettingspoort, #323). Een MAAT, geen inhoud. */
  unknownCode?: number;
  /** Taaktype van een /ask-call (cheap/hard/research/agentic). */
  task?: string;
}): void {
  logEvent("ai_call", {
    endpoint: entry.endpoint,
    ms: Math.round(entry.ms),
    status: entry.status,
    outcome: entry.outcome,
    task: entry.task,
    bytes: entry.bytes,
    refs: entry.refs,
    items: entry.items,
    rejected: entry.rejected,
    rejectedConditions: entry.rejectedConditions,
    cards: entry.cards,
    cardsOk: entry.cardsOk,
    unknownCode: entry.unknownCode,
    reason: entry.reason,
    // Lege toelichting weglaten in plaats van als "" loggen: `logEvent` filtert
    // alleen undefined/null, want daar is `0` een betekenisvolle waarde.
    detail: entry.detail || undefined,
  });
}
