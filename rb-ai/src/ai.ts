import {
  createSdkMcpServer,
  query,
  tool,
  type Options,
} from "@anthropic-ai/claude-agent-sdk";
import {
  BRAIN_SERVER_NAME,
  BRAIN_TOOLS,
  brainToolAllowlist,
  compactJson,
  createBrainSession,
} from "./brain-tools.js";
import { aiSemaphore, AI_QUEUE_WAIT_MS, ConcurrencyLimitError } from "./concurrency.js";
import {
  AiRunError,
  describeThrown,
  logEvent,
  resultFailure,
  RetryTracker,
  StderrTail,
  stderrDigestLine,
  withRetries,
  withStderr,
  withStderrDigest,
  type AiFailure,
} from "./failure.js";
import { RELATIONS_MARKER } from "./relations.js";
import { usageFromSdk, type AskUsage } from "./usage.js";
import {
  pushableInput,
  WarmPool,
  type WarmBootHandle,
  type WarmSignature,
} from "./warmpool.js";

// Auth: CLAUDE_CODE_OAUTH_TOKEN (abonnement) of ANTHROPIC_API_KEY.
// Laat ANTHROPIC_API_KEY leeg bij abonnementsgebruik — die wint stilletjes.
export type Task = "cheap" | "hard" | "research" | "agentic";

export interface AskImage {
  mediaType: string; // image/jpeg | image/png | image/webp | image/gif
  data: string; // base64
}

const MODEL: Record<Task, string> = {
  cheap: "claude-sonnet-4-6",
  hard: "claude-opus-4-8",
  research: "claude-sonnet-4-6", // web-werk is zoek+samenvat: Sonnet volstaat (kosten, #42)
  agentic: "claude-sonnet-4-6", // brein-loop is 2–8 beurten: Sonnet houdt kosten/latency binnen budget (BRAIN.md §2.4)
};

// Websearch is opt-in per taak (#64): alléén "research" krijgt WebSearch/
// WebFetch; alle andere taken draaien expliciet zonder built-in tools
// (`tools: []`), zodat web-toegang nooit stilzwijgend aan gaat
// (kosten/latency-bewaking, #42).
const RESEARCH_TOOLS = ["WebSearch", "WebFetch"];

// Research doet meerdere zoek/fetch-rondes; de andere taken blijven één
// prompt→antwoord-beurt.
const RESEARCH_MAX_TURNS = 16;

// Ruime maar harde grens: een research-call die hierna nog loopt wordt via
// AbortController afgebroken. De fout bubbelt naar server.ts → 500 → de
// aanroeper (RbAiClient) degradeert tot null. De sidecar crasht nooit.
const RESEARCH_TIMEOUT_MS = 300_000; // 5 minuten

// Resultaatcontract voor task="research" (#64; #63's bronnenjacht bouwt hierop):
// - Het antwoord eindigt ALTIJD met een sectie "Bronnen:" met per regel één
//   volledige URL (https://…) van een daadwerkelijk geraadpleegde pagina.
// - Beweringen zonder gevonden bron worden expliciet als onbevestigd benoemd.
// - Webresultaten zijn per definitie community-/meta-laag (docs/KNOWLEDGE.md):
//   de aanroeper mag ze nooit als officiële laag behandelen.
// Dit blok wordt server-side ACHTER de eventuele system-prompt van de
// aanroeper geplakt, zodat bronvermelding afgedwongen blijft ongeacht wat de
// aanroeper meestuurt.
const RESEARCH_CONTRACT = `Je hebt toegang tot WebSearch en WebFetch. Regels voor je antwoord:
- Baseer elke bewering op daadwerkelijk geraadpleegde webpagina's; verzin geen bronnen of URL's.
- Sluit je antwoord ALTIJD af met een sectie die begint met "Bronnen:" gevolgd door per regel één volledige URL (https://...) van een geraadpleegde pagina, waar mogelijk met publicatie- of raadpleegdatum erachter.
- Vind je voor (een deel van) de vraag geen bruikbare bron, zeg dat dan expliciet in plaats van te gokken; de sectie "Bronnen:" blijft ook dan verplicht (eventueel met alleen de wel-geraadpleegde URL's).
- Sommige sites weigeren datacenter-verkeer; meld het kort als een relevante pagina niet op te halen was.`;

// Agentic (#106, docs/BRAIN.md §2.4): de agent redeneert zelf over het brein
// via zes in-process MCP-tools die 1-op-1 op de brein-API mappen (§2.3,
// brain-tools.ts; HTTP naar rb-api via RB_API_URL). Alléén die tools staan in
// de allowlist — géén web, géén bash. Dubbele rem op kosten en latency:
// maxTurns + harde timeout hier, en een tool-call-cap in de sessie zelf.
const AGENTIC_MAX_TURNS = 8; // §2.4: "maxTurns ~8"
const AGENTIC_TIMEOUT_MS = 120_000; // §2.4: harde grens 90–120s; zelfde AbortController-patroon als research
const AGENTIC_MAX_TOOL_CALLS = 12; // §2.4: "cap ~12 calls"
const AGENTIC_HTTP_TIMEOUT_MS = 10_000; // per brein-call: een hangende rb-api mag de harde timeout niet in z'n eentje opsouperen

// Gedragscontract voor task="agentic". Net als RESEARCH_CONTRACT wordt dit
// blok server-side ACHTER de system-prompt van de aanroeper geplakt, zodat de
// voorrangregels van de kennispiramide (docs/KNOWLEDGE.md, BRAIN.md §2.4)
// gelden ongeacht wat de aanroeper meestuurt.
const AGENT_ADDENDUM = `Je hebt zes brein-tools (semantic_search, get_node, neighbors, path, evidence, contradictions) die de Riftbound-kennisbank bevragen. Regels:
- De kennispiramide blijft gelden: officieel > geverifieerde rulings > primer > community > meta. Weeg elk toolresultaat op zijn laag- en trust-label; een community-claim wint nooit van een officiële regel of geverifieerde ruling.
- Weerlegde of vervangen claims (via contradictions) zijn geen kennis — noem ze hooguit expliciet als weerlegd.
- Een toolresultaat "brein niet beschikbaar" of "niet gevonden" is informatie, geen fout: redeneer verder met wat je al hebt en verzin nooit toolresultaten, refs of citaten.
- Wees zuinig met tool-calls (er is een harde limiet): stop met zoeken zodra je genoeg weet en geef dan je eindantwoord in het gevraagde format, met §-verwijzingen waar je die gevonden hebt.
- Ontdek je tijdens het redeneren een verband tussen twee brein-knopen dat het brein nog niet als relatie kent, meld dat dan NA je volledige eindantwoord: één regel met exact "${RELATIONS_MARKER}" gevolgd door JSON in de vorm {"relations": [{"from": "<ref>", "to": "<ref>", "kind": "...", "explanation": "..."}]}. Alleen refs die letterlijk in je toolresultaten voorkwamen — verzin er geen. kind is kort, herbruikbaar en in kleine letters (bv. counters, enables, versterkt, wordt beperkt door, vereist, verduidelijkt); explanation is 1-2 zinnen in het Engels (#187: afgeleide kennis in de brontaal, dicht bij de officiële bewoording) die het verband onderbouwen vanuit je toolresultaten. Maximaal 5 sterke voorstellen; geen open deuren die de graph al kent. Niets ontdekt? Laat marker en blok dan helemaal weg.`;

/** Meld één brein-tool-aanroep langs de twee kanalen die er zijn (#292).
 *
 * DE SPLITSING IS DE HELE POINTE. Tot #292 ging dezelfde regel — toolnaam PLUS
 * argumenten — naar allebei, en bij `semantic_search` zijn die argumenten in de
 * praktijk de VRAAGTEKST VAN DE GEBRUIKER. Die belandde onbewerkt in
 * `docker logs rb-v2-ai`, een kanaal dat veel losser wordt behandeld dan de
 * plek waar de vraag bewust wél staat (`ask_trace`, achter de admin-poort).
 * Door `safeDetail` halen lost dat NIET op: die haalt secrets weg, geen
 * gebruikersinvoer. De enige echte oplossing is de inhoud niet meegeven.
 *
 *  - `onStep` (→ server.ts `steps` → `AskTrace.BrainSteps`): de VOLLEDIGE stap,
 *    ongewijzigd. Daar hoort hij thuis en daar verandert niets aan; het
 *    verificatiepad van #106/#107 blijft intact.
 *  - stdout: alleen de toolnaam (gesloten verzameling, {@link BRAIN_TOOLS}) en
 *    de MAAT van de argumenten. Genoeg om te zien dát en hoe vaak de agent het
 *    brein bevroeg — de vraag "welke tools riep hij aan, in welke volgorde" is
 *    beantwoordbaar zonder één teken invoer te loggen.
 *
 * Geëxporteerd omdat dit de plek is waar de privacy-beslissing valt: zo is ze
 * op GEDRAG te toetsen (schrijft de logregel echt geen argument-inhoud?) in
 * plaats van met een grep op de broncode. */
export function noteBrainStep(
  toolName: string,
  args: Record<string, unknown>,
  onStep?: (step: string) => void,
): string {
  const rendered = compactJson(args);
  const step = `${toolName} ${rendered.slice(0, 200)}`;
  onStep?.(step);
  logEvent("brain_step", { tool: toolName, bytes: Buffer.byteLength(rendered, "utf8") });
  return step;
}

/** In-process MCP-server met de zes brein-tools (§2.4, createSdkMcpServer).
 * Per aanroep een verse sessie zodat de tool-call-cap per vraag telt. Stappen
 * gaan via {@link noteBrainStep} naar de aanroeper (`steps` →
 * `AskTrace.BrainSteps`, deelissue 4 #107) en inhoudsloos naar de containerlog.
 * Exported zodat de MCP-laag ook zonder LLM-call te smoken/testen is. */
export function createBrainMcpServer(onStep?: (step: string) => void) {
  const session = createBrainSession({
    baseUrl: process.env.RB_API_URL,
    timeoutMs: AGENTIC_HTTP_TIMEOUT_MS,
    maxCalls: AGENTIC_MAX_TOOL_CALLS,
  });
  return createSdkMcpServer({
    name: BRAIN_SERVER_NAME,
    version: "1.0.0",
    tools: BRAIN_TOOLS.map((t) =>
      tool(t.name, t.description, t.schema, async (args) => {
        const a = args as Record<string, unknown>;
        noteBrainStep(t.name, a, onStep);
        // session.run gooit nooit: fouten (rb-api plat, timeout, cap) komen
        // als leesbaar toolresultaat terug — fouten zijn data (§2.3).
        return { content: [{ type: "text" as const, text: await session.run(t.name, a) }] };
      }),
    ),
  });
}

// Brein-extractie (#226, §3.1): tool-forced structured output. Eén beurt om de
// tool te roepen, één om af te ronden — ruim gehouden op 3. Harde timeout net als
// research/agentic zodat een hangende run nooit op abonnementskosten blijft draaien.
const EXTRACT_MAX_TURNS = 3;

/** Harde grens per extractie-run. Verstelbaar via `AI_EXTRACT_TIMEOUT_MS`, met
 * de bestaande 90 s als default — dus zonder env-wijziging verandert er niets.
 *
 * EERLIJKE MOTIVERING (#281): ophogen is een PLEISTER, geen oplossing. Een
 * productie-experiment liet zien dat de duur van een extractie meeschaalt met
 * het AANTAL AANGEBODEN REFS — 3 refs → 200 na 49,0 s, 39 refs → 500 na 92,1 s.
 * Het vocabulaire dat we per kaart meesturen groeit met elke set die de
 * kennisbank leert, dus dit is een SCHAALKLIP: hoe meer het brein weet, hoe
 * groter de kans dat een extractie omvalt. De timeout verhogen verschuift die
 * klip alleen — bij 60 refs ligt hij er weer. De echte oplossing (minder refs
 * per aanroep aanbieden, of op mechanic-niveau vragen in plaats van per kaart)
 * staat in #288 en hoort NIET in de meet-PR van #281.
 *
 * De knop staat er dus als ops-noodrem, niet als fix. Let op: hem daadwerkelijk
 * gebruiken vraagt óók een regel in de compose-`environment:` van `rb-v2-ai`
 * (#268-valkuil: een variabele in de VM-`.env` is een substitutiebron voor de
 * compose-file, géén container-env). */
const EXTRACT_TIMEOUT_MS = (() => {
  const parsed = Number.parseInt(process.env.AI_EXTRACT_TIMEOUT_MS ?? "", 10);
  return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 90_000;
})();

/** Extra tijdsbudget per EXTRA kaart in een batch-sessie (#323). De issue-rand
 * is hard: "timeout schaalt mee met K, anders meet je alleen je eigen plafond"
 * (les #311). Default 180 s: ruim boven de gemeten sonnet-generatie per kaart
 * (147-163 s), zodat de schaal niet zelf de klip wordt. Net als
 * AI_EXTRACT_TIMEOUT_MS hoort de knop óók in de compose-`environment:` van
 * `rb-v2-ai` (#268-valkuil), en spiegelt rb-api dezelfde waarde voor zijn
 * HttpClient-budget — een rb-api-timeout die korter is dan deze keten verkleedt
 * elke batchfout als "traag" (#281). */
const EXTRACT_PER_CARD_MS = (() => {
  const parsed = Number.parseInt(process.env.AI_EXTRACT_PER_CARD_MS ?? "", 10);
  return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 180_000;
})();

/** De effectieve tijdslimiet van een sessie met `k` kaarten: basis + (k−1) ×
 * per-kaart-budget. PURE functie, apart geëxporteerd zodat de schaling met
 * LETTERLIJKE waarden getest kan worden (#286-les) — haal de schaling weg en de
 * literal-test gaat rood. */
export function scaledExtractTimeoutMs(k: number, baseMs: number, perCardMs: number): number {
  return baseMs + Math.max(0, k - 1) * perCardMs;
}

/** De SDK-aanroep als injecteerbare functie (test-seam, zie
 * {@link extractWithTool}). Structureel getypeerd op wat wij ervan gebruiken:
 * prompt + options erin, een berichtenstroom eruit. */
export type QueryRunner = (arg: {
  prompt: string;
  options: Options;
}) => AsyncIterable<unknown>;

/** Dezelfde naad voor het /ask-pad (#300), met de bredere prompt-vorm die
 * {@link askClaude} gebruikt: een kale string, of een streaming-input-iterator
 * zodra er afbeeldingen meegaan.
 *
 * Waarom askClaude er nu ook een krijgt: zonder naad valt over het /ask-faalpad
 * alleen te toetsen DAT de juiste tekens in de broncode staan, en zo'n bron-grep
 * vangt zijn eigen bug niet — dat is de les die #281 hier al duur betaalde. Met
 * de naad kan een test een subprocess simuleren dat naar stderr schrijft en dan
 * omvalt, en toetsen wat er daadwerkelijk in de uitval-toelichting terechtkomt. */
export type AskQueryRunner = (arg: {
  prompt: string | AsyncIterable<unknown>;
  options: Options;
}) => AsyncIterable<unknown>;

/** Uitkomst van één {@link extractWithTool}-run (#281). `items` houdt het
 * bestaande contract: de gevangen array (mogelijk leeg) of `null` bij uitval.
 * `failure` is nieuw en staat er ALLEEN bij `items === null`: het zegt WAAROM
 * er niets kwam, zodat server.ts het kan loggen én in de foutbody kan
 * meesturen. Vóór #281 was dit onderscheid nergens: elke mislukking — timeout,
 * max beurten, API-fout, een model dat de tool gewoon niet riep — kwam als
 * dezelfde kale `null` naar buiten en daarna als hetzelfde kale
 * `500 {"error":"extractie mislukt"}`. */
export interface ExtractOutcome {
  items: unknown[] | null;
  failure?: AiFailure;
  /** Is de run door ONZE harde timeout afgekapt (#281)? Apart van
   * `failure.reason`, want `withRetries` mag die reden overschrijven met de
   * upstream-oorzaak (een timeout ná zeven mislukte API-pogingen is een
   * API-fout). Op HTTP-niveau blijft het feit "wij hebben afgekapt" staan, en
   * dáár hangt de 504 aan — zodat rb-api een timeout als `timeout` telt en niet
   * als generieke 5xx. Precies de samenval die #281 blind maakte: drie
   * verschillende oorzaken (tool niet geroepen, timeout, echte fout) kwamen
   * allemaal als dezelfde kale 500 naar buiten. */
  timedOut?: boolean;
}

/** De afloop van één extractie-run, als PURE functie (#281-review).
 *
 * Waarom dit geen inline-branches meer zijn: de eerste versie besliste dit op
 * twee plaatsen — in het catch-blok én na de leeslus — en die twee liepen
 * uiteen. Alleen het catch-pad woog `timedOut`, dus een afgebroken run die
 * NIET gooide (de iterator loopt leeg, of geeft nog een fout-result af) kwam er
 * als `no_tool_call` + 500 uit. Dat is niet alleen fout maar actief misleidend:
 * het stuurt de beheerder naar de prompt terwijl het de tijdslimiet was. En het
 * sprak de kernbevinding van deze PR tegen — de Agent SDK gooit juist NIET
 * noodzakelijk bij een mislukte run.
 *
 * Eén functie voor beide paden maakt die drift per constructie onmogelijk, en
 * maakt de beslissing bovendien uitputtend testbaar zonder SDK.
 *
 * Volgorde is betekenisvol: een geldige vangst wint van elke faalreden (weggooien
 * zou goed werk vernietigen), en de afkapping wint van wat de run verder ook
 * meldde (wij hakten af — dat is wat er gebeurde). */
export function decideExtractOutcome(input: {
  captured: unknown[] | null;
  timedOut: boolean;
  aborted: boolean;
  runFailure?: AiFailure;
  toolName: string;
  timeoutMs: number;
}): ExtractOutcome {
  const { captured, timedOut, aborted, runFailure, toolName, timeoutMs } = input;
  const seconds = timeoutMs / 1000;

  if (captured !== null) {
    // De tool vuurde: de kandidaten zijn geldig en gaan mee. Werd de run dáárna
    // afgekapt, dan is het een PARTIËLE vangst — de meting moet dat zien, anders
    // vallen juist de traagste kaarten uit de schaalklip-meting weg.
    return timedOut
      ? {
          items: captured,
          timedOut: true,
          failure: {
            reason: "timeout",
            detail:
              `tool vuurde nog vóór de tijdslimiet van ${seconds}s; ` +
              "resultaat behouden, run daarna afgekapt",
          },
        }
      : { items: captured };
  }
  if (timedOut) {
    return {
      items: null,
      timedOut: true,
      failure: {
        reason: "timeout",
        detail: `extractie afgebroken na ${seconds}s (harde timeout)`,
      },
    };
  }
  if (aborted) {
    return {
      items: null,
      failure: { reason: "aborted", detail: "client heeft de verbinding gesloten" },
    };
  }
  // Geen afkapping: de run liep echt af. Meldde de SDK een eigen fout, dan is
  // dát de oorzaak; anders koos het model ervoor de tool niet te roepen — een
  // prompt-/schemaprobleem, een andere knop dan een machineprobleem.
  return {
    items: null,
    failure: runFailure ?? {
      reason: "no_tool_call",
      detail: `run afgerond zonder aanroep van ${toolName}`,
    },
  };
}

/** Tool-forced brein-extractie (#226, docs/ARCHITECTURE brein-epic §3.1). Draait
 * één geforceerde in-process MCP-tool (createSdkMcpServer/tool, zelfde mechaniek als
 * de agentic brein-tools). Sinds #312 is de tool-VORM een constante (extract.ts):
 * het vocabulaire reist als prompt-invoer mee en de gesloten-vraag-regel wordt in
 * server.ts deterministisch nagerekend (`enforce*Vocabulary`) — het schema is dus
 * niet langer de poort. De tool-handler VANGT de argumenten in een closure en geeft
 * een ack terug; de daadwerkelijke kandidaten reizen dus niet via de antwoordtekst
 * maar via de tool-input. Puur SDK-gedreven en dus, net als askClaude, niet los
 * unit-getest; de vocabulaire-poort in extract.ts en de faalvertaling in failure.ts
 * zijn dat wél.
 *
 * BEWUST GEEN warme-pool-claim op dit pad (#312, gemeten). De boot-kost die een
 * warme sessie voorbetaalt (subprocess-spawn + MCP-registratie, meetbaar als
 * query()→system/init en auth-onafhankelijk) is 0,41-0,46 s lokaal (mediaan
 * 0,43 s over 10 runs, M-serie); geëxtrapoleerd naar de VM-CPU is dat orde
 * 1-2,5 s tegen een gemiddelde kaart-generatie van 147-163 s (validatieruns;
 * het plafond staat via de VM-env inmiddels op 240 s) — terwijl één extra idle
 * warme sessie 300-400 MiB RSS vasthoudt op de VM waar de OOM-killer al
 * llama-server schoot (#282/#293). De generieke tool-vorm is de eigenlijke winst: het request-prefix
 * (tools + system) is nu byte-stabiel over een mining-run, dus prompt-cache-baar,
 * én de blokkade voor een latere warme aansluiting is weg. Sluit die pas aan
 * wanneer een PRODUCTIE-meting van de boot-kost (≥ enkele seconden) het
 * geheugenoffer rechtvaardigt; de guard-test in ai.test.ts ("extract-pad raakt de
 * warme pool niet aan") is dan de plek die bewust rood hoort te gaan, en een
 * claim mag er alléén komen voor task="cheap" — het audit-pad (task "hard",
 * #255) zou anders stil op een warme cheap-sessie (MODEL.cheap) draaien met
 * valse provenance.
 *
 * Wat de tool vóór een uitval al ving blijft geldig (dan is er gewoon een
 * resultaat); anders komt er `null` mét een {@link AiFailure} terug. */
export async function extractWithTool(opts: {
  toolName: string;
  description: string;
  schema: Parameters<typeof tool>[2];
  resultKey: string;
  system?: string;
  addendum: string;
  text: string;
  signal?: AbortSignal;
  /** Modelkeuze via de bestaande taak-typering (#255): default "cheap" — het
   * bestaande gedrag van de bulk-extractie. De steekproef-audit zet "hard"
   * (MODEL.hard): een sterker model dat de cheap-output beoordeelt is de hele
   * pointe van de audit, en de bestaande Task→model-mapping is er al — géén
   * nieuw model-config-mechanisme. */
  task?: Task;
  /** Opgelost model-ID uit de gesloten aliasmap (#323, extract.ts
   * `parseExtractModelAlias`) — wint van `MODEL[task]`. Nooit een rauwe
   * gebruikersstring: de 400-poort zit in de request-parse. */
  model?: string;
  /** Test-seam (#281-review): de SDK-aanroep zelf. Productie laat dit weg en
   * krijgt `query`; een test levert een eigen berichtenstroom en kan zo de
   * faal- en timeout-paden ECHT doorlopen. Zonder deze naad viel er over dit
   * pad alleen te toetsen dát de juiste tekens in de broncode staan — en zo'n
   * bron-grep vangt zijn eigen bug niet: het weghalen van één `timedOut = true`
   * liet alle tests groen terwijl elke timeout weer als generieke 500 naar
   * buiten kwam. Zelfde patroon als `RbAiClient.RetryDelay` in rb-api. */
  runQuery?: QueryRunner;
}): Promise<ExtractOutcome> {
  const {
    toolName, description, schema, resultKey, system, addendum, text, signal,
    task = "cheap", model,
    runQuery = query as unknown as QueryRunner,
  } = opts;
  const serverName = "extract";

  let captured: unknown[] | null = null;
  const extractServer = createSdkMcpServer({
    name: serverName,
    version: "1.0.0",
    tools: [
      tool(toolName, description, schema, async (args) => {
        const value = (args as Record<string, unknown>)[resultKey];
        // Alleen een echte array telt; alles anders is een lege vangst (de .NET-
        // parser is de tweede muur, maar we leveren nooit rommel op).
        captured = Array.isArray(value) ? value : [];
        return { content: [{ type: "text" as const, text: "ok" }] };
      }),
    ],
  });

  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const systemPrompt = [system, addendum].filter(Boolean).join("\n\n");
  // Stderr van het subprocess meelezen (#281): stil bij succes, doorslaggevend
  // bij uitval — zie StderrTail.
  const stderr = new StderrTail();
  // SDK-interne retries meetellen (#281): een aanhoudende 429/529 kost via de
  // exponentiële backoff meer dan de hele 90s-begroting en verscheen daardoor
  // als onze timeout in plaats van als de API-fout die het was.
  const retries = new RetryTracker();
  /** Elke uitval krijgt dezelfde context mee: wat het subprocess naar stderr
   * schreef en hoe vaak de SDK intern opnieuw probeerde. Eén poort, zodat geen
   * enkel faalpad half-geïnstrumenteerd kan achterblijven. */
  const enrich = (failure: AiFailure): AiFailure =>
    withRetries(withStderr(failure, stderr), retries);
  /** Beide uitgangen — na de leeslus én uit het catch-blok — lopen hier
   * doorheen, zodat ze per constructie dezelfde beslissing nemen. De
   * stderr-staart en de SDK-retries worden er hier overheen gelegd. */
  const finish = (): ExtractOutcome => {
    const outcome = decideExtractOutcome({
      captured,
      timedOut,
      aborted: controller.signal.aborted,
      runFailure,
      toolName,
      timeoutMs: EXTRACT_TIMEOUT_MS,
    });
    return outcome.failure ? { ...outcome, failure: enrich(outcome.failure) } : outcome;
  };
  let release: (() => void) | undefined;
  let runFailure: AiFailure | undefined;
  try {
    // Background (#279): de extractie-endpoints zijn batch-werk voor de
    // brein-mining — er zit geen bezoeker op te wachten. Ze mogen daarom
    // hoogstens de achtergrond-deelcap bezetten en worden in de rij altijd
    // ingehaald door /ask. Een mining-kaart die hierdoor een 429 krijgt komt
    // de volgende run gewoon terug (per-kaart-watermark); een weggestuurde
    // bezoeker niet.
    release = await aiSemaphore.acquire(1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
      priority: "background",
    });
    // Timer PAS na de permit (#281): de 90 s is een begroting voor de
    // LLM-run, niet voor de wachtrij. Startte hij bij binnenkomst, dan at een
    // volle achtergrond-deelcap tot 30 s (AI_QUEUE_WAIT_MS) van het budget op
    // en verscheen het resultaat als een timeout die op de LLM leek te wijzen
    // terwijl de call gewoon te kort de tijd kreeg. Sinds de mining parallel
    // draait (#279) is die wachttijd de regel, niet de uitzondering.
    timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, EXTRACT_TIMEOUT_MS);
    const options: Options = {
      model: model ?? MODEL[task],
      maxTurns: EXTRACT_MAX_TURNS,
      tools: [],
      mcpServers: { [serverName]: extractServer },
      allowedTools: [`mcp__${serverName}__${toolName}`],
      permissionMode: "dontAsk" as const,
      abortController: controller,
      systemPrompt,
      stderr: (data: string) => stderr.append(data),
    };
    // De berichten leeglezen zodat de tool-call daadwerkelijk vuurt; het
    // tekstantwoord interesseert ons niet — captured draagt het resultaat.
    // Het afsluitende result-bericht lezen we WEL (#281): daar — en nergens
    // anders — meldt de SDK dat de run mislukte.
    for await (const message of runQuery({ prompt: text, options })) {
      retries.observe(message);
      runFailure = resultFailure(message) ?? runFailure;
    }
    // De leeslus is afgelopen — één beslispunt, gedeeld met het catch-blok.
    return finish();
  } catch (e) {
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout/uitval is verwacht pad: null → rb-api degradeert. Wat de tool vóór
    // de uitval al ving blijft geldig; anders null MET de reden.
    // Een geworpen fout is alleen de oorzaak als er geen afkapping was; anders
    // wint de afkapping (dezelfde beslissing als hierboven).
    if (!timedOut && !controller.signal.aborted && captured === null)
      return { items: null, failure: enrich(runFailure ?? describeThrown(e)) };
    return finish();
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    release?.();
  }
}

// ── Batch-extractie: K kaarten per sessie (#323) ─────────────────────────────

/** Uitkomst van één {@link extractBatchWithTool}-run. Anders dan het losse pad
 * is de vangst hier PER SLEUTEL (kaartcode): valt de sessie om nadat m van de K
 * kaarten een geldige tool-call hadden, dan blijven die m staan (partial
 * salvage) en zegt `failure` waarom de rest ontbreekt. `perKey` is leeg én
 * `failure` gezet ⇒ de hele sessie leverde niets — dat pad krijgt dezelfde
 * 504/500-vertaling als het losse endpoint. */
export interface BatchExtractOutcome {
  /** Gevangen tool-input per aangeboden sleutel; een sleutel ontbreekt als het
   * model er nooit een geldige call voor deed. */
  perKey: Map<string, unknown[]>;
  /** Tool-calls met een sleutel BUITEN de aangeboden set — geweigerd en geteld
   * (kruisbesmettingspoort, #323). Een MAAT, geen inhoud. */
  unknownKeys: number;
  /** Echte token-usage van de sessie uit het SDK-result-bericht (#121-vorm),
   * of null wanneer de run er geen afgaf (bv. afgekapt vóór het result). De
   * batch amortiseert de sessiekost over K kaarten — zonder deze meting is
   * "wat kost een fable-batch van 50?" niet te beantwoorden. */
  usage: AskUsage | null;
  failure?: AiFailure;
  timedOut?: boolean;
}

/** De vangststand van één batch-sessie — gedeeld tussen de tool-handler en de
 * afloop-beslissing. */
export interface BatchCaptureState {
  perKey: Map<string, unknown[]>;
  unknownKeys: number;
}

/** Uitslag van één batch-tool-call: de ack-tekst voor het model, en — alléén
 * bij een geaccepteerde vangst — de sleutel. Die laatste voedt de heartbeat
 * per kaart (#323): zonder levensteken lijkt een sessie van uren bevroren en
 * nodigt dat uit tot een handmatige cancel. */
export interface BatchCaptureResult {
  ack: string;
  accepted?: string;
}

/** Verwerk één batch-tool-call, als PURE functie (#323). Dit is de
 * kruisbesmettingspoort: een call met een sleutel buiten de aangeboden set
 * wordt NIET opgeslagen — geweigerd en geteld (unknown_keys) — en de fouttekst
 * gaat als ack terug de sessie in zodat het model zichzelf kan corrigeren.
 * Binnen de set wint de laatste geldige call per sleutel (zelfcorrectie), en
 * alles anders dan een array is een lege vangst — exact de regel van het losse
 * pad. Apart van de SDK-handler zodat de poort op GEDRAG te toetsen is; de
 * handler in {@link extractBatchWithTool} is er alleen de bedrading van. */
export function captureBatchToolCall(
  state: BatchCaptureState,
  keySet: ReadonlySet<string>,
  keyField: string,
  resultKey: string,
  args: Record<string, unknown>,
): BatchCaptureResult {
  const key = typeof args[keyField] === "string" ? (args[keyField] as string).trim() : "";
  if (!keySet.has(key)) {
    state.unknownKeys += 1;
    return { ack: "geweigerd: onbekende kaartcode — gebruik exact één van de aangeboden codes" };
  }
  const value = args[resultKey];
  state.perKey.set(key, Array.isArray(value) ? value : []);
  return { ack: `ok (${state.perKey.size}/${keySet.size})`, accepted: key };
}

/** De afloop van één batch-run, als PURE functie — zelfde rol en zelfde
 * volgorde-regels als {@link decideExtractOutcome}: wat gevangen is blijft
 * (goed werk weggooien mag nooit), de afkapping wint van wat de run verder
 * meldde, en een nette afloop zonder calls voor de resterende sleutels is een
 * prompt-probleem (`no_tool_call`), geen machineprobleem. */
export function decideBatchExtractOutcome(input: {
  perKey: Map<string, unknown[]>;
  expected: number;
  unknownKeys: number;
  timedOut: boolean;
  aborted: boolean;
  runFailure?: AiFailure;
  toolName: string;
  timeoutMs: number;
  usage?: AskUsage | null;
}): BatchExtractOutcome {
  const { perKey, expected, unknownKeys, timedOut, aborted, runFailure, toolName, timeoutMs } =
    input;
  const usage = input.usage ?? null;
  const seconds = timeoutMs / 1000;
  const missing = expected - perKey.size;

  if (timedOut) {
    return {
      perKey, unknownKeys, usage, timedOut: true,
      failure: {
        reason: "timeout",
        detail:
          perKey.size > 0
            ? `sessie afgekapt na ${seconds}s; ${perKey.size}/${expected} kaarten al gevangen en behouden`
            : `batch-extractie afgebroken na ${seconds}s (harde timeout)`,
      },
    };
  }
  if (aborted && missing > 0) {
    return {
      perKey, unknownKeys, usage,
      failure: { reason: "aborted", detail: "client heeft de verbinding gesloten" },
    };
  }
  if (runFailure && missing > 0) return { perKey, unknownKeys, usage, failure: runFailure };
  if (missing > 0) {
    return {
      perKey, unknownKeys, usage,
      failure: {
        reason: "no_tool_call",
        detail: `run afgerond met ${perKey.size}/${expected} aanroepen van ${toolName}`,
      },
    };
  }
  // Alles gevangen: een eventueel gemelde run-fout kost geen kaarten meer en
  // reist alleen als context mee in de logregel.
  return { perKey, unknownKeys, usage, ...(runFailure ? { failure: runFailure } : {}) };
}

/** Tool-forced batch-extractie (#323): één SDK-sessie behandelt K kaarten, elk
 * met een eigen tool-call die de KAARTCODE draagt. Zelfde discipline als
 * {@link extractWithTool} — achtergrond-permit, harde timeout (geschaald met K,
 * {@link scaledExtractTimeoutMs}), result-bericht + api_retry gelezen, stderr
 * meegelezen — plus de twee batch-specifieke poorten: een call met een code
 * buiten `keys` wordt geweigerd en geteld (unknown_keys), en de vangst wordt
 * incrementeel per kaart bewaard zodat een omvallende sessie de al-gevangen
 * kaarten niet meesleept (partial salvage; rb-api watermarkt alléén die). */
export async function extractBatchWithTool(opts: {
  toolName: string;
  description: string;
  schema: Parameters<typeof tool>[2];
  /** Veld in de tool-input dat de sleutel (kaartcode) draagt. */
  keyField: string;
  /** Veld in de tool-input dat de items-array draagt. */
  resultKey: string;
  /** De aangeboden sleutels — de gesloten set waartegen elke call getoetst wordt. */
  keys: string[];
  system?: string;
  addendum: string;
  text: string;
  signal?: AbortSignal;
  task?: Task;
  /** Zie {@link extractWithTool}: opgelost model-ID, wint van MODEL[task]. */
  model?: string;
  /** Heartbeat per geaccepteerde kaart (#323): vuurt bij elke geldige
   * tool-call met (code, gevangen, totaal). server.ts stuurt er een
   * NDJSON-frame op uit zodat rb-api de job-voortgang per kaart kan tonen —
   * zonder levensteken lijkt een sessie van uren bevroren. */
  onCapture?: (key: string, done: number, total: number) => void;
  runQuery?: QueryRunner;
}): Promise<BatchExtractOutcome> {
  const {
    toolName, description, schema, keyField, resultKey, keys, system, addendum, text,
    signal, task = "cheap", model, onCapture,
    runQuery = query as unknown as QueryRunner,
  } = opts;
  const serverName = "extract";
  const keySet = new Set(keys);
  const timeoutMs = scaledExtractTimeoutMs(keys.length, EXTRACT_TIMEOUT_MS, EXTRACT_PER_CARD_MS);

  const state: BatchCaptureState = { perKey: new Map(), unknownKeys: 0 };
  const extractServer = createSdkMcpServer({
    name: serverName,
    version: "1.0.0",
    tools: [
      // Dunne bedrading om de pure poort {@link captureBatchToolCall} — de
      // kruisbesmettings- en vangstregels staan dáár, gedragsgetest.
      tool(toolName, description, schema, async (args) => {
        const r = captureBatchToolCall(
          state, keySet, keyField, resultKey, args as Record<string, unknown>);
        if (r.accepted) onCapture?.(r.accepted, state.perKey.size, keys.length);
        return { content: [{ type: "text" as const, text: r.ack }] };
      }),
    ],
  });

  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const systemPrompt = [system, addendum].filter(Boolean).join("\n\n");
  const stderr = new StderrTail();
  const retries = new RetryTracker();
  const enrich = (failure: AiFailure): AiFailure =>
    withRetries(withStderr(failure, stderr), retries);
  const finish = (): BatchExtractOutcome => {
    const outcome = decideBatchExtractOutcome({
      perKey: state.perKey,
      expected: keys.length,
      unknownKeys: state.unknownKeys,
      timedOut,
      aborted: controller.signal.aborted,
      runFailure,
      toolName,
      timeoutMs,
      usage,
    });
    return outcome.failure ? { ...outcome, failure: enrich(outcome.failure) } : outcome;
  };
  let release: (() => void) | undefined;
  let runFailure: AiFailure | undefined;
  let usage: AskUsage | null = null;
  try {
    // Eén batch-call = één achtergrond-permit (#279): de semaphore blijft
    // ongewijzigd en de interactieve reserve blijft vrij — dat K kaarten in
    // één sessie reizen is juist wat de doorvoer per permit verhoogt.
    release = await aiSemaphore.acquire(1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
      priority: "background",
    });
    timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, timeoutMs);
    const options: Options = {
      model: model ?? MODEL[task],
      // Eén tool-beurt per kaart plus de bestaande marge: bij K=1 exact
      // EXTRACT_MAX_TURNS, elke extra kaart één beurt erbij. Een assistant-
      // beurt kan meerdere tool-calls dragen, dus dit is een bovengrens.
      maxTurns: EXTRACT_MAX_TURNS + (keys.length - 1),
      tools: [],
      mcpServers: { [serverName]: extractServer },
      allowedTools: [`mcp__${serverName}__${toolName}`],
      permissionMode: "dontAsk" as const,
      abortController: controller,
      systemPrompt,
      stderr: (data: string) => stderr.append(data),
    };
    for await (const message of runQuery({ prompt: text, options })) {
      retries.observe(message);
      runFailure = resultFailure(message) ?? runFailure;
      // Usage uit het result-bericht (#121-vorm): de echte token-kosten van de
      // sessie — het antwoord op "wat kost een batch van K?".
      const m = message as { type?: string; usage?: unknown };
      if (m.type === "result") usage = usageFromSdk(m.usage) ?? usage;
    }
    return finish();
  } catch (e) {
    if (e instanceof ConcurrencyLimitError) throw e;
    // Zelfde beslisregels als het losse pad: een geworpen fout is alleen de
    // oorzaak als er geen afkapping was; wat al gevangen is blijft staan.
    if (!timedOut && !controller.signal.aborted) runFailure = runFailure ?? describeThrown(e);
    return finish();
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    release?.();
  }
}

/** Bericht-vorm voor streaming input. Gedeeld door het koude beeld-pad
 * (userMessage hieronder) en de warme pool (het bericht dat bij de claim in
 * de vastgehouden input-iterator wordt gepusht) — de SDK schrijft voor een
 * kale string-prompt exact dezelfde content-vorm naar het subprocess, dus
 * warm en koud doen dezelfde API-call. */
export function buildUserMessage(prompt: string, images: AskImage[]) {
  return {
    type: "user" as const,
    message: {
      role: "user" as const,
      content: [
        ...images.map((img) => ({
          type: "image" as const,
          source: {
            type: "base64" as const,
            media_type: img.mediaType,
            data: img.data,
          },
        })),
        { type: "text" as const, text: prompt },
      ],
    },
    parent_tool_use_id: null,
    session_id: "rb-ai",
  };
}

/** Streaming-input met content-blocks — nodig zodra er afbeeldingen meegaan. */
async function* userMessage(prompt: string, images: AskImage[]) {
  yield buildUserMessage(prompt, images);
}

/** Antwoord + echte token-usage van één askClaude-run (#121). `usage` komt
 * uit het afsluitende result-bericht van de SDK (opgeteld over alle beurten,
 * incl. tool-overhead bij research/agentic) en is null wanneer de SDK er
 * geen meegaf — de aanroeper behandelt dat als "onbekend", nooit als 0.
 *
 * `failure` (#281) draagt de uitvalsoort die datzelfde result-bericht meldde
 * (`error_max_turns`, een `api_error_status`, een geweigerde tool). De Agent
 * SDK GOOIT daar niet bij — het is een gewoon bericht — dus zonder dit veld
 * eindigde een mislukte run als een leeg antwoord zonder enig spoor. */
export interface AskAnswer {
  answer: string;
  usage: AskUsage | null;
  failure?: AiFailure;
}

/** Eén bron van waarheid voor de query-opties per taaktype (#154): het koude
 * pad én de warme pool bouwen hun opties hier — de contract-test in
 * ai.test.ts bewaakt dat warm en koud nooit uiteenlopen. systemPrompt en
 * includePartialMessages liggen bij de SDK vast op het moment van `query()`
 * (initialize-controlbericht resp. spawn-flag) en zijn daarom onderdeel van
 * de warme-pool-signatuur. */
export function buildQueryOptions(input: {
  task: Task;
  systemPrompt?: string;
  includePartialMessages: boolean;
  controller: AbortController;
  onBrainStep?: (step: string) => void;
  /** Model-sweep (#174): expliciete modeloverride voor deze call — alleen
   * gebruikt door benchmarkruns (AskOptions.Model in rb-api). Onbekend
   * volgt de gewone SDK-degradatie (query() geeft een fout terug, gevangen
   * door de aanroeper — geen aparte validatie hier); zonder override
   * ongewijzigd gedrag (MODEL[task]). */
  model?: string;
  /** Afnemer van de stderr van het Claude-subprocess (#300).
   *
   * VERPLICHT, EN DAT IS DE FIX. Deze optie is bij de SDK geen afnemer maar een
   * SCHAKELAAR: `stdio:["pipe","pipe", options.stderr ? "pipe" : "ignore"]`.
   * Zonder haar wordt de stroom niet doorgegeven-maar-genegeerd, ze wordt
   * WEGGEGOOID bij de spawn. `extractWithTool` zette de optie wel en
   * `buildQueryOptions` niet, dus de faaldiagnostiek van #281 was compleet op
   * het extract-pad en half op /ask — inclusief het agentic pad.
   *
   * Bewust een verplichte parameter in plaats van een optionele met een
   * bron-grep-test erop: zo is het de TYPECHECKER die elke nieuwe call-site
   * dwingt een staart te leveren. Een poort die je kunt vergeten is geen poort
   * (#292), en een structurele test op de aanroepvorm heeft altijd omzeilingen
   * (#295-review). Hier kan het per constructie niet fout gaan. */
  stderr: (data: string) => void;
}): Options {
  const {
    task, systemPrompt, includePartialMessages, controller, onBrainStep, model, stderr,
  } = input;
  const research = task === "research";
  const agentic = task === "agentic";
  return {
    model: model ?? MODEL[task],
    maxTurns: research ? RESEARCH_MAX_TURNS : agentic ? AGENTIC_MAX_TURNS : 1,
    // Basis-toolset (built-ins): leeg voor cheap/hard/agentic (agentic krijgt
    // zijn tools via de MCP-server hieronder), alleen de web-tools voor
    // research.
    tools: research ? RESEARCH_TOOLS : [],
    ...(research
      ? {
          // Headless: web-tools vooraf goedkeuren; al het overige wordt
          // geweigerd in plaats van op een prompt te blijven hangen.
          allowedTools: RESEARCH_TOOLS,
          permissionMode: "dontAsk" as const,
        }
      : {}),
    ...(agentic
      ? {
          // In-process brein-tools (§2.4): alléén mcp__brain__* in de
          // allowlist, headless — al het overige wordt geweigerd.
          mcpServers: { [BRAIN_SERVER_NAME]: createBrainMcpServer(onBrainStep) },
          allowedTools: brainToolAllowlist(),
          permissionMode: "dontAsk" as const,
        }
      : {}),
    abortController: controller,
    ...(systemPrompt ? { systemPrompt } : {}),
    // Streaming (#31): partial messages alleen aanzetten als er een
    // delta-afnemer is — anders blijft het berichtenverkeer zoals het was.
    ...(includePartialMessages ? { includePartialMessages: true } : {}),
    stderr,
  };
}

/** De warme-boot-opties zijn per constructie het koude cheap-pad met
 * dezelfde signatuur — apart benoemd zodat de contract-test elke toekomstige
 * special-casing van het warme pad ziet.
 *
 * `stderr` komt van buiten omdat elke sessie een EIGEN staart hoort te hebben
 * (#300): het warm/koud-contract van #154 gaat over de opties waarmee het
 * subprocess gespawnd en de API-call gedaan wordt, en die blijven byte-gelijk —
 * de callback is, net als `abortController`, per sessie uniek en per definitie
 * geen onderdeel van die gelijkheid. Zou hij dat wél zijn, dan zouden warm en
 * koud in dezelfde buffer schrijven en was elke staart bij een gelijktijdige
 * call onbruikbaar. */
export function warmBootOptions(
  sig: WarmSignature,
  controller: AbortController,
  stderr: (data: string) => void,
): Options {
  return buildQueryOptions({
    task: "cheap",
    systemPrompt: sig.systemPrompt,
    includePartialMessages: sig.includePartialMessages,
    controller,
    stderr,
  });
}

function bootWarmCheapSession(sig: WarmSignature): WarmBootHandle {
  const controller = new AbortController();
  const input = pushableInput<ReturnType<typeof buildUserMessage>>();
  // Eigen staart per warme sessie (#300), aangelegd vóór de spawn zodat ook
  // wat het subprocess tijdens de boot al roept erin belandt — precies de
  // uitvoer die verklaart waarom een claim later dood blijkt.
  const stderr = new StderrTail();
  const q = query({
    prompt: input.iterable as Parameters<typeof query>[0]["prompt"],
    options: warmBootOptions(sig, controller, (data: string) => stderr.append(data)),
  });
  return {
    messages: q,
    stderr,
    push: (m) => input.push(m as ReturnType<typeof buildUserMessage>),
    endInput: () => input.end(),
    kill: () => {
      controller.abort();
      (q as { close?: () => void }).close?.();
    },
  };
}

/** Warme-sessie-pool (#154), signaal-gedreven — zie warmpool.ts voor het
 * ontwerp en de grenzen. Kill-switch: AI_WARM_POOL=0 (default AAN: zonder
 * /prewarm-signaal boot er toch niets, en de degradatie naar koud is
 * transparant — de schakelaar is er voor ops-noodgevallen). */
export const warmPool = new WarmPool({
  boot: bootWarmCheapSession,
  enabled: !["0", "false", "off"].includes(
    (process.env.AI_WARM_POOL ?? "1").toLowerCase(),
  ),
  ttlMs: (() => {
    const parsed = Number.parseInt(process.env.AI_WARM_TTL_MS ?? "", 10);
    return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 600_000; // 10 min
  })(),
});

/** Voortgangsvlag voor de leeslus: heeft de sessie echt output geleverd
 * (assistant/result/tekst-delta)? Bij een warme sessie die dood bleek bij de
 * claim blijft dit false — dan is er gegarandeerd geen API-call gedaan en
 * mag ai.ts transparant koud opnieuw starten. */
export interface CollectProgress {
  sawOutput: boolean;
}

/** Gedeelde leeslus over de SDK-berichten (koud én warm — #154).
 *
 * De Agent SDK levert dezelfde tekst twee keer: als streaming 'assistant'-
 * berichten én als afsluitend 'result'-bericht. Tel ze NIET op — verzamel
 * apart en geef het 'result' terug; val terug op assistant-tekst zonder
 * result. Het result-bericht draagt ook de echte token-usage (#121). */
export async function collectAnswer(
  messages: AsyncIterable<unknown>,
  onDelta?: (text: string) => void | Promise<void>,
  progress?: CollectProgress,
  retries?: RetryTracker,
): Promise<AskAnswer> {
  let assistantText = "";
  let resultText = "";
  let usage: AskUsage | null = null;
  let failure: AiFailure | undefined;
  for await (const message of messages) {
    // Mislukte run (#281): de SDK meldt die met een result-bericht, niet met
    // een exception. Vóór deze regel viel zo'n run stil terug op een leeg
    // antwoord — de directe oorzaak van 22 spoorloze 5xx'en. `api_retry`-
    // berichten gaan naar de tracker: die maken zichtbaar dat de SDK intern
    // al minutenlang op een 429/529 zat te wachten.
    retries?.observe(message);
    failure = resultFailure(message) ?? failure;
    const m = message as {
      type: string;
      text?: string;
      result?: string;
      usage?: unknown;
      message?: { content?: Array<{ type: string; text?: string }> };
      event?: { type?: string; delta?: { type?: string; text?: string } };
    };
    if (m.type === "stream_event") {
      // Partial-message-event: alleen echte text-deltas doorgeven; het
      // volledige antwoord komt daarnaast gewoon als assistant/result
      // binnen (dus hier NIET aan assistantText/resultText toevoegen).
      const ev = m.event;
      if (
        ev?.type === "content_block_delta" &&
        ev.delta?.type === "text_delta" &&
        ev.delta.text
      ) {
        if (progress) progress.sawOutput = true;
        if (onDelta) await onDelta(ev.delta.text);
      }
    } else if (m.type === "assistant" && Array.isArray(m.message?.content)) {
      if (progress) progress.sawOutput = true;
      for (const block of m.message.content) {
        if (block.type === "text" && block.text) assistantText += block.text;
      }
    } else if (m.type === "text" && m.text) {
      if (progress) progress.sawOutput = true;
      assistantText += m.text;
    } else if (m.type === "result") {
      if (progress) progress.sawOutput = true;
      if (m.result) resultText += m.result;
      usage = usageFromSdk(m.usage) ?? usage;
    }
  }
  return {
    answer: (resultText || assistantText).trim(),
    usage,
    ...(failure ? { failure } : {}),
  };
}

/** De afloop van één uitgelezen /ask-run: gedeeld door het koude pad én de
 * warme claim (#300).
 *
 * WAAROM DIT GEDEELD MOET ZIJN. Het koude pad hád deze poort al — `if
 * (res.failure && !res.answer) throw new AiRunError(...)` — en de warme claim
 * niet: die deed `if (progress.sawOutput) return res` en gaf een mislukte run
 * dus terug als een 200 met een leeg antwoord. Exact dezelfde run leverde koud
 * een AiRunError met reden op en warm niets. Dat is dezelfde soort stilte als
 * #281 zelf: niet een verkeerde melding, maar géén melding, langs precies het
 * pad dat een gedragstest op het andere pad niet ziet. Eén functie voor beide
 * uitgangen maakt die drift per constructie onmogelijk (zelfde reden als
 * {@link decideExtractOutcome} op het extract-pad).
 *
 * De stderr-staart komt van de aanroeper omdat alléén die weet WELKE sessie
 * deze run draaide — zie de attributie-regel in {@link askClaude}. */
function finishAskRun(
  res: AskAnswer,
  stderr: StderrTail,
  retries: RetryTracker,
): AskAnswer {
  if (!res.failure) return res;
  const failure = withStderrDigest(withRetries(res.failure, retries), stderr);
  // Mislukte run ZONDER antwoord (#281): de SDK gooit hier niet, dus dit
  // eindigde voorheen als een 200 met een leeg antwoord — rb-api degradeerde
  // dan wel correct naar null, maar de reden was nergens te zien. Met een
  // antwoord erbij is de run bruikbaar en telt de fout niet als uitval.
  if (!res.answer) throw new AiRunError(failure);
  return { ...res, failure };
}

/** Stuur één prompt (optioneel met afbeeldingen) naar Claude.
 *
 * Met `onDelta` (#31, streaming) levert de Agent SDK naast de gewone
 * berichten ook partial-message-events (`includePartialMessages`); elke
 * text-delta gaat direct naar de callback zodat de aanroeper het antwoord
 * woord-voor-woord kan doorsturen. De return-waarde blijft in beide gevallen
 * het volledige eindantwoord — de niet-streamende route verandert niet.
 *
 * Capaciteit (#155): elke run verwerft eerst een permit in de globale
 * semaphore (agentic weegt 2); boven de cap wacht hij kort in de rij en
 * daarna bubbelt een ConcurrencyLimitError naar server.ts (429).
 *
 * Warme pool (#154): een cheap-call waarvan de sessie-opties byte-gelijk
 * zijn aan een voorverwarmde sessie krijgt die sessie (subprocess-boot al
 * betaald); in alle andere gevallen — en bij een dood gebleken warme sessie
 * — start hij transparant koud. Eén sessie = één call, nooit hergebruik.
 * Een `model`-override (#174) slaat de warme pool altijd over — de
 * voorverwarmde sessie is altijd op MODEL.cheap gebootstrapt, dus een claim
 * zou de override stilzwijgend negeren; zie de guard hieronder. */
export async function askClaude(opts: {
  prompt: string;
  system?: string;
  task?: Task;
  images?: AskImage[];
  onDelta?: (text: string) => void | Promise<void>;
  /** #107: ontvangt per brein-tool-call één regel (toolnaam + argumenten);
   * alleen relevant bij task="agentic" — server.ts geeft ze als `steps`
   * terug zodat rb-api ze in AskTrace.BrainSteps kan vastleggen. */
  onBrainStep?: (step: string) => void;
  signal?: AbortSignal;
  /** Model-sweep (#174): expliciete modeloverride voor déze call — alleen
   * gezet door een benchmarkrun (rb-api's AskOptions.Model reist hier
   * ongewijzigd doorheen). Undefined = het bestaande gedrag (MODEL[task]). */
  model?: string;
  /** Test-seam (#300): de SDK-aanroep van het KOUDE pad. Productie laat dit weg
   * en krijgt `query`; zie {@link AskQueryRunner}. */
  runQuery?: AskQueryRunner;
  /** Test-seam (#300): de warme pool. Productie laat dit weg en krijgt de
   * module-singleton {@link warmPool}.
   *
   * Waarom deze naad er nu is: het warme pad kon tot dusver alleen met een echt
   * SDK-subprocess doorlopen worden, dus geen enkele test kwam er ooit — en
   * precies daar zat het gat dat deze PR opruimt (een warme claim die met een
   * fout-result eindigde gaf een leeg antwoord terug waar het koude pad een
   * AiRunError gooide). Een gedragstest kan per definitie niet zien dat er een
   * TWEEDE pad bestaat dat ze nooit aanroept (#292); met deze naad roept ze het
   * wél aan. */
  pool?: WarmPool;
}): Promise<AskAnswer> {
  const {
    prompt, system, task = "cheap", images = [], onDelta, onBrainStep, signal, model,
    runQuery = query as unknown as AskQueryRunner,
    pool = warmPool,
  } = opts;
  const research = task === "research";
  const agentic = task === "agentic";

  // Server-side addendum per taak (nooit door de aanroeper te omzeilen):
  // research krijgt het bronnen-contract, agentic de brein-voorrangregels.
  const addendum = research ? RESEARCH_CONTRACT : agentic ? AGENT_ADDENDUM : undefined;
  const systemPrompt = addendum
    ? [system, addendum].filter(Boolean).join("\n\n")
    : system;

  // Eén AbortController voor álle taken (review #31): server.ts koppelt er
  // de client-verbinding aan (`signal`), zodat een weggelopen client de
  // Claude-call afbreekt in plaats van hem op abonnementskosten af te laten
  // maken. De harde timeout geldt alleen voor de multi-turn-taken (research,
  // agentic): de andere taken zijn één beurt en kennen dat risico niet.
  const timeoutMs = research
    ? RESEARCH_TIMEOUT_MS
    : agentic
      ? AGENTIC_TIMEOUT_MS
      : undefined;
  const controller = new AbortController();
  let timedOut = false;
  const timer = timeoutMs
    ? setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, timeoutMs)
    : undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const includePartialMessages = Boolean(onDelta);
  // SDK-interne retries (#281): dezelfde meting als op het extract-pad, zodat
  // een /ask-timeout die in werkelijkheid een aanhoudende API-fout was ook hier
  // als zodanig in de log belandt.
  const retries = new RetryTracker();
  // Stderr van het subprocess (#300). De KOUDE sessie krijgt deze buffer; een
  // warme claim brengt zijn eigen mee (aangelegd bij zijn boot).
  const coldStderr = new StderrTail();
  /** Welke staart de uitval van DEZE aanroep verklaart.
   *
   * ATTRIBUTIE IS HIER HET HELE PUNT, en fout toewijzen is erger dan geen
   * staart: dan diagnosticeer je met stelligheid de verkeerde sessie. De regel
   * is simpel omdat de ontwerpgrens van de pool simpel is (één sessie = één
   * call): de staart van de sessie die de run daadwerkelijk draaide, en niets
   * anders. Valt een warme claim dood terug op koud, dan wordt dit veld
   * expliciet teruggezet — de warme regels horen bij de warme sessie en zijn
   * daar al gemeld, en de koude herstart is een ándere sessie. */
  let failureStderr = coldStderr;
  let release: (() => void) | undefined;
  let unhookWarmAbort: (() => void) | undefined;
  const progress: CollectProgress = { sawOutput: false };
  try {
    // Permit vóór elke sessie-start (koud én warm-claim) — de voorverwarmde
    // boot zelf telt niet (idle, geen API-call; de pool-cap begrenst die al).
    // Interactief (#279, de default): dit pad draagt /ask, dus het mag tot aan
    // de volle cap en haalt wachtend mining-werk in.
    release = await aiSemaphore.acquire(agentic ? 2 : 1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
      priority: "interactive",
    });

    if (task === "cheap" && !model && pool.isEnabled()) {
      const sig: WarmSignature = { systemPrompt, includePartialMessages };
      pool.observe(sig);
      const claimed = controller.signal.aborted ? null : pool.claim(sig);
      if (claimed) {
        // Vanaf hier draait de WARME sessie; haar staart verklaart wat er
        // misgaat tot het moment dat we eventueel koud herstarten.
        failureStderr = claimed.stderr;
        const killWarm = () => claimed.kill();
        controller.signal.addEventListener("abort", killWarm, { once: true });
        unhookWarmAbort = () =>
          controller.signal.removeEventListener("abort", killWarm);
        try {
          claimed.send(buildUserMessage(prompt, images));
          const res = await collectAnswer(claimed.messages(), onDelta, progress, retries);
          if (progress.sawOutput) return finishAskRun(res, claimed.stderr, retries);
          // Sessie eindigde zonder één output-bericht: subprocess was dood
          // bij de claim — er is geen API-call gedaan, dus koud is veilig.
          // Wát het subprocess nog geroepen heeft vóór het omviel staat in zijn
          // eigen staart, en hoort HIER gemeld te worden (#300): dit is het
          // enige moment waarop die uitvoer nog aan de juiste sessie hangt.
          logEvent("warmpool_fallback", {
            stage: "dood bij claim",
            stderr: stderrDigestLine(claimed.stderr) || undefined,
          });
        } catch (e) {
          if (progress.sawOutput || controller.signal.aborted || timedOut) throw e;
          // Door dezelfde poort als elk ander faalpad sinds #281 (#292): een
          // rauwe `String(e)` uit de SDK kan een auth-header of tokenfragment
          // dragen, en levert bovendien geen classificatie op. `describeThrown`
          // redacteert, kapt af én zegt WELKE knop dit is (spawn/auth/api_error)
          // — precies het onderscheid waar de warme pool om vraagt als hij
          // stelselmatig omvalt.
          const failure = describeThrown(e);
          logEvent("warmpool_fallback", {
            stage: "faalde vóór output",
            reason: failure.reason,
            detail: failure.detail,
            stderr: stderrDigestLine(claimed.stderr) || undefined,
          });
        }
        pool.noteDeadClaim();
        progress.sawOutput = false;
        // De warme sessie is op en haar uitvoer is hierboven gemeld. Wat nu
        // volgt is een VERSE sessie; die mag nooit verklaard worden met de
        // regels van de dode (zie `failureStderr`).
        failureStderr = coldStderr;
      }
    }

    const options = buildQueryOptions({
      task,
      systemPrompt,
      includePartialMessages,
      controller,
      onBrainStep,
      model,
      stderr: (data: string) => coldStderr.append(data),
    });
    const arg = {
      prompt: images.length > 0 ? userMessage(prompt, images) : prompt,
      options,
    };
    const res = await collectAnswer(runQuery(arg), onDelta, progress, retries);
    return finishAskRun(res, coldStderr, retries);
  } catch (e) {
    // Capaciteitsgrens (#155) onvertaald doorgeven: server.ts maakt er een
    // 429 met machine-leesbare reden van.
    if (e instanceof ConcurrencyLimitError) throw e;
    // AL geclassificeerd door `finishAskRun` (#300-review): niet opnieuw door
    // `describeThrown` halen. Dat zou de reden HERclassificeren op de tekst van
    // de al-gebouwde melding — `max_turns`/`permission_denied` werden zo
    // `unknown` (`describeThrown` kent geen AiRunError-special-case, alleen
    // `failureOf` doet dat), of toevallig `spawn` omdat "exited with code 137"
    // uit de stderr-staart in de message stond. Plus: de stderr-digest werd een
    // TWEEDE keer aangeplakt en er kwam een `AiRunError:`-prefix als ruis bij.
    // De reason is precies de knop die de beheerder afleest — dit is #281
    // opnieuw. De enige AiRunError die hier aankomt komt uit `finishAskRun`;
    // `collectAnswer`/de warme claim gooien rauwe fouten.
    if (e instanceof AiRunError) throw e;
    // Timeout en client-abort herkenbaar maken voor de aanroeper (run_log);
    // overige fouten ongewijzigd doorgeven — server.ts vertaalt ze naar een
    // nette 500 of een error-frame.
    if (timedOut && timeoutMs) {
      throw new AiRunError(withStderrDigest(
        withRetries(
          {
            reason: "timeout",
            detail: `${task}-call afgebroken na ${timeoutMs / 1000}s (harde timeout)`,
          },
          retries,
        ),
        failureStderr,
      ));
    }
    if (controller.signal.aborted) {
      // Bewust ZONDER stderr-staart: de client liep weg, er is niets aan de
      // machine te diagnosticeren, en dit is het pad dat het vaakst vuurt —
      // diagnostiek die niets verklaart is hier alleen ruis en lekoppervlak.
      throw new AiRunError({
        reason: "aborted",
        detail: "aanroep afgebroken: client heeft de verbinding gesloten",
      });
    }
    // Overige fouten geclassificeerd doorgeven (#281): de melding blijft
    // leesbaar (AiRunError.message = "reden: detail") en server.ts kan de reden
    // eruit lezen in plaats van hem uit een string te moeten raden. De staart
    // van de sessie die dit veroorzaakte gaat mee (#300) — dit is het pad waar
    // een omgevallen subprocess uitkomt, en juist daar was `reason: spawn`
    // voorheen alles wat er stond.
    throw new AiRunError(withStderrDigest(withRetries(describeThrown(e), retries), failureStderr));
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    unhookWarmAbort?.();
    release?.();
  }
}
